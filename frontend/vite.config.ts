import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const backend = 'http://localhost:5210'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': backend,
      '/hubs': { target: backend, ws: true },
    },
  },
  build: {
    outDir: '../backend/TechCare.Clinic.Api/wwwroot',
    emptyOutDir: true,
  },
})
