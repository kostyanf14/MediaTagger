'use strict'

function apiHostIsLocal(origin) {
  try {
    const host = new URL(origin).hostname
    return host === 'localhost' || host === '127.0.0.1' || host === '::1'
  } catch {
    return true
  }
}

// Empty means this PC. A host typed without a scheme is https, except loopback.
function normalizeApiOrigin(input) {
  let text = String(input ?? '').trim().replace(/\/+$/, '')
  if (!text) return 'http://localhost:5001'
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(text) && !/^https?:\/\//i.test(text)) return null
  if (!/^https?:\/\//i.test(text)) {
    const name = text.split('/')[0].replace(/^\[|\]$/g, '').split(':')[0]
    const local = name === 'localhost' || name === '127.0.0.1' || name === '::1'
    text = `${local ? 'http' : 'https'}://${text}`
  }
  let url
  try { url = new URL(text) } catch { return null }
  if (url.username || url.password) return null
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null
  if (!url.hostname) return null
  return url.origin
}

module.exports = { apiHostIsLocal, normalizeApiOrigin }
