// Walker paths and their C# twins, split into segments so breadcrumbs, the code generator and the
// tree can reason about parents without re-deriving anything the server already decided.
//
//   GameController.Player.GetComponent<Life>().Health.Current
//   -> ["GameController", "Player", "GetComponent<Life>()", "Health", "Current"]
//   GameController.Player.Stats["MaximumLife"]
//   -> ["GameController", "Player", "Stats", "[\"MaximumLife\"]"]
//
// C# accessors split the same way, each segment keeping its own operator:
//   GameController?.Player?.Stats?[GameStat.MaximumLife]
//   -> ["GameController", "?.Player", "?.Stats", "?[GameStat.MaximumLife]"]

/** Split a walker path into segments. Indexers become their own segment ("[2]", "[\"key\"]"). */
export function splitPath(path: string): string[] {
  const out: string[] = [];
  let cur = "";
  let depth = 0; // inside <...> or (...)
  let quote: string | null = null;
  for (let i = 0; i < path.length; i++) {
    const c = path[i];
    if (quote) {
      cur += c;
      if (c === quote && path[i - 1] !== "\\") quote = null;
      continue;
    }
    if (c === '"' || c === "'") { cur += c; quote = c; continue; }
    if (c === "<" || c === "(") { depth++; cur += c; continue; }
    if (c === ">" || c === ")") { depth--; cur += c; continue; }
    if (depth === 0 && c === ".") { if (cur) out.push(cur); cur = ""; continue; }
    if (depth === 0 && c === "[") { if (cur) out.push(cur); cur = "["; continue; }
    cur += c;
  }
  if (cur) out.push(cur);
  return out;
}

/** Join path segments back into a walker path. */
export function joinPath(segments: string[]): string {
  let s = "";
  for (const seg of segments) s += seg.startsWith("[") || !s ? seg : `.${seg}`;
  return s;
}

/** Every prefix of a path, shortest first: the ancestors plus the path itself. */
export function ancestorsOf(path: string): string[] {
  const segs = splitPath(path);
  return segs.map((_, i) => joinPath(segs.slice(0, i + 1)));
}

export function parentPath(path: string): string | undefined {
  const segs = splitPath(path);
  return segs.length > 1 ? joinPath(segs.slice(0, -1)) : undefined;
}

/**
 * Split a C# accessor into segments that line up 1:1 with splitPath() of the same node's path.
 * Each segment keeps its leading operator ("?.", ".", "?[", "[").
 */
export function splitCSharp(csharp: string): string[] {
  const out: string[] = [];
  let cur = "";
  let depth = 0;
  let quote: string | null = null;
  const flush = () => { if (cur) out.push(cur); cur = ""; };
  for (let i = 0; i < csharp.length; i++) {
    const c = csharp[i];
    if (quote) { cur += c; if (c === quote && csharp[i - 1] !== "\\") quote = null; continue; }
    if (c === '"' || c === "'") { cur += c; quote = c; continue; }
    if (c === "<" || c === "(") { depth++; cur += c; continue; }
    if (c === ">" || c === ")") { depth--; cur += c; continue; }
    if (depth === 0 && c === "?" && (csharp[i + 1] === "." || csharp[i + 1] === "[")) { flush(); cur = "?"; continue; }
    if (depth === 0 && (c === "." || c === "[") && cur !== "?") { flush(); cur = c; continue; }
    cur += c;
  }
  flush();
  return out;
}

/** "GetComponent<Life>()" -> "Life"; "[\"MaximumLife\"]" -> "MaximumLife"; "[2]" -> "2"; "Player" -> "Player". */
export function segmentLabel(seg: string): string {
  const gc = /^GetComponent<([^>]+)>\(\)$/.exec(seg);
  if (gc) return gc[1];
  if (seg.startsWith("[")) return seg.slice(1, -1).replace(/^"(.*)"$/, "$1");
  return seg;
}

/** Walker path of a child under its parent, given the child's display name ("Health", "[2]", "[Key]"). */
export function childPath(parent: string, name: string): string {
  if (!name.startsWith("[")) return `${parent}.${name}`;
  // Dictionary entries are shown as [Key]; the walker wants a quoted string key. List items stay [i].
  const inner = name.slice(1, -1);
  return /^\d+$/.test(inner) || inner.startsWith('"') ? `${parent}${name}` : `${parent}["${inner}"]`;
}

/** Short, legible display of a path for titles and toasts: last three segments. */
export function shortPath(path: string, keep = 3): string {
  const segs = splitPath(path);
  return segs.length <= keep ? path : `…${joinPath(segs.slice(-keep)).replace(/^(?=\[)/, "")}`;
}
