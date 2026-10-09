// Pure helpers: a tool's inputSchema → form fields with defaults and validation → the arguments to send; and the
// shape of a result (from outputSchema when the tool is typed) for the result view. No React here.

import type { JsonSchema } from "./types";

export type FieldType = "string" | "integer" | "number" | "boolean" | "array" | "object" | "any";

export interface Field {
  name: string;
  type: FieldType;
  /** array: the item type. */
  items?: FieldType;
  nullable: boolean;
  required: boolean;
  description?: string;
  /** The schema default (undefined when none; null when the default is null). */
  default?: unknown;
  /** Choices: from `enum`, or parsed from a description like "list | set | remove". */
  options?: string[];
  min?: number;
  max?: number;
  /** A number range parsed from the description, e.g. "(1-500, default 100)". */
  hintRange?: [number, number];
  /** True for `game`: every game tool takes it; the control center fills it from the selected game. */
  isGame: boolean;
}

function typeOf(s: JsonSchema | undefined): { type: FieldType; nullable: boolean } {
  if (!s) return { type: "any", nullable: true };
  const raw = Array.isArray(s.type) ? s.type : s.type ? [s.type] : [];
  const nullable = raw.includes("null");
  const t = raw.find((x) => x !== "null");
  if (t === "string" || t === "integer" || t === "number" || t === "boolean" || t === "array" || t === "object") return { type: t, nullable };
  if (s.properties) return { type: "object", nullable };
  if (s.items) return { type: "array", nullable };
  return { type: "any", nullable: true };
}

/** "list | set | remove", "set: struct | props | dict | list", "changes (most first) | recent | unit" → the choices. */
export function optionsFromDescription(desc: string | undefined): string[] | undefined {
  if (!desc) return undefined;
  const head = desc.split(/[.;]\s/)[0].replace(/^[a-z ,]+:\s*/i, "").trim();
  if (!head.includes(" | ")) return undefined;
  const parts = head.split(" | ").map((p) => p.trim());
  if (parts.length < 2 || parts.length > 12) return undefined;
  const words = parts.map((p) => p.replace(/\s*\([^)]*\)\s*$/, "").trim());
  if (!words.every((w) => /^[\w.+-]+$/.test(w))) return undefined;
  return words;
}

function rangeFromDescription(desc: string | undefined): [number, number] | undefined {
  const m = desc && /\((-?\d+(?:\.\d+)?)\s*-\s*(-?\d+(?:\.\d+)?)(?:,|\))/.exec(desc);
  return m ? [Number(m[1]), Number(m[2])] : undefined;
}

export function fieldsOf(schema: JsonSchema | null | undefined): Field[] {
  const props = schema?.properties ?? {};
  const required = new Set(schema?.required ?? []);
  return Object.entries(props).map(([name, s]) => {
    const { type, nullable } = typeOf(s);
    const enumOpts = s.enum?.map(String);
    return {
      name, type, nullable, required: required.has(name),
      items: s.items ? typeOf(s.items).type : undefined,
      description: s.description,
      default: "default" in s ? s.default : undefined,
      options: enumOpts ?? (type === "string" ? optionsFromDescription(s.description) : undefined),
      min: s.minimum, max: s.maximum,
      hintRange: type === "integer" || type === "number" ? rangeFromDescription(s.description) : undefined,
      isGame: name === "game",
    };
  });
}

/** Form values: strings for text/number inputs, booleans for toggles, strings for arrays (one item per line) and objects (JSON). */
export type FormValues = Record<string, string | boolean | undefined>;

export function initialValues(fields: Field[], presets?: Record<string, unknown>): FormValues {
  const v: FormValues = {};
  for (const f of fields) {
    const preset = presets?.[f.name];
    const d = preset !== undefined ? preset : f.default;
    if (f.type === "boolean") v[f.name] = typeof d === "boolean" ? d : false;
    else if (d === undefined || d === null) v[f.name] = "";
    else if (f.type === "array") v[f.name] = Array.isArray(d) ? d.map(String).join("\n") : String(d);
    else if (f.type === "object" || f.type === "any") v[f.name] = typeof d === "string" ? d : JSON.stringify(d, null, 2);
    else v[f.name] = String(d);
  }
  return v;
}

export interface Validation { errors: Record<string, string>; args: Record<string, unknown> }

/** Validate and convert; only fields that differ from "unset" go into the arguments. */
export function validate(fields: Field[], values: FormValues): Validation {
  const errors: Record<string, string> = {};
  const args: Record<string, unknown> = {};
  for (const f of fields) {
    const raw = values[f.name];
    if (f.type === "boolean") {
      const b = !!raw;
      // Send it when it differs from the default (or when there is no default), so the call stays minimal.
      if (f.default === undefined || b !== f.default) args[f.name] = b;
      continue;
    }
    const s = typeof raw === "string" ? raw.trim() : "";
    if (!s) {
      if (f.required) errors[f.name] = "Required";
      continue;
    }
    switch (f.type) {
      case "integer":
      case "number": {
        const n = Number(s);
        if (!Number.isFinite(n)) { errors[f.name] = "Not a number"; break; }
        if (f.type === "integer" && !Number.isInteger(n)) { errors[f.name] = "Must be a whole number"; break; }
        const lo = f.min ?? f.hintRange?.[0], hi = f.max ?? f.hintRange?.[1];
        if (lo !== undefined && n < lo) { errors[f.name] = `At least ${lo}`; break; }
        if (hi !== undefined && n > hi) { errors[f.name] = `At most ${hi}`; break; }
        args[f.name] = n;
        break;
      }
      case "array": {
        const items = s.split(/\r?\n|,/).map((x) => x.trim()).filter(Boolean);
        if (f.items === "integer" || f.items === "number") {
          const nums = items.map(Number);
          if (nums.some((n) => !Number.isFinite(n))) { errors[f.name] = "One item per line, numbers only"; break; }
          args[f.name] = nums;
        } else args[f.name] = items;
        break;
      }
      case "object":
      case "any": {
        if (/^[[{"]/.test(s) || /^(true|false|null|-?\d)/.test(s)) {
          try { args[f.name] = JSON.parse(s); } catch { errors[f.name] = "Not valid JSON"; }
        } else args[f.name] = s;
        break;
      }
      default:
        if (f.options && !f.options.includes(s) && !f.description?.includes("e.g.")) {
          // Options parsed from prose are hints, never a hard constraint.
        }
        args[f.name] = s;
    }
  }
  return { errors, args };
}

/** One line for a form value, for history rows and tooltips. */
export function argsLine(args: Record<string, unknown>): string {
  const parts = Object.entries(args).map(([k, v]) => `${k}=${typeof v === "string" ? v : JSON.stringify(v)}`);
  return parts.length ? parts.join(" ") : "no arguments";
}

// ── Output schema → property order and descriptions ─────────────────

export interface PropInfo { order: string[]; descriptions: Record<string, string> }

/** The top-level properties of an output schema, in schema order, with descriptions when present. */
export function propInfo(schema: JsonSchema | null | undefined): PropInfo | undefined {
  if (!schema?.properties) return undefined;
  const order = Object.keys(schema.properties);
  const descriptions: Record<string, string> = {};
  for (const [k, s] of Object.entries(schema.properties)) if (s.description) descriptions[k] = s.description;
  return { order, descriptions };
}

/** The schema of a nested property / array item, when the parent is known. */
export function subSchema(schema: JsonSchema | undefined, key: string | number): JsonSchema | undefined {
  if (!schema) return undefined;
  if (typeof key === "number") return schema.items;
  return schema.properties?.[key];
}
