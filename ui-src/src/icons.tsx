import type { SVGProps } from "react";

// Inline icon set (stroke, 24-unit grid). The bundle must stay self-contained, so no icon font.

const PATHS = {
  pin: "M12 17v5M9.5 11V5a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v6l2.5 3H7z",
  search: "M11 4a7 7 0 1 1 0 14 7 7 0 0 1 0-14zM20 20l-4-4",
  x: "M6 6l12 12M18 6L6 18",
  copy: "M9 9h11v11H9zM5 15H4V4h11v1",
  check: "M5 12.5l4.5 4.5L19 7",
  maximize: "M8 3H3v5M16 3h5v5M3 16v5h5M21 16v5h-5",
  minimize: "M8 3v5H3M16 3v5h5M3 16h5v5M21 16h-5v5",
  chevron: "M6 9l6 6 6-6",
  sparkle: "M12 3l2 5.5L19.5 10 14 12l-2 5.5L10 12 4.5 10 10 8.5zM19 17l.8 2.2L22 20l-2.2.8L19 23l-.8-2.2L16 20l2.2-.8z",
  flame: "M12 22c4.4 0 7-3 7-6.8 0-3.3-2-5.4-3.2-7.6-.9 1.8-1.7 2.7-2.8 3.1C13 7.6 12 4.4 9.8 2 9 6.6 5 8.6 5 15.2 5 19 7.6 22 12 22z",
  snowflake: "M12 2v20M2 12h20M5 5l14 14M19 5L5 19",
  bolt: "M13 2L4 14h7l-1 8 10-13h-7z",
  skull: "M12 3a8 8 0 0 0-8 8c0 2.8 1.3 4.9 3 6.2V20a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-2.8c1.7-1.3 3-3.4 3-6.2a8 8 0 0 0-8-8zM9 12.5h.01M15 12.5h.01M10.5 21v-2.5M13.5 21v-2.5",
  heart: "M12 21s-7.5-4.8-9.3-9.6A5.2 5.2 0 0 1 12 6.6a5.2 5.2 0 0 1 9.3 4.8C19.5 16.2 12 21 12 21z",
  shield: "M12 2l8 3.2V11c0 5-3.4 9.1-8 11-4.6-1.9-8-6-8-11V5.2z",
  drop: "M12 2.5S5 10 5 14.5a7 7 0 0 0 14 0C19 10 12 2.5 12 2.5z",
  sync: "M21 12a9 9 0 0 1-15.6 6.2L3 16M3 12a9 9 0 0 1 15.6-6.2L21 8M3 21v-5h5M21 3v5h-5",
  warning: "M12 3l10 18H2zM12 10v4M12 18h.01",
  offline: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM5.6 5.6l12.8 12.8",
  layers: "M12 3l9 4.5-9 4.5-9-4.5zM3 12l9 4.5 9-4.5M3 16.5L12 21l9-4.5",
  code: "M8 7l-5 5 5 5M16 7l5 5-5 5",
  activity: "M22 12h-4l-3 9L9 3l-3 9H2",
  sword: "M14.5 17.5L3 6V3h3l11.5 11.5M13 19l6-6M16 16l4 4M19 21l2-2",
  info: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 11v5M12 8h.01",
  gamepad: "M6 11h4M8 9v4M15 12h.01M18 10h.01M6.5 6h11a4.5 4.5 0 0 1 4.4 5.6l-1 4a3 3 0 0 1-5.2 1.2L14 15h-4l-1.7 1.8a3 3 0 0 1-5.2-1.2l-1-4A4.5 4.5 0 0 1 6.5 6z",
  up: "M12 19V5M5 12l7-7 7 7",
  down: "M12 5v14M19 12l-7 7-7-7",
  arrowUpRight: "M7 17L17 7M8 7h9v9",
  // Data explorer
  chevronRight: "M9 6l6 6-6 6",
  arrowLeft: "M19 12H5M12 5l-7 7 7 7",
  arrowRight: "M5 12h14M12 5l7 7-7 7",
  eye: "M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z",
  eyeOff: "M3 3l18 18M10.6 10.6A3 3 0 0 0 13.4 13.4M9.9 5.2A10.5 10.5 0 0 1 12 5c6 0 10 7 10 7a17 17 0 0 1-3.2 3.8M6.2 6.2A17 17 0 0 0 2 12s4 7 10 7a10 10 0 0 0 4.8-1.2",
  lock: "M6 11V8a6 6 0 0 1 12 0v3M5 11h14v10H5z",
  clock: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 7v5l3 2",
  puzzle: "M10 3a2 2 0 0 1 4 0v1h3a1 1 0 0 1 1 1v3h1a2 2 0 0 1 0 4h-1v3a1 1 0 0 1-1 1h-3v1a2 2 0 0 1-4 0v-1H7a1 1 0 0 1-1-1v-3H5a2 2 0 0 1 0-4h1V5a1 1 0 0 1 1-1h3z",
  pencil: "M4 20h4L18 10l-4-4L4 16zM13 7l4 4",
  send: "M22 2L11 13M22 2l-7 20-4-9-9-4z",
  target: "M12 3v3M12 18v3M3 12h3M18 12h3M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10z",
  square: "M5 5h14v14H5z",
  checkSquare: "M5 5h14v14H5zM8.5 12.5l2.5 2.5 5-5",
  list: "M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01",
  braces: "M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5a2 2 0 0 0 2 2h1M16 3h1a2 2 0 0 1 2 2v5a2 2 0 0 0 2 2 2 2 0 0 0-2 2v5a2 2 0 0 1-2 2h-1",
  play: "M7 4l13 8-13 8z",
  pause: "M8 5v14M16 5v14",
  dot: "M12 10a2 2 0 1 0 0 4 2 2 0 0 0 0-4z",
  box: "M12 3l8 4.5v9L12 21l-8-4.5v-9zM12 12l8-4.5M12 12v9M12 12L4 7.5",
  hash: "M5 9h14M5 15h14M9.5 4l-2 16M16.5 4l-2 16",
  // Memory view
  quote: "M7 7h4v4H7zM13 7h4v4h-4zM11 11c0 2.5-1.5 4-4 4.5M17 11c0 2.5-1.5 4-4 4.5",
  sliders: "M4 6h10M18 6h2M4 12h3M11 12h9M4 18h12M20 18h0M14 4v4M7 10v4M16 16v4",
  chip: "M8 8h8v8H8zM5 5h14v14H5zM9 2v3M15 2v3M9 19v3M15 19v3M2 9h3M2 15h3M19 9h3M19 15h3",
  radio: "M12 12h.01M8.5 8.5a5 5 0 0 0 0 7M15.5 8.5a5 5 0 0 1 0 7M5.6 5.6a9 9 0 0 0 0 12.8M18.4 5.6a9 9 0 0 1 0 12.8",
  binary: "M6 4h4v7H6zM6 13h4v7H6zM14 4h4v7h-4zM14 13h4v7h-4z",
  diff: "M12 3v18M3 12h18",
  grid: "M4 4h16v16H4zM4 12h16M12 4v16",
  circleX: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM9 9l6 6M15 9l-6 6",
  circleCheck: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM8.5 12.5l2.5 2.5 5-5",
} as const;

export type IconName = keyof typeof PATHS;

export function Icon({ name, className = "size-4", ...rest }: { name: IconName; className?: string } & Omit<SVGProps<SVGSVGElement>, "name">) {
  return (
    <svg viewBox="0 0 24 24" className={className} fill="none" stroke="currentColor" strokeWidth="1.8"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden focusable="false" {...rest}>
      <path d={PATHS[name]} />
    </svg>
  );
}
