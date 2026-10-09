// Settings: every loaded plugin's settings for the selected game, grouped, with the right control per kind, instant
// feedback (optimistic, flashes when the HUD confirms), undo of the last change, pull now / pulled n ago, and the
// permission settings handled apart: never through an MCP tool; standalone through the control center's own route
// behind an explicit confirmation; read-only inside Claude, with how to change them.

import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Banner, EmptyState, Skeleton, useNow } from "../../components";
import { Icon } from "../../icons";
import type { ControlStore, Snapshot } from "../store";
import { T } from "../tour/ids";
import { useTours } from "../tour/engine";
import type { PluginSettings, SettingNode } from "../types";
import { Badge, Button, Confirm, Kbd, Select, ShowMe, Slider, Switch, TextInput, agoShort } from "../ui";

const UNDO_MS = 8000;

const PERMISSION_TEXT: Record<string, string> = {
  AllowCSharpScripts: "Lets agents run arbitrary C# inside the HUD process (run_csharp). Read-only by convention, but it is code with the HUD's rights.",
  AllowHudInstrumentation: "Lets agents patch the HUD's own render loop for a few seconds (pipeline_trace, hud_health_report, profile_plugin). Never the game.",
  AllowPluginReload: "Lets agents recompile a source plugin in place (reload_plugin). The HUD pauses while it compiles.",
};

export function SettingsPage({ store, snap }: { store: ControlStore; snap: Snapshot }) {
  const st = store.settingsFor(snap.game);
  const now = useNow(1000);
  const tours = useTours();
  const [q, setQ] = useState("");
  const [pendingPermission, setPendingPermission] = useState<{ plugin: string; node: SettingNode; value: boolean }>();
  const [permBusy, setPermBusy] = useState(false);
  const noHud = store.gamesUp.length === 0 && snap.gamesAt !== undefined;

  // Pull once when the page opens with nothing pulled yet.
  useEffect(() => { if (!st.result && !st.loading && !st.error && snap.game) void store.pullSettings(); }, [snap.game]); // eslint-disable-line react-hooks/exhaustive-deps

  const query = q.trim().toLowerCase();
  const plugins = useMemo(() => {
    const all = st.result?.plugins ?? [];
    if (!query) return all;
    return all.map((p) => ({ ...p, settings: p.plugin.toLowerCase().includes(query) ? p.settings : p.settings.filter((s) => [s.label, s.path, s.group, s.description].some((x) => x?.toLowerCase().includes(query))) })).filter((p) => p.settings.length);
  }, [st.result, query]);

  const total = st.result?.plugins.reduce((n, p) => n + p.settings.length, 0) ?? 0;

  const change = (plugin: string, node: SettingNode, value: unknown) => {
    if (node.permission) {
      if (store.host.mode !== "standalone") return;
      setPendingPermission({ plugin, node, value: !!value });
      return;
    }
    void store.changeSetting(plugin, node, value);
  };

  const confirmPermission = async () => {
    if (!pendingPermission) return;
    setPermBusy(true);
    const ok = await store.changeSetting(pendingPermission.plugin, pendingPermission.node, pendingPermission.value, { viaRoute: true });
    setPermBusy(false);
    if (ok) store.toast("info", `${pendingPermission.node.label} is now ${pendingPermission.value ? "allowed" : "off"}`);
    setPendingPermission(undefined);
  };

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="relative min-w-0 flex-1 basis-48">
          <Icon name="search" className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-fg-3" />
          <input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder={total ? `Search ${total} settings` : "Search settings"} aria-label="Search settings" data-tour={T.settingsSearch}
            className="h-7 w-full rounded-md border border-line bg-surface pl-7 pr-2 text-[12.5px] placeholder:text-fg-3 hover:border-line-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" />
        </div>
        <span className="flex items-center gap-1.5 text-[11px] text-fg-3" title={st.pulledAt ? new Date(st.pulledAt).toLocaleTimeString() : undefined}>
          {st.loading ? <><Icon name="sync" className="spin size-3" />pulling…</> : st.pulledAt ? <><span className={`size-1.5 rounded-full ${now - st.pulledAt < 60_000 ? "bg-success" : "bg-warning"}`} />pulled {agoShort(now - st.pulledAt)}</> : "not pulled yet"}
        </span>
        <Button icon="sync" onClick={() => void store.pullSettings()} busy={st.loading} disabled={!snap.game} tour={T.settingsPull} title="Re-read every plugin's settings from the HUD (hud_settings)">Pull now</Button>
        <ShowMe onClick={() => tours.start("change-setting")} done={tours.done.has("change-setting")} size="md">Show me</ShowMe>
      </div>

      {noHud && !st.result && <Banner tone="warning" icon="offline" title="No HUD is up">Settings live in the running HUD. Start a HUD with "Whats An AI Bridge" enabled, then pull.</Banner>}
      {st.error && <Banner tone="danger" icon="warning" title="Could not pull the settings" action={<Button size="sm" onClick={() => void store.pullSettings()}>Retry</Button>}>{st.error}</Banner>}
      {!st.result && st.loading && <div className="rounded-card border border-line bg-surface p-3"><Skeleton kind="rows" count={6} /></div>}
      {st.result && plugins.length === 0 && <EmptyState icon="search" title="No setting matches" className="rounded-card border border-dashed border-line">Try a plugin name, a group or a label.</EmptyState>}

      {st.result && plugins.length > 0 && (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2 lg:items-start">
          {plugins.map((p, i) => <PluginCard key={p.plugin} plugin={p} index={i} pending={st.pending} mode={store.host.mode} onChange={change} first={i === 0} highlight={!!query} />)}
        </div>
      )}

      {snap.undo && now - snap.undo.at < UNDO_MS && <UndoBar store={store} label={snap.undo.label} previous={snap.undo.previous} />}

      {pendingPermission && (
        <Confirm title={`${pendingPermission.value ? "Allow" : "Turn off"} ${pendingPermission.node.label}?`} confirmLabel={pendingPermission.value ? "Allow it" : "Turn it off"} tone={pendingPermission.value ? "accent" : "primary"}
          onCancel={() => setPendingPermission(undefined)} onConfirm={() => void confirmPermission()} busy={permBusy} tour={T.settingsPermission}>
          <p>{PERMISSION_TEXT[pendingPermission.node.path] ?? pendingPermission.node.description ?? "This setting grants agents power in the HUD."}</p>
          <p className="mt-2">Permissions are never changed by an MCP tool: this goes through the control center's own route (<code className="font-code text-[11px]">POST /app/api/settings</code>), which only a person on this page can reach. {pendingPermission.value ? "Every agent connected to the server gets it as soon as you confirm." : "Agents lose it as soon as you confirm; a call in flight finishes."}</p>
        </Confirm>
      )}
    </div>
  );
}

