import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Built straight into the API host's wwwroot, so `lastwar-server.exe` serves the UI
// with nothing else running. `npm run dev` proxies to the same host for iteration.
export default defineConfig({
  plugins: [react()],
  build: { outDir: "../src/LastWar.Server/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://localhost:5183" } },
});
