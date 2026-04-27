import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath } from 'node:url'

// https://vite.dev/config/
export default defineConfig({
  // Root'u config dosyasının bulunduğu klasöre sabitle.
  // (Bazı Windows/terminal senaryolarında cwd şaşınca 404 görülebiliyor.)
  root: fileURLToPath(new URL('.', import.meta.url)),
  plugins: [react(), tailwindcss()],
  appType: 'spa',
  server: {
    // Bazı ortamlarda middlewareMode yanlışlıkla devreye girebiliyor;
    // SPA için index.html servisinin her zaman aktif kalmasını istiyoruz.
    middlewareMode: false,
    host: 'localhost',
    port: 5178,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'http://localhost:5189',
        changeOrigin: true,
      },
    },
  },
})
