// The health bar plugin's bars, drawn in the browser exactly as the plugin's BarRenderer draws them in game (the same
// geometry snapped to whole pixels, the same pixel-stepped corners: outline, background, then the pools life / energy
// shield / ward each with its own fill, trail chip, hit flash and regen glow: as a sandwich of layers stacked top to
// bottom (ward, ES, life), each as thick as its animated share of the combined max; side by side in regions of the width
// by that share, with dividers; or life only. An overfull pool spills past the bar's right end on its row, faint for a
// little and bolder for more; a granted pool (a value with no max) is a translucent veil; shading and gloss over the whole
// bar like a glass tube; segment notches; the culling marker on life; the total line under the bar, which spills past
// 100% too; the percent, up to 999%; low-life pulse on the life pool), animated as Bar.Animate animates them (per pool:
// a drop of the raw current is instant with a trail that holds then drains and a flash, a drop from a max increase eases
// quietly, a rise eases in at rate 9 with a glow only when the current went up; the veil eases down at 20 and up at 6,
// the shares at 10; the bar fades in and out), and driven by the plugin's PoolSim (hits take ward, then ES, then life; ES
// recharges after a pause; ward restores on its timer; life regenerates and gets a flask; every 6-11 s an event: a pool
// overcapped by 40-120% that decays back, a missing pool granted, or max life buffed +30% for 4 s). The look is read from
// the plugin's live settings nodes, so a slider or colour change shows at once. Mirrors Plugins/Whats A Health Bar/
// BarRenderer.cs, Bar.cs and PoolSim.cs: change both together.

export const KINDS = ["Unique", "Rare", "Magic", "Normal", "Minion", "Player", "Self"] as const;
/** What the plugin writes next to each preview bar. */
export const KIND_LABELS = ["Unique", "Rare", "Magic", "Normal", "Minion", "Player", "You"];
/** The settings group of each kind, as the plugin's [Menu] names it. */
export const KIND_GROUPS = ["Unique enemies", "Rare enemies", "Magic enemies", "Normal enemies", "Minions and allies", "Other players", "You"];
/** The plugin's preview samples per kind: life, energy shield, ward. */
export const PREVIEW_POOLS: [number, number, number][] = [[600, 400, 0], [150, 850, 0], [400, 0, 600], [1000, 0, 0], [1000, 0, 0], [700, 300, 0], [500, 300, 200]];
export const LIFE = 0, ES = 1, WARD = 2, POOLS = 3;

export interface Rgba { r: number; g: number; b: number; a: number }

/** "#RRGGBB" or "#RRGGBBAA" (the bridge's colour value) to channels; anything else is the fallback. */
export function parseColor(v: unknown, fallback = "#ffffffff"): Rgba {
  const s = typeof v === "string" && /^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(v) ? v : fallback;
  const n = parseInt(s.slice(1, 7), 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255, a: s.length === 9 ? parseInt(s.slice(7, 9), 16) / 255 : 1 };
}

export function css(c: Rgba, mul = 1): string {
  return `rgba(${c.r | 0},${c.g | 0},${c.b | 0},${Math.max(0, Math.min(1, c.a * mul)).toFixed(3)})`;
}

const lerpWhite = (c: Rgba, t: number): Rgba => ({ r: c.r + (255 - c.r) * t, g: c.g + (255 - c.g) * t, b: c.b + (255 - c.b) * t, a: c.a });

export interface AnimParams { enabled: boolean; trail: boolean; trailHold: number; trailDrain: number; hitFlash: boolean; healGlow: boolean; healRate: number; fadeRate: number }

/** One bar: the plugin's Bar for a preview sample. Pools are independent, each with its own max, current and animation. */
export class Bar {
  readonly cur = [0, 0, 0];
  /** The game's (unreserved) max; 0 when the pool has none. */
  readonly baseMax = [0, 0, 0];
  /** The 100% mark used for the fill: the base max, or the high-water mark of a pool without one. */
  readonly max = [0, 0, 0];
  /** A pool without a base max that has a value (granted): drawn as a veil. */
  readonly temp = [false, false, false];
  private readonly peak = [0, 0, 0];
  private readonly lastCur = [-1, -1, -1];
  readonly shown = [0, 0, 0];
  readonly trail = [0, 0, 0];
  readonly flash = [0, 0, 0];
  readonly glow = [0, 0, 0];
  readonly veil = [0, 0, 0];
  readonly share = [0, 0, 0];
  private readonly hold = [0, 0, 0];
  x = 0; y = 0;
  seen = false;
  alpha = 0;
  dying = false;
  wanted = true;

  constructor(public kind: number) {}

  get maxHp(): number { return this.baseMax[LIFE]; }
  get maxTotal(): number { return this.max[LIFE] + this.max[ES] + this.max[WARD]; }
  get baseTotal(): number { return this.baseMax[LIFE] + this.baseMax[ES] + this.baseMax[WARD]; }
  get total(): number { return this.cur[LIFE] + this.cur[ES] + this.cur[WARD]; }

  /** Bar.SetPool: one pool from the game's current and (unreserved) max. */
  setPool(p: number, cur: number, baseMax: number) {
    this.cur[p] = cur;
    this.baseMax[p] = baseMax;
    if (baseMax > 0) { this.temp[p] = false; this.max[p] = baseMax; this.peak[p] = 0; }
    else if (cur > 0) { this.temp[p] = true; this.peak[p] = Math.max(this.peak[p], cur); this.max[p] = this.peak[p]; }
    else { this.temp[p] = false; this.max[p] = 0; this.peak[p] = 0; }
  }

  fraction(p: number): number { return this.max[p] > 0 ? Math.max(0, Math.min(1, this.cur[p] / this.max[p])) : 0; }

  /** How far over full a pool is; for a granted pool, its size next to the other pools' base total. */
  over(p: number): number {
    if (this.temp[p]) { const others = this.baseTotal; return others > 0 ? this.cur[p] / others : 1; }
    return this.baseMax[p] > 0 && this.cur[p] > this.baseMax[p] ? (this.cur[p] - this.baseMax[p]) / this.baseMax[p] : 0;
  }

  /** All pools against their normal maxima: can pass 1 when overfull or granted (the percent and the total line). */
  get totalFraction(): number {
    const baseTotal = this.baseTotal;
    if (baseTotal > 0) return this.total / baseTotal;
    return this.maxTotal > 0 ? this.total / this.maxTotal : 0;
  }

  /** Bar.Animate, cause-aware: hits and heals are judged on the raw current; a max change eases quietly. */
  animate(now: number, dt: number, p: AnimParams) {
    const first = !this.seen;
    if (first) { this.seen = true; this.alpha = p.enabled && p.fadeRate > 0 ? 0 : 1; }
    const maxTotal = this.maxTotal;
    let holdEnd = 0;
    for (let i = 0; i < POOLS; i++) {
      const target = this.dying ? 0 : this.fraction(i);
      const over = this.dying ? 0 : this.over(i);
      const share = maxTotal > 0 ? this.max[i] / maxTotal : 0;
      const dCur = this.lastCur[i] < 0 ? 0 : this.cur[i] - this.lastCur[i];
      this.lastCur[i] = this.cur[i];
      if (first || !p.enabled) {
        this.shown[i] = this.trail[i] = target;
        this.veil[i] = over;
        this.share[i] = share;
        this.flash[i] = this.glow[i] = 0;
        continue;
      }
      if (target < this.shown[i] - 0.0005) {
        if (dCur < 0 || this.dying) {
          if (this.trail[i] < this.shown[i]) this.trail[i] = this.shown[i];
          this.shown[i] = target;
          this.hold[i] = now + p.trailHold;
          if (p.hitFlash) this.flash[i] = 1;
        } else this.shown[i] += (target - this.shown[i]) * ease(12, dt);   // the max went up: not a hit
      } else if (target > this.shown[i] + 0.0005) {
        this.shown[i] += (target - this.shown[i]) * ease(p.healRate, dt);
        if (target - this.shown[i] < 0.002) this.shown[i] = target;
        if (p.healGlow && dCur > 0) this.glow[i] = 1;   // the max went down: no glow
      }
      if (!p.trail) this.trail[i] = this.shown[i];
      else if (now >= this.hold[i]) {
        this.trail[i] += (this.shown[i] - this.trail[i]) * ease(p.trailDrain, dt);
        if (this.trail[i] - this.shown[i] < 0.002) this.trail[i] = this.shown[i];
      }
      if (this.trail[i] < this.shown[i]) this.trail[i] = this.shown[i];
      this.veil[i] += (over - this.veil[i]) * ease(over < this.veil[i] ? 20 : 6, dt);
      if (Math.abs(over - this.veil[i]) < 0.002) this.veil[i] = over;
      this.share[i] += (share - this.share[i]) * ease(10, dt);
      if (Math.abs(share - this.share[i]) < 0.002) this.share[i] = share;
      if (this.flash[i] > 0) this.flash[i] = Math.max(0, this.flash[i] - dt * 7);
      if (this.glow[i] > 0) this.glow[i] = Math.max(0, this.glow[i] - dt * 2.2);
      holdEnd = Math.max(holdEnd, this.hold[i]);
    }
    if (!p.enabled) { this.alpha = this.dying ? 0 : 1; return; }
    let fadeTo = this.wanted && !this.dying ? 1 : 0;
    if (this.dying && now < holdEnd + 0.15) fadeTo = 1;
    this.alpha = p.fadeRate <= 0 ? fadeTo : moveTo(this.alpha, fadeTo, p.fadeRate * dt);
  }
}

const ease = (rate: number, dt: number) => 1 - Math.exp(-rate * dt);
const moveTo = (v: number, to: number, step: number) => (v < to ? Math.min(to, v + step) : Math.max(to, v - step));

/** PoolSim: drives a preview bar the way pools move in game, each on its own, with the cases that trip naive displays. */
export class PoolSim {
  private readonly baseLife: number; private readonly baseEs: number; private readonly baseWard: number;
  private maxLife: number; private maxEs: number; private maxWard: number;
  private life: number; private es: number; private ward: number;
  private nextHit: number; private lastHit = -10; private wardBroke = -1; private lastFlask = -10; private nextEvent: number; private buffUntil = 0;
  static readonly ES_RECHARGE_DELAY = 1.6; static readonly WARD_RESTORE_DELAY = 2.4; static readonly FLASK_COOLDOWN = 3.5;
  static readonly ES_RECHARGE_PER_SEC = 0.45; static readonly LIFE_REGEN_PER_SEC = 0.025; static readonly OVER_DECAY_PER_SEC = 0.12;

  constructor(public readonly bar: Bar, life: number, es: number, ward: number, now: number, rng: () => number) {
    this.maxLife = this.baseLife = life; this.maxEs = this.baseEs = es; this.maxWard = this.baseWard = ward;
    this.life = life; this.es = es; this.ward = ward;
    this.nextHit = now + rng() * 1.2;
    this.nextEvent = now + 3 + rng() * 5;
    this.write();
  }

  step(now: number, dt: number, rng: () => number) {
    if (now >= this.nextEvent) this.event(now, rng);
    if (this.buffUntil > 0 && now >= this.buffUntil) { this.maxLife = this.baseLife; this.buffUntil = 0; this.life = Math.min(this.life, this.maxLife); }
    if (now >= this.nextHit) {
      const total = this.baseLife + this.baseEs + this.baseWard;
      let dmg = total * (rng() < 0.1 ? 0.25 + rng() * 0.15 : 0.04 + rng() * 0.12);
      const w = Math.min(this.ward, dmg); this.ward -= w; dmg -= w;
      if (w > 0 && this.ward <= 0 && this.maxWard > 0) this.wardBroke = now;
      const e = Math.min(this.es, dmg); this.es -= e; dmg -= e;
      this.life = Math.max(1, this.life - dmg);
      this.lastHit = now;
      this.nextHit = now + (rng() < 0.25 ? 2.2 + rng() * 1.5 : 0.35 + rng() * 0.6);
    }
    this.es = settle(this.es, this.maxEs, this.maxEs > 0 && now - this.lastHit > PoolSim.ES_RECHARGE_DELAY ? this.maxEs * PoolSim.ES_RECHARGE_PER_SEC : 0, dt);
    this.ward = settle(this.ward, this.maxWard, 0, dt);
    if (this.maxWard > 0 && this.wardBroke >= 0 && now - this.wardBroke > PoolSim.WARD_RESTORE_DELAY) { this.ward = Math.max(this.ward, this.maxWard); this.wardBroke = -1; }
    this.life = settle(this.life, this.maxLife, this.maxLife * PoolSim.LIFE_REGEN_PER_SEC, dt);
    if (this.life < this.maxLife * 0.25 && now - this.lastFlask > PoolSim.FLASK_COOLDOWN) { this.life = Math.min(this.maxLife, this.life + this.maxLife * 0.6); this.lastFlask = now; }
    this.write();
  }

  private event(now: number, rng: () => number) {
    this.nextEvent = now + 6 + rng() * 5;
    const which = Math.floor(rng() * 3);
    if (which === 0) {   // overcap: the largest pool goes 40-120% over its max
      if (this.maxEs >= this.maxLife && this.maxEs > 0) this.es = this.maxEs * (1.4 + rng() * 0.8);
      else this.life = this.maxLife * (1.4 + rng() * 0.8);
    } else if (which === 1) {   // a pool it doesn't have is granted (no max), else overcap ward
      const size = (this.baseLife + this.baseEs + this.baseWard) * (0.25 + rng() * 0.5);
      if (this.maxWard === 0) this.ward += size;
      else if (this.maxEs === 0) this.es += size;
      else this.ward = this.maxWard * 1.6;
    } else {   // max life buffed for 4 s; current life unchanged
      this.maxLife = Math.floor(this.baseLife * 1.3);
      this.buffUntil = now + 4;
    }
  }

  private write() {
    this.bar.setPool(LIFE, this.life | 0, this.maxLife);
    this.bar.setPool(ES, this.es | 0, this.maxEs);
    this.bar.setPool(WARD, this.ward | 0, this.maxWard);
  }
}

/** Above max: decays toward it (to 0 for a pool without a max). Below: regenerates at `regen`. */
function settle(cur: number, max: number, regen: number, dt: number): number {
  if (cur > max) return Math.max(max, cur - Math.max(max, 1) * PoolSim.OVER_DECAY_PER_SEC * dt - (max === 0 ? cur * 0.08 * dt : 0));
  return Math.min(max, cur + regen * dt);
}

/** The plugin's Sim(kind): a preview bar with that kind's sample pools. */
export function previewSim(kind: number, now: number, rng: () => number = Math.random): PoolSim {
  const [life, es, ward] = PREVIEW_POOLS[kind];
  return new PoolSim(new Bar(kind), life, es, ward, now, rng);
}

export interface KindStyle { show: boolean; showPercent: boolean; w: number; h: number; life: Rgba; segments: number }
export type Pools = "Sandwich" | "Side by side" | "Life only";
export type TotalLine = "Off" | "Rares and uniques" | "All";

/** BarRenderer.Prepare: the settings read into flat colours and sizes. */
export interface Look {
  scale: number; rounding: number; outlineW: number;
  outline: Rgba; bg: Rgba; trail: Rgba; heal: Rgba; es: Rgba; ward: Rgba; text: Rgba;
  gloss: number; shading: number; cull: number; pulse: number;
  pools: Pools; dividers: boolean; totalLine: TotalLine;
  styles: KindStyle[];
}

/** Reads the plugin's settings (by path, e.g. "Look.Scale") into a Look. Missing nodes take the plugin's defaults. */
export function lookFrom(get: (path: string) => unknown, resolutionScale: number): Look {
  const num = (p: string, d: number) => { const v = get(p); return typeof v === "number" && Number.isFinite(v) ? v : d; };
  const bool = (p: string, d: boolean) => { const v = get(p); return typeof v === "boolean" ? v : d; };
  const col = (p: string, d: string) => parseColor(get(p), d);
  const animate = bool("Animation.Enabled", true);
  const scale = num("Look.Scale", 1) * (bool("Look.ScaleWithResolution", true) ? resolutionScale : 1);
  const poolsRaw = get("Look.Pools");
  const pools: Pools = poolsRaw === "Side by side" || poolsRaw === "Life only" ? poolsRaw : "Sandwich";   // anything else (an old saved value) is the default
  const totalRaw = get("Look.TotalLine");
  const totalLine: TotalLine = totalRaw === "Off" || totalRaw === "All" ? totalRaw : "Rares and uniques";
  const defaults: [number, number, string, number, boolean, boolean][] = [
    [150, 13, "#e87824ff", 10, true, true], [118, 11, "#e2c434ff", 4, true, true], [86, 8, "#5c84ffff", 0, false, true], [68, 6, "#d63a34ff", 0, false, true],
    [56, 5, "#48c46eff", 0, false, true], [96, 8, "#40c88cff", 0, false, true], [96, 8, "#40c88cff", 0, false, false],
  ];
  return {
    scale,
    rounding: Math.round(num("Look.Rounding", 2)),
    outlineW: num("Look.Outline", 1),
    outline: col("Look.OutlineColor", "#08080ae6"),
    bg: col("Look.BackgroundColor", "#161418c8"),
    trail: col("Look.TrailColor", "#fffaf2d7"),
    heal: col("Look.HealColor", "#96ffaadc"),
    es: col("Look.EsColor", "#b0e2fff5"),
    ward: col("Look.WardColor", "#b084fff5"),
    text: col("Look.TextColor", "#f5f0e6ff"),
    gloss: num("Look.Gloss", 0.22),
    shading: num("Look.Shading", 0.35),
    cull: num("Look.CullPercent", 0),
    pulse: animate ? num("Animation.LowLifePulse", 20) : 0,
    pools,
    dividers: bool("Look.PoolDividers", true),
    totalLine,
    styles: KINDS.map((k, i) => {
      const [w, h, life, segments, percent, show] = defaults[i];
      return { show: bool(`${k}.Show`, show), showPercent: bool(`${k}.ShowPercent`, percent), w: num(`${k}.Width`, w) * scale, h: num(`${k}.Height`, h) * scale, life: col(`${k}.LifeColor`, life), segments: num(`${k}.Segments`, segments) };
    }),
  };
}