function PluginCard({ plugin: p, index, pending, mode, onChange, first, highlight }: { plugin: PluginSettings; index: number; pending: ReadonlySet<string>; mode: "mcp-app" | "standalone"; onChange: (plugin: string, node: SettingNode, value: unknown) => void; first: boolean; highlight: boolean }) {
  const [open, setOpen] = useState(true);
  const groups = useMemo(() => {
    const m = new Map<string, SettingNode[]>();
    for (const s of p.settings) { const g = s.group ?? ""; if (!m.has(g)) m.set(g, []); m.get(g)!.push(s); }
    return [...m.entries()];
  }, [p.settings]);
  const permissions = p.settings.filter((s) => s.permission).length;
  let controlTour = false, permTour = false;
  return (
    <section className="rise min-w-0 rounded-card border border-line bg-surface" style={{ "--i": Math.min(index, 8) } as React.CSSProperties} data-tour={first ? T.settingsPlugin : undefined}>
      <header className="flex min-h-10 flex-wrap items-center gap-x-2 gap-y-1 px-3 py-1">
        <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open} className="grid size-6 place-items-center rounded-md text-fg-3 hover:bg-surface-3 hover:text-fg"><Icon name="chevron" className={`size-3.5 transition-transform ${open ? "" : "-rotate-90"}`} /></button>
        <Icon name="puzzle" className="size-3.5 text-fg-3" />
        <h2 className="min-w-[7rem] flex-1 truncate text-[13px] font-semibold">{p.plugin}</h2>
        {permissions > 0 && <Badge tone="warning" icon="lock" title="Has permission settings">{permissions} permission{permissions > 1 ? "s" : ""}</Badge>}
        <Badge tone={p.enabled ? "success" : "neutral"}>{p.enabled ? "enabled" : "disabled"}</Badge>
        <span className="tnum text-[10.5px] text-fg-3">{p.settings.length}</span>
      </header>
      {open && (
        <div className="border-t border-line">
          {groups.map(([g, nodes]) => (
            <div key={g}>
              {g && <div className="bg-surface-2/60 px-3 py-1 text-[10.5px] font-semibold uppercase tracking-wide text-fg-3">{g}</div>}
              <ul className="divide-y divide-line">
                {nodes.map((n) => {
                  const key = `${p.plugin}\u0000${n.path}`;
                  const tour = !controlTour && !n.permission && !n.readOnly && (n.kind === "range" || n.kind === "toggle") ? (controlTour = true, T.settingsControl) : !permTour && n.permission ? (permTour = true, T.settingsPermission) : undefined;
                  return <SettingRow key={n.path} node={n} pending={pending.has(key)} mode={mode} onChange={(v) => onChange(p.plugin, n, v)} tour={tour} highlight={highlight} />;
                })}
              </ul>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}

function SettingRow({ node: n, pending, mode, onChange, tour, highlight }: { node: SettingNode; pending: boolean; mode: "mcp-app" | "standalone"; onChange: (v: unknown) => void; tour?: string; highlight: boolean }) {
  const [flash, setFlash] = useState(0);
  const [wasPending, setWasPending] = useState(false);
  useEffect(() => { if (pending) setWasPending(true); else if (wasPending) { setWasPending(false); setFlash(Date.now()); } }, [pending]); // eslint-disable-line react-hooks/exhaustive-deps
  const disabled = pending || n.readOnly || (n.permission && mode !== "standalone");
  const [draft, setDraft] = useState<number | undefined>();
  const num = typeof n.value === "number" ? n.value : Number(n.value ?? 0);

  let control: ReactNode;
  switch (n.kind) {
    case "toggle":
      control = <Switch checked={!!n.value} onChange={onChange} label={n.label} disabled={disabled} tone={n.permission ? "accent" : "success"} />;
      break;
    case "range": {
      const min = n.min ?? 0, max = n.max ?? Math.max(100, num * 2);
      control = <div className="w-full min-w-0 sm:w-64"><Slider value={draft ?? num} min={min} max={max} onChange={setDraft} onCommit={(v) => { setDraft(undefined); if (v !== num) onChange(v); }} label={n.label} disabled={disabled} /></div>;
      break;
    }
    case "text":
      control = <TextInput value={String(n.value ?? "")} onChange={() => {}} onCommit={(v) => { if (v !== String(n.value ?? "")) onChange(v); }} label={n.label} disabled={disabled} className="sm:w-56" {...{ key: String(n.value) }} />;
      break;
    case "list":
      control = <Select value={String(n.value ?? "")} options={(n.options ?? [String(n.value ?? "")]).map((o) => ({ value: o }))} onChange={onChange} label={n.label} disabled={disabled} className="sm:w-56" />;
      break;
    case "color":
      control = <ColorControl value={String(n.value ?? "#ffffffff")} onChange={onChange} label={n.label} disabled={disabled} />;
      break;
    case "hotkey":
      control = <span className="flex items-center gap-1.5 text-[11.5px] text-fg-3"><Kbd>{String(n.value ?? "none")}</Kbd>set in game</span>;
      break;
    case "button":
      control = <Button size="sm" icon="play" onClick={() => onChange(true)} disabled={disabled} busy={pending}>Press</Button>;
      break;
    default:
      control = <code className="truncate font-code text-[11px] text-fg-3">{JSON.stringify(n.value)}</code>;
  }

  return (
    <li className={`flex flex-col gap-1.5 px-3 py-2 sm:flex-row sm:items-center sm:gap-3 ${pending ? "opacity-70" : ""}`} data-tour={tour}>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1.5">
          {n.permission && <Icon name="lock" className="size-3 shrink-0 text-warning" />}
          <span className={`truncate text-[12.5px] ${n.permission ? "font-semibold" : "font-medium"}`}>{highlight ? n.label : n.label}</span>
          {n.permission && <Badge tone="warning">permission</Badge>}
          {n.readOnly && !n.permission && <Badge tone="neutral">in game only</Badge>}
          {pending && <Icon name="sync" className="spin size-3 text-fg-3" />}
        </div>
        {n.description && <p className="mt-0.5 text-[11px] leading-snug text-fg-3">{n.description}</p>}
        {n.permission && mode !== "standalone" && <p className="mt-0.5 text-[11px] leading-snug text-warning">Read-only here: change it in the standalone control center (open it from the launcher) or in game under HUD menu → Whats An AI Bridge.</p>}
        <code className="mt-0.5 block truncate font-code text-[10px] text-fg-3">{n.path}</code>
      </div>
      <div key={flash} className={`flex shrink-0 items-center rounded-md ${flash ? "flash-accent" : ""}`}>{control}</div>
    </li>
  );
}

function ColorControl({ value, onChange, label, disabled }: { value: string; onChange: (v: string) => void; label: string; disabled?: boolean }) {
  const rgb = value.length >= 7 ? value.slice(0, 7) : "#ffffff";
  const alpha = value.length === 9 ? value.slice(7) : "ff";
  const [text, setText] = useState(value);
  useEffect(() => setText(value), [value]);
  const commit = (v: string) => { if (/^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(v) && v !== value) onChange(v.length === 7 ? v + alpha : v); else setText(value); };
  return (
    <div className="flex items-center gap-2">
      <span className="relative size-7 overflow-hidden rounded-md border border-line" style={{ background: `linear-gradient(45deg, var(--color-surface-3) 25%, transparent 25%, transparent 75%, var(--color-surface-3) 75%), linear-gradient(45deg, var(--color-surface-3) 25%, transparent 25%, transparent 75%, var(--color-surface-3) 75%)`, backgroundSize: "8px 8px", backgroundPosition: "0 0, 4px 4px" }}>
        <span className="absolute inset-0" style={{ background: value }} />
        <input type="color" value={rgb} disabled={disabled} aria-label={label} onChange={(e) => setText(e.target.value + alpha)} onBlur={(e) => commit(e.target.value + alpha)} className="absolute inset-0 cursor-pointer opacity-0" />
      </span>
      <input type="text" value={text} disabled={disabled} aria-label={`${label} hex`} onChange={(e) => setText(e.target.value)} onBlur={() => commit(text)} onKeyDown={(e) => { if (e.key === "Enter") commit(text); }} spellCheck={false}
        className="h-7 w-24 rounded-md border border-line bg-surface px-1.5 font-code text-[11.5px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50" />
    </div>
  );
}

function UndoBar({ store, label, previous }: { store: ControlStore; label: string; previous: unknown }) {
  const [busy, setBusy] = useState(false);
  return (
    <div className="slide-up fixed inset-x-3 bottom-3 z-40 flex justify-center" data-tour={T.settingsUndo}>
      <div className="relative flex max-w-md items-center gap-3 overflow-hidden rounded-lg border border-line bg-surface px-3 py-2 text-[12px] shadow-xl">
        <Icon name="check" className="size-3.5 shrink-0 text-success" />
        <span className="min-w-0 truncate">Changed <b className="font-semibold">{label}</b></span>
        <Button size="sm" icon="arrowLeft" busy={busy} onClick={async () => { setBusy(true); await store.undoLast(); setBusy(false); }} title={`Put back ${JSON.stringify(previous)}`}>Undo</Button>
        <button type="button" aria-label="Dismiss" onClick={() => store.dismissUndo()} className="grid size-6 place-items-center rounded-md text-fg-3 hover:bg-surface-3 hover:text-fg"><Icon name="x" className="size-3" /></button>
        <span className="drain absolute inset-x-0 bottom-0 h-0.5 bg-ring/60" aria-hidden />
      </div>
    </div>
  );
}
