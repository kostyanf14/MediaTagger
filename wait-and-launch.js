#!/usr/bin/env node
const http = require('http')
const { spawn } = require('child_process')
const path = require('path')

const API_URL = (process.env.MEDIATAGGER_API_URL || process.env.VITE_API_ORIGIN || 'http://localhost:5001').replace(/\/$/, '')
const CLIENT_URL = process.env.MEDIATAGGER_CLIENT_URL || 'http://localhost:3000'
const URLS = [
  CLIENT_URL,
  `${API_URL}/health`
]
const INTERVAL = 1000
const TIMEOUT = 120000

function checkUrl(url) {
  const lib = url.startsWith('https:') ? require('https') : http
  return new Promise((resolve) => {
    lib.get(url, (res) => {
      res.resume()
      resolve(res.statusCode < 500)
    }).on('error', () => resolve(false))
  })
}

async function waitForAll() {
  const start = Date.now()
  while (Date.now() - start < TIMEOUT) {
    const results = await Promise.all(URLS.map(checkUrl))
    if (results.every(Boolean)) return true
    const pending = URLS.filter((_, i) => !results[i])
    process.stdout.write(`\r[Electron] Waiting for: ${pending.join(', ')}...`)
    await new Promise(r => setTimeout(r, INTERVAL))
  }
  return false
}

waitForAll().then(ready => {
  if (!ready) {
    console.error('\n[Electron] Timed out waiting for servers')
    process.exit(1)
  }
  console.log('\n[Electron] All servers ready — launching Electron...')

  const mainJs = path.join(__dirname, 'MediaTagger.Electron', 'main.js')

  // Use npx electron on Windows — avoids all .cmd / EINVAL issues
  const proc = spawn('npx', ['electron', mainJs], {
    stdio: 'inherit',
    shell: true,
    cwd: path.join(__dirname, 'MediaTagger.Electron'),
    env: { ...process.env, NODE_ENV: 'development' }
  })

  proc.on('close', code => process.exit(code ?? 0))
})
