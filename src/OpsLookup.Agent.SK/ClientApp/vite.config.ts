import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'

// The build lands in ../wwwroot so ASP.NET Core serves the page from the same
// origin as /api/chat. No CORS, and no Node needed to run the app.
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  base: './',
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    outDir: fileURLToPath(new URL('../wwwroot', import.meta.url)),
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      // In dev the page runs on 5173 and the .NET app elsewhere, so /api is
      // proxied. In production both are same-origin and this is unused.
      '/api': {
        target: 'http://localhost:5100',
        changeOrigin: true,
      },
    },
  },
})