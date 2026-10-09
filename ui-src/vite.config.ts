import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { viteSingleFile } from "vite-plugin-singlefile";

// Six builds into dist/, all single-file (vite-plugin-singlefile takes one input per build):
//   default          player-stats.html  -> embedded in the server as ui://exile/player-stats
//   --mode explorer  data-explorer.html -> embedded as ui://exile/data-explorer
//   --mode memory    memory-view.html   -> embedded as ui://exile/memory-view
//   --mode perf      hud-performance.html -> embedded as ui://exile/hud-performance
//   --mode control   control-center.html -> embedded as ui://exile/control-center and served at /app
//   --mode harness   harness.html       -> dev-only fake host that loads any app (?app=stats|explorer|memory|perf|control)
const INPUTS: Record<string, string> = { explorer: "data-explorer.html", memory: "memory-view.html", perf: "hud-performance.html", control: "control-center.html", harness: "harness.html" };

export default defineConfig(({ mode }) => {
  const input = INPUTS[mode] ?? "player-stats.html";
  return {
    plugins: [react(), tailwindcss(), viteSingleFile()],
    build: {
      outDir: "dist",
      // The first build (player-stats) clears dist/; the others add to it.
      emptyOutDir: !(mode in INPUTS),
      rollupOptions: { input },
      // The apps ship inside tool resources: keep them small and dependency-free at runtime.
      sourcemap: false,
      reportCompressedSize: false,
    },
    preview: { allowedHosts: ["127.0.0.1", "localhost"] },
  };
});
