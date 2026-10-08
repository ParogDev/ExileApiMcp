// Flattening the cached entries into the visible tree rows, plus resolving a row id back to its node.

import type { Entry, Snapshot } from "./store";
import type { ExploreChild, ExploreComponent, ExploreNode } from "./types";

export type Row =
  | { t: "node"; id: string; depth: number; parent: string; child: ExploreChild; path?: string; expandable: boolean; expanded: boolean; loading: boolean; component?: boolean }
  | { t: "group"; id: string; depth: number; label: string; count: number }
  | { t: "more"; id: string; depth: number; parent: string; remaining: number; loading: boolean }
  | { t: "skipped"; id: string; depth: number; parent: string; members: string[]; reason: string; loading: boolean }
  | { t: "error"; id: string; depth: number; parent: string; message: string }
  | { t: "empty"; id: string; depth: number; parent: string }
  | { t: "loading"; id: string; depth: number };

const ACTION_ROWS: ReadonlySet<string> = new Set(["more", "skipped", "error", "empty", "loading", "components"]);

/** Rows a keyboard user can land on. */
export const FOCUSABLE: ReadonlySet<Row["t"]> = new Set(["node", "more", "skipped", "error"]);

export function rowId(parent: string, child: { name: string; path?: string }): string {
  return child.path ?? `${parent}#${child.name}`;
}

/** The freshest view of a listed child: its own loaded node (preview, kind, count) over how the parent listed it. */
export function liveChild(snap: Snapshot, child: ExploreChild): ExploreChild {
  const own = child.path ? snap.entries.get(child.path)?.node : undefined;
  if (!own) return child;
  return { ...child, kind: own.kind, preview: own.preview ?? child.preview, count: own.count ?? child.count, type: own.type ?? child.type, expandable: child.expandable || own.expandable };
}

export function hasMore(e: Entry): number {
  const total = e.page?.total ?? e.node?.count;
  return total !== undefined && e.children ? Math.max(0, total - e.children.length) : 0;
}

export function matches(c: ExploreChild, q: string): boolean {
  return c.name.toLowerCase().includes(q) || (c.preview?.toLowerCase().includes(q) ?? false) || (c.type?.toLowerCase().includes(q) ?? false);
}

export function flatten(snap: Snapshot): Row[] {
  const rows: Row[] = [];
  if (!snap.root) return rows;
  const q = snap.filter.trim().toLowerCase();

  const visit = (parent: string, depth: number): boolean => {
    const e = snap.entries.get(parent);
    if (!e) return false;
    if (!e.children) {
      if (e.loading) rows.push({ t: "loading", id: `${parent}#loading`, depth });
      // The root's own error is the tree's empty state (Tree.tsx), not a row.
      else if (e.error && parent !== snap.root) rows.push({ t: "error", id: `${parent}#error`, depth, parent, message: e.error });
      return false;
    }
    let any = false;

    if (e.components?.length) {
      const start = rows.length;
      if (!q) rows.push({ t: "group", id: `${parent}#components`, depth, label: "Components", count: e.components.length });
      let groupAny = false;
      for (const comp of e.components) {
        if (q && !comp.name.toLowerCase().includes(q)) continue;
        const child = componentAsChild(comp);
        const expanded = !!comp.path && snap.expanded.has(comp.path);
        rows.push({ t: "node", id: rowId(parent, comp), depth: depth + 1, parent, child, path: comp.path, component: true,
          expandable: !!comp.path, expanded, loading: expanded && !!snap.entries.get(comp.path!)?.loading });
        if (expanded) visit(comp.path!, depth + 2);
        groupAny = true;
      }
      if (q && !groupAny) rows.length = start;
      any ||= groupAny;
    }

    for (const raw of e.children) {
      const c = liveChild(snap, raw);
      const start = rows.length;
      const expandable = !!c.path && !!c.expandable && c.kind !== "blocked" && !c.error;
      const expanded = expandable && snap.expanded.has(c.path!);
      rows.push({ t: "node", id: rowId(parent, c), depth, parent, child: c, path: c.path, expandable, expanded,
        loading: expanded && !!snap.entries.get(c.path!)?.loading });
      const childMatch = expanded ? visit(c.path!, depth + 1) : false;
      if (q && !matches(c, q) && !childMatch) rows.length = start;
      else any = true;
    }

    if (e.children.length === 0 && !e.skipped?.members.length && !e.components?.length) rows.push({ t: "empty", id: `${parent}#empty`, depth, parent });
    if (e.skipped?.members.length) rows.push({ t: "skipped", id: `${parent}#skipped`, depth, parent, members: e.skipped.members, reason: e.skipped.reason, loading: !!e.loadingSkipped });
    const remaining = hasMore(e);
    if (remaining > 0) rows.push({ t: "more", id: `${parent}#more`, depth, parent, remaining, loading: !!e.loadingMore });
    return any;
  };

  visit(snap.root, 0);
  return rows;
}

function componentAsChild(c: ExploreComponent): ExploreChild {
  return { name: c.name, kind: "component", path: c.path, csharp: c.csharp, expandable: c.expandable, note: c.note, type: c.name };
}

export interface Resolved {
  id: string;
  name: string;
  node: ExploreNode;
  path?: string;
  parent?: string;
  entry?: Entry;
  component?: boolean;
  /** The row as the parent listed it (slowMs, error). */
  child?: ExploreChild;
}

/** A row id back to its data: the root, a child of a loaded entry, or a component. */
export function resolveRow(snap: Snapshot, id: string | undefined): Resolved | undefined {
  if (!id) return undefined;
  const hash = id.indexOf("#");
  if (hash >= 0) {
    const parent = id.slice(0, hash), name = id.slice(hash + 1);
    // Action rows (load more, skipped, error) belong to their parent: inspect that.
    if (ACTION_ROWS.has(name)) return resolveRow(snap, parent);
    const e = snap.entries.get(parent);
    const c = e?.children?.find((k) => k.name === name) ?? e?.components?.find((k) => k.name === name);
    if (!c) return undefined;
    const child = "kind" in c && c.kind !== "component" ? (c as ExploreChild) : componentAsChild(c as ExploreComponent);
    return { id, name, node: child, parent, child, component: child.kind === "component" };
  }
  const entry = snap.entries.get(id);
  for (const e of snap.entries.values()) {
    const c = e.children?.find((k) => k.path === id);
    if (c) {
      const live = liveChild(snap, c);
      return { id, name: c.name, node: { ...live, namespace: entry?.node?.namespace, declaredType: live.declaredType }, path: id, parent: e.path, entry, child: c };
    }
    const comp = e.components?.find((k) => k.path === id);
    if (comp) return { id, name: comp.name, node: entry?.node ?? componentAsChild(comp), path: id, parent: e.path, entry, component: true, child: componentAsChild(comp) };
  }
  if (entry?.node) {
    const segs = id.split(/\.(?![^<]*>)(?![^(]*\))/);
    return { id, name: segs[segs.length - 1], node: entry.node, path: id, entry };
  }
  return entry ? { id, name: id, node: { kind: "object", path: id }, path: id, entry } : undefined;
}