/** The plugin's AnimParams from its Animation settings (heal rate 9, fade rate 1000 / FadeMs). */
export function animFrom(get: (path: string) => unknown): AnimParams {
  const num = (p: string, d: number) => { const v = get(p); return typeof v === "number" && Number.isFinite(v) ? v : d; };
  const bool = (p: string, d: boolean) => { const v = get(p); return typeof v === "boolean" ? v : d; };
  const fade = num("Animation.FadeMs", 180);
  return { enabled: bool("Animation.Enabled", true), trail: bool("Animation.DamageTrail", true), trailHold: num("Animation.TrailHoldMs", 320) / 1000, trailDrain: num("Animation.TrailDrain", 4.5), hitFlash: bool("Animation.HitFlash", true), healGlow: bool("Animation.HealGlow", true), healRate: 9, fadeRate: fade <= 0 ? 0 : 1000 / fade };
}

// ── Drawing: the plugin's quad builders on a canvas, in device pixels ──

/** Per radius, the vertical inset of each corner column that is inset at all (a circle, rounded to pixels): BarRenderer.Insets. */
const INSETS: number[][] = (() => {
  const all: number[][] = [];
  for (let r = 0; r <= 12; r++) {
    const list: number[] = [];
    for (let k = 0; k < r; k++) {
      const d = r - k - 0.5;
      const inset = Math.round(r - Math.sqrt(r * r - d * d));
      if (inset <= 0) break;
      list.push(inset);
    }
    all.push(list);
  }
  return all;
})();

/** How opaque an overfull spill or a granted pool is: faint for a little, bolder as more is added. */
const veilAlpha = (over: number) => 0.22 + 0.53 * Math.min(1, over);

class Quads {
  constructor(private readonly ctx: CanvasRenderingContext2D) {}

  rect(x0: number, y0: number, x1: number, y1: number, fill: string) {
    if (x1 - x0 < 0.5 || y1 - y0 < 0.5) return;
    this.ctx.fillStyle = fill;
    this.ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
  }

  /** A gradient from the top edge to the bottom (the plugin's Grad: a quad with a colour per corner). */
  gradV(x0: number, y0: number, x1: number, y1: number, top: string, bottom: string) {
    if (x1 - x0 < 0.5 || y1 - y0 < 0.5) return;
    const g = this.ctx.createLinearGradient(0, y0, 0, y1);
    g.addColorStop(0, top);
    g.addColorStop(1, bottom);
    this.ctx.fillStyle = g;
    this.ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
  }

  /** A gradient from the left edge to the right. */
  gradH(x0: number, y0: number, x1: number, y1: number, left: string, right: string) {
    if (x1 - x0 < 0.5 || y1 - y0 < 0.5) return;
    const g = this.ctx.createLinearGradient(x0, 0, x1, 0);
    g.addColorStop(0, left);
    g.addColorStop(1, right);
    this.ctx.fillStyle = g;
    this.ctx.fillRect(x0, y0, x1 - x0, y1 - y0);
  }

