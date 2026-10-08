import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { viteSingleFile } from "vite-plugin-singlefile";

// Two builds into dist/, both single-file:
//   default         player-stats.html -> embedded in the server as ui://exile/player-stats
//   --mode harness  harness.html      -> dev-only fake host that loads ./player-stats.html
export default defineConfig(({ mode }) => {
  const input = mode === "harness" ? "harness.html" : "player-stats.html";
  return {
    plugins: [react(), tailwindcss(), viteSingleFile()],
    build: {
      outDir: "dist",
      emptyOutDir: mode !== "harness",
      rollupOptions: { input },
      // The app ships inside a tool resource: keep it small and dependency-free at runtime.
      sourcemap: false,
      reportCompressedSize: false,
    },
    preview: { allowedHosts: ["127.0.0.1", "localhost"] },
  };
});
