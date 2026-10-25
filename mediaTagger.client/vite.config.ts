import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  const fileEnv = loadEnv(mode, process.cwd(), '')
  const apiOrigin = (
    process.env.MEDIATAGGER_API_URL ||
    process.env.VITE_API_ORIGIN ||
    fileEnv.MEDIATAGGER_API_URL ||
    fileEnv.VITE_API_ORIGIN ||
    'http://localhost:5001'
  ).replace(/\/$/, '')
  const clientUrl = new URL(process.env.MEDIATAGGER_CLIENT_URL || fileEnv.MEDIATAGGER_CLIENT_URL || 'http://localhost:3000')

  return {
    plugins: [react()],
    // Electron loads the build over file://, where root-relative /assets/ paths
    // resolve to the drive root.
    base: './',
    envPrefix: ['VITE_', 'MEDIATAGGER_'],
    server: {
      host: clientUrl.hostname,
      port: Number(clientUrl.port || 3000),
      proxy: {
        '/api': {
          target: apiOrigin,
          changeOrigin: true
        }
      }
    },
    build: {
      outDir: 'build'
    }
  }
})