  /** BarRenderer.Round: a body plus one column per inset step on the rounded ends. */
  round(x0: number, y0: number, x1: number, y1: number, fill: string, r: number, left: boolean, right: boolean) {
    r = Math.min(Math.min(r, Math.floor((x1 - x0) * 0.5)), INSETS.length - 1);
    const ins = r > 0 ? INSETS[r] : [];
    const cols = ins.length;
    if (cols === 0 || (!left && !right)) { this.rect(x0, y0, x1, y1, fill); return; }
    this.rect(left ? x0 + cols : x0, y0, right ? x1 - cols : x1, y1, fill);
    for (let k = 0; k < cols; k++) {
      if (left) this.rect(x0 + k, y0 + ins[k], x0 + k + 1, y1 - ins[k], fill);
      if (right) this.rect(x1 - k - 1, y0 + ins[k], x1 - k, y1 - ins[k], fill);
    }
  }

  /** BarRenderer.CutRect: a rect whose end columns, where they touch the bar's ends, are shortened by the corner cut. */
  cutRect(rx0: number, ry0: number, rx1: number, ry1: number, fill: string, x0: number, x1: number, cutTop: number, cutBottom: number, D: number) {
    if (cutTop === 0 && cutBottom === 0) { this.rect(rx0, ry0, rx1, ry1, fill); return; }
    const left = rx0 <= x0, right = rx1 >= x1;
    this.rect(left ? rx0 + D : rx0, ry0, right ? rx1 - D : rx1, ry1, fill);
    if (left) this.rect(rx0, ry0 + cutTop, rx0 + D, ry1 - cutBottom, fill);
    if (right && rx1 - D > rx0) this.rect(rx1 - D, ry0 + cutTop, rx1, ry1 - cutBottom, fill);
  }
}

/** BarRenderer.Layers: pixel thickness of each pool's layer from its animated share, summing exactly to h (largest remainder, a 2 px minimum). */
function layers(b: Bar, h: number, D: number): number[] {
  const out = [0, 0, 0];
  let total = 0, present = 0;
  for (let p = 0; p < POOLS; p++) if (b.share[p] > 0.001) { present++; total += b.share[p]; }
  if (present === 0) return out;
  const min = h >= present * 2 * D ? 2 * D : h >= present * D ? D : 0;
  const free = h - min * present;
  let used = 0;
  const rem = [0, 0, 0];
  for (let p = 0; p < POOLS; p++) {
    if (b.share[p] <= 0.001) continue;
    const exact = (free * b.share[p]) / total;
    out[p] = min + Math.floor(exact);
    rem[p] = exact - Math.floor(exact);
    used += out[p];
  }
  while (used < h) {
    let best = -1;
    for (let p = 0; p < POOLS; p++) if (b.share[p] > 0.001 && (best < 0 || rem[p] > rem[best])) best = p;
    if (best < 0) break;
    out[best]++;
    rem[best] = -1;
    used++;
  }
  return out;
}

/**
 * BarRenderer.Draw, in device pixels: `dpr` (whole) scales the plugin's pixel sizes so one plugin pixel is one device
 * pixel per CSS pixel and every edge lands on a whole device pixel. `b.x`, `b.y` are the bar's anchor in CSS pixels
 * (bottom centre, the plugin's Screen).
 */
