import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// D5-S16: a separate front-end build with no .NET package a backend could
// reference (design/10-design.md, Open questions 2). It is served as static
// assets by whatever host chooses to serve it; this config only builds those
// assets, it does not decide who serves them.
export default defineConfig({
  plugins: [react()],
  // Dev-only convenience: proxies API calls to a locally running backend (e.g. the sample host on
  // :5299) so `npm run dev` can be checked against a real server. Not present in `vite build`'s
  // output — the production shell is static assets with no backend of its own opinion.
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5299",
    },
  },
});
