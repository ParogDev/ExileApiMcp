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