export function drawBar(ctx: CanvasRenderingContext2D, look: Look, b: Bar, now: number, dpr: number, label?: string, fontPx = 13) {
  const st = look.styles[b.kind];
  const a = b.alpha;
  if (a <= 0.004 || b.maxTotal <= 0) return;
  const q = new Quads(ctx);
  const D = Math.max(1, Math.round(dpr));

  const w = Math.round(st.w * dpr);
  const h = Math.max(2 * D, Math.round(st.h * dpr));
  const x0 = Math.round(b.x * dpr - w * 0.5);
  const y0 = Math.round(b.y * dpr - h);
  const x1 = x0 + w;
  const y1 = y0 + h;
  const r = Math.min(look.rounding * D, Math.floor(h * 0.5));
  const ow = Math.round(look.outlineW) * D;

  if (ow > 0) q.round(x0 - ow, y0 - ow, x1 + ow, y1 + ow, css(look.outline, a), r > 0 ? r + ow : 0, true, true);
  q.round(x0, y0, x1, y1, css(look.bg, a), r, true, true);

  let life = st.life;
  if (look.pulse > 0 && b.kind <= 1 && !b.dying && b.maxHp > 0 && b.shown[LIFE] * 100 < look.pulse) life = lerpWhite(life, 0.35 * (0.5 + 0.5 * Math.sin(now * 9)));
  const colorOf = (p: number) => (p === LIFE ? life : p === ES ? look.es : look.ward);

  // Overfull spills past the bar's right end (outside the outline); the percent text moves past the longest spill.
  const spillX = x1 + ow;
  let spillMax = 0;
  /** BarRenderer.Spill: as long as 6% of the bar for a sliver up to 30% at double, faint to bold, fading toward its tip. */
  const spill = (sy0: number, sy1: number, c: Rgba, over: number): number => {
    const len = Math.max(2 * D, Math.round(w * (0.06 + 0.24 * Math.min(1, over))));
    const alpha = a * veilAlpha(over);
    q.gradH(spillX, sy0, spillX + len, sy1, css(c, alpha), css(c, alpha * 0.25));
    return len;
  };

  /** BarRenderer.Pool: one pool in its region: trail chip, fill (a veil when granted), glow, flash. */
  const pool = (p: number, rx0: number, rx1: number, ry0: number, ry1: number, rr: number, roundLeft: boolean, roundRight: boolean) => {
    const rw = rx1 - rx0;
    const xf = Math.round(rx0 + rw * b.shown[p]);
    const xt = Math.round(rx0 + rw * b.trail[p]);
    if (xt > xf) q.round(xf, ry0, xt, ry1, css(look.trail, a), rr, roundLeft && xf <= rx0, roundRight && xt >= rx1);
    if (xf <= rx0) return;
    const right = roundRight && xf >= rx1;
    const fill = b.temp[p] ? a * veilAlpha(b.veil[p]) : a;
    q.round(rx0, ry0, xf, ry1, css(colorOf(p), fill), rr, roundLeft, right);
    if (b.glow[p] > 0) q.round(rx0, ry0, xf, ry1, css(look.heal, a * b.glow[p] * 0.7), rr, roundLeft, right);
    if (b.flash[p] > 0) q.rect(Math.max(rx0, xf - 2 * D), ry0, Math.min(rx1, xf + D), ry1, `rgba(255,255,255,${(a * b.flash[p]).toFixed(3)})`);
  };

  /** BarRenderer.Layer: one sandwich layer, with the bar's outer corners cut by one pixel when rounding is on. */
  const layer = (p: number, ly0: number, ly1: number, cut: boolean, isTop: boolean, isBottom: boolean) => {
    const xf = Math.round(x0 + w * b.shown[p]);
    const xt = Math.round(x0 + w * b.trail[p]);
    const t = cut && isTop ? D : 0, bt = cut && isBottom ? D : 0;
    if (xt > xf) q.cutRect(xf, ly0, xt, ly1, css(look.trail, a), x0, x1, t, bt, D);
    if (xf <= x0) return;
    const fill = b.temp[p] ? a * veilAlpha(b.veil[p]) : a;
    q.cutRect(x0, ly0, xf, ly1, css(colorOf(p), fill), x0, x1, t, bt, D);
    if (b.glow[p] > 0) q.cutRect(x0, ly0, xf, ly1, css(look.heal, a * b.glow[p] * 0.7), x0, x1, t, bt, D);
    if (b.flash[p] > 0) q.rect(Math.max(x0, xf - 2 * D), ly0, Math.min(x1, xf + D), ly1, `rgba(255,255,255,${(a * b.flash[p]).toFixed(3)})`);
  };

  let lifeX0 = x0, lifeX1 = x1;
  if (look.pools === "Side by side") {
    const shareSum = b.share[0] + b.share[1] + b.share[2];
    let cursor = x0, first = true;
    for (let p = 0; p < POOLS; p++) {
      if (b.share[p] <= 0.001 || shareSum <= 0) continue;
      const rx0 = Math.round(cursor);
      cursor += (w * b.share[p]) / shareSum;
      let rx1 = Math.round(cursor);
      if (rx1 >= x1 - 0.5) rx1 = x1;
      if (rx1 <= rx0) continue;
      if (p === LIFE) { lifeX0 = rx0; lifeX1 = rx1; }
      pool(p, rx0, rx1, y0, y1, r, rx0 <= x0, rx1 >= x1);
      if (!first && look.dividers) q.rect(rx0, y0, rx0 + D, y1, `rgba(0,0,0,${(a * 0.85).toFixed(3)})`);
      if (b.veil[p] > 0.002 && !b.temp[p]) spillMax = Math.max(spillMax, spill(y0, y1, colorOf(p), b.veil[p]));
      first = false;
    }
  } else if (look.pools === "Sandwich") {
    const ls = layers(b, h, D);
    let ly = y0, top = -1, bottom = -1;
    for (let p = POOLS - 1; p >= 0; p--) if (ls[p] > 0) { if (top < 0) top = p; bottom = p; }
    for (let p = POOLS - 1; p >= 0; p--) {
      if (ls[p] <= 0) continue;
      const ly1 = ly + ls[p];
      layer(p, ly, ly1, r > 0, p === top, p === bottom);
      if (b.veil[p] > 0.002 && !b.temp[p]) spillMax = Math.max(spillMax, spill(ly, ly1, colorOf(p), b.veil[p]));
      ly = ly1;
    }
  } else {
    pool(LIFE, x0, x1, y0, y1, r, true, true);
    if (b.veil[LIFE] > 0.002) spillMax = spill(y0, y1, life, b.veil[LIFE]);
  }

  const inset = r > 0 ? D : 0;
  if (h >= 4 * D) {
    const ym = Math.round(y0 + h * 0.5);
    if (look.shading > 0) q.gradV(x0 + inset, ym, x1 - inset, y1 - inset, "rgba(0,0,0,0)", `rgba(0,0,0,${(a * look.shading).toFixed(3)})`);
    if (look.gloss > 0) q.gradV(x0 + inset, y0, x1 - inset, ym, `rgba(255,255,255,${(a * look.gloss).toFixed(3)})`, "rgba(255,255,255,0)");
  }

  if (st.segments > 1) {
    const c = `rgba(0,0,0,${(0.588 * a).toFixed(3)})`;
    for (let i = 1; i < st.segments; i++) {
      const x = Math.round(x0 + (w * i) / st.segments);
      q.rect(x, y0 + D, x + D, y1 - D, c);
    }
  }

  if (look.cull > 0 && b.kind <= 3 && b.maxHp > 0) {
    const x = Math.round(lifeX0 + ((lifeX1 - lifeX0) * look.cull) / 100);
    q.rect(x, y0 - D, x + D, y1 + D, `rgba(255,255,255,${(a * 0.9).toFixed(3)})`);
  }

  const below = y1 + ow;
  const total = b.totalFraction;
  const totalUpTo = look.totalLine === "All" ? 6 : 1;
  if (look.totalLine !== "Off" && b.kind <= totalUpTo && (b.max[ES] > 0 || b.max[WARD] > 0 || total > 1.001)) {
    const th = Math.max(2 * D, Math.round(2 * look.scale * dpr));
    q.rect(x0, below + D, x1, below + D + th, css(look.bg, a));
    q.rect(x0, below + D, Math.round(x0 + w * Math.min(1, total)), below + D + th, css(look.text, a * 0.9));
    if (total > 1.001) spill(below + D, below + D + th, look.text, total - 1);
  }

  if (st.showPercent || label) {
    const fs = Math.round(fontPx * dpr);
    ctx.font = `${fs}px system-ui, "Segoe UI", sans-serif`;
    ctx.textBaseline = "top";
    const ty = Math.round(y0 + (h - fs) * 0.5) - D;
    if (st.showPercent) {
      const pct = `${Math.max(0, Math.min(999, Math.ceil(total * 100)))}%`;
      const tx = spillX + spillMax + 4 * D;
      ctx.textAlign = "left";
      ctx.fillStyle = `rgba(0,0,0,${(a * 0.8).toFixed(3)})`;
      ctx.fillText(pct, tx + D, ty + D);
      ctx.fillStyle = css(look.text, a);
      ctx.fillText(pct, tx, ty);
    }
    if (label) {
      ctx.textAlign = "right";
      ctx.fillStyle = css(look.text, a);
      ctx.fillText(label, Math.round(x0 - 8 * D - ow), ty);
    }
  }
}
