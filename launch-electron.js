#!/usr/bin/env node
// Launches Electron from the correct working directory
// This fixes the issue where `npm run dev --prefix` doesn't set cwd for electron binary

const { execSync } = require('child_process')
const path = require('path')

const electronDir = path.join(__dirname, 'MediaTagger.Electron')

try {
  execSync('npm run dev', {
    cwd: electronDir,
    stdio: 'inherit',
    env: { ...process.env, NODE_ENV: 'development' }
  })
} catch (e) {
  process.exit(e.status ?? 1)
}
