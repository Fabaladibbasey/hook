import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: "autoUpdate",
      includeAssets: ["favicon.svg", "icons/*"],
      manifest: {
        name: "Hook Chat",
        short_name: "Hook",
        description: "Secure chat for Hook service connector",
        theme_color: "#0f172a",
        background_color: "#ffffff",
        display: "standalone",
        scope: "/c/",
        start_url: "/c/",
        icons: [
          { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png" },
          { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png" }
        ]
      },
      workbox: {
        navigateFallback: "/index.html",
        navigateFallbackDenylist: [/^\/api\//, /^\/hubs\//, /^\/webhooks\//]
      }
    })
  ],
  build: {
    outDir: "../src/Hook/wwwroot",
    emptyOutDir: true
  },
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5212",
      "/dev/whatsapp": "http://localhost:5212",
      "/hubs": { target: "http://localhost:5212", ws: true }
    }
  }
});
