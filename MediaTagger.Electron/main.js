const { app, BrowserWindow, ipcMain, shell, nativeImage, clipboard, protocol, net } = require('electron')
const path = require('path')
const { pathToFileURL } = require('url')
const { spawn } = require('child_process')
const fs = require('fs')
const http = require('http')
const https = require('https')
const { apiHostIsLocal, normalizeApiOrigin } = require('./apiOrigin')

let mainWindow
let dotnetProcess

// Empty until whenReady reads a saved address. An env var set at launch wins.
let API_URL = (process.env.MEDIATAGGER_API_URL || process.env.VITE_API_ORIGIN || '').replace(/\/$/, '')
const DEV = process.env.NODE_ENV === 'development'

// The built window is served from its own scheme rather than file://. A file:// page
// sends "Origin: null", which every sandboxed frame on the web can send too, so the
// API could not tell it apart from a stranger's page.
const APP_SCHEME = 'app'
const APP_ORIGIN = `${APP_SCHEME}://mediatagger`
const BUILD_DIR = path.normalize(path.join(__dirname, '../mediaTagger.client/build'))
const CLIENT_URL = DEV
  ? (process.env.MEDIATAGGER_CLIENT_URL || 'http://localhost:3000')
  : `${APP_ORIGIN}/index.html`

// What the page may load. Images and the API can live on any host (a NAS, map tiles),
// but scripts only come from the app itself.
const CONTENT_SECURITY_POLICY = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data: blob: http: https:",
  "media-src 'self' blob: http: https:",
  "connect-src 'self' http: https:",
  "font-src 'self' data:",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'"
].join('; ')

protocol.registerSchemesAsPrivileged([
  { scheme: APP_SCHEME, privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true } }
])

// Files the page may ask the system to open, reveal, drag or copy. A compromised page
// must not be able to start an arbitrary program through shell.openPath.
const MEDIA_EXTENSIONS = new Set([
  '.jpg', '.jpeg', '.png', '.gif', '.webp', '.bmp', '.tiff', '.heic', '.heif',
  '.mp4', '.mov', '.avi', '.mkv', '.wmv', '.m4v', '.webm', '.mts', '.m2ts', '.3gp'
])

function isMediaFile(filePath) {
  return typeof filePath === 'string'
    && path.isAbsolute(filePath)
    && MEDIA_EXTENSIONS.has(path.extname(filePath).toLowerCase())
}

/** Only our own window may use the privileged calls below. */
function fromOurWindow(event) {
  const url = event.senderFrame?.url || event.sender?.getURL?.() || ''
  return url.startsWith(`${APP_ORIGIN}/`) || (DEV && url.startsWith(new URL(CLIENT_URL).origin + '/'))
}

async function serveClient(request) {
  let relative = '/index.html'
  try {
    const { pathname } = new URL(request.url)
    if (pathname && pathname !== '/') relative = decodeURIComponent(pathname)
  } catch {
    return new Response('Bad request', { status: 400 })
  }
  const file = path.normalize(path.join(BUILD_DIR, relative))
  if (file !== BUILD_DIR && !file.startsWith(BUILD_DIR + path.sep))
    return new Response('Forbidden', { status: 403 })

  let response
  try { response = await net.fetch(pathToFileURL(file).toString()) }
  catch { return new Response('Not found', { status: 404 }) }

  if (path.extname(file).toLowerCase() !== '.html') return response
  const headers = new Headers(response.headers)
  headers.set('Content-Security-Policy', CONTENT_SECURITY_POLICY)
  return new Response(response.body, { status: response.status, headers })
}

function isLoopback(url) {
  return apiHostIsLocal(url)
}

function originFile() {
  return path.join(app.getPath('userData'), 'api-origin')
}

function readSavedOrigin() {
  try { return normalizeApiOrigin(fs.readFileSync(originFile(), 'utf8')) || '' } catch { return '' }
}

function applyApiOrigin(origin) {
  API_URL = origin
  process.env.MEDIATAGGER_API_URL = origin
}

function normFingerprint(value) {
  const text = String(value || '').trim()
  // Electron's fingerprint is "sha256/<base64>". Node's is colon-separated hex.
  const marked = text.match(/^sha256\/(.+)$/i)
  if (marked) return Buffer.from(marked[1], 'base64').toString('hex').toUpperCase()
  return text.toUpperCase().replace(/[^0-9A-F]/g, '')
}

function pinFile() {
  let host = 'api'
  try { host = new URL(API_URL).hostname } catch {}
  return path.join(app.getPath('userData'), `nas-cert-${host}`)
}

// shortcut: the first certificate this host presents is pinned. Someone who can
// spoof the NAS on that first connection would be trusted. Delete the pin file
// after you replace the NAS certificate.
function rememberCert(fingerprint) {
  const fp = normFingerprint(fingerprint)
  if (!fp) return false
  const file = pinFile()
  let saved = ''
  try { saved = normFingerprint(fs.readFileSync(file, 'utf8')) } catch {}
  // The first build stored a SHA-1 pin. Both sides can only agree on SHA-256.
  if (!saved || (saved.length === 40 && fp.length === 64)) {
    fs.mkdirSync(path.dirname(file), { recursive: true })
    fs.writeFileSync(file, fp)
    console.log(`[API] Trusting NAS certificate ${fp}`)
    return true
  }
  if (saved === fp) return true
  // Every image and retry hits this. One line is enough.
  if (!rememberCert.logged) {
    rememberCert.logged = true
    console.error(`[API] NAS certificate changed. Delete ${file} if you replaced the server certificate.`)
  }
  return false
}

function isApiHost(url) {
  try {
    const target = new URL(API_URL)
    return target.protocol === 'https:' && new URL(url).hostname === target.hostname
  } catch {
    return false
  }
}

app.on('certificate-error', (event, _webContents, url, _error, certificate, callback) => {
  if (!isApiHost(url)) { callback(false); return }
  event.preventDefault()
  callback(rememberCert(certificate.fingerprint))
})

// Where `npm run build` in the repo root publishes the Release API.
const PUBLISHED_API = path.join(
  __dirname, '../MediaTagger.API/bin/publish',
  process.platform === 'win32' ? 'MediaTagger.API.exe' : 'MediaTagger.API'
)

function startDotnetApi() {
  const apiPath = path.join(__dirname, '../MediaTagger.API')
  // The published build starts straight away. `dotnet run` goes through MSBuild
  // first and runs unoptimised code, so it is only the fallback for a checkout
  // that has not been built yet. Both run from the project folder, which is what
  // the API resolves appsettings.json and its caches against.
  // The API answers browser calls only from the window's own origin.
  const clientOrigins = [APP_ORIGIN, new URL(CLIENT_URL).origin].filter((o, i, all) => o !== 'null' && all.indexOf(o) === i)
  const apiEnv = {
    ...process.env,
    MediaTagger__ListenUrl: API_URL,
    MediaTagger__ClientOrigins: process.env.MediaTagger__ClientOrigins || clientOrigins.join(',')
  }
  dotnetProcess = fs.existsSync(PUBLISHED_API)
    ? spawn(PUBLISHED_API, [], { cwd: apiPath, stdio: 'pipe', env: apiEnv })
    : spawn('dotnet', ['run'], { cwd: apiPath, stdio: 'pipe', env: apiEnv })
  dotnetProcess.stdout.on('data', d => process.stdout.write(`[API] ${d}`))
  dotnetProcess.stderr.on('data', d => process.stderr.write(`[API ERR] ${d}`))
}

// Polled often: the API is usually up within a second, and every interval spent
// waiting here is time the window stays blank.
const API_POLL_MS = 150
const API_TIMEOUT_MS = 30000

function waitForApi() {
  const deadline = Date.now() + API_TIMEOUT_MS
  return new Promise((resolve, reject) => {
    const attempt = () => {
      const retry = () => {
        if (Date.now() < deadline) setTimeout(attempt, API_POLL_MS)
        else reject(new Error('API did not start'))
      }
      const lib = API_URL.startsWith('https:') ? https : http
      const options = API_URL.startsWith('https:') ? { rejectUnauthorized: false } : {}
      const req = lib.get(`${API_URL}/health`, options, res => {
        if (API_URL.startsWith('https:')) {
          const cert = res.socket.getPeerCertificate()
          const presented = cert && cert.fingerprint256
          if (!normFingerprint(presented)) {
            res.resume()
            retry()
            return
          }
          // A different certificate will not start matching, so do not poll for 30s.
          if (!rememberCert(presented)) {
            res.resume()
            reject(new Error('NAS certificate was rejected'))
            return
          }
        }
        res.resume()
        resolve()
      })
      req.on('error', retry)
    }
    attempt()
  })
}

async function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1400, height: 900, minWidth: 900, minHeight: 600,
    titleBarStyle: process.platform === 'darwin' ? 'hiddenInset' : 'default',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    },
    backgroundColor: '#0f0f0f',
    show: false
  })

  mainWindow.once('ready-to-show', () => mainWindow.show())

  // Links (map attributions, say) open in the browser; the window itself never leaves the app.
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//i.test(url)) shell.openExternal(url)
    return { action: 'deny' }
  })
  mainWindow.webContents.on('will-navigate', (event, url) => {
    const allowed = url.startsWith(`${APP_ORIGIN}/`) || (DEV && url.startsWith(new URL(CLIENT_URL).origin + '/'))
    if (!allowed) event.preventDefault()
  })

  try { await waitForApi() } catch {}

  mainWindow.loadURL(CLIENT_URL)

  if (DEV) {
    mainWindow.webContents.openDevTools()
  }
}

// Native drag & drop — filePath must be an absolute local path.
// previewDataUrl is an optional data: URL captured from the thumbnail the gallery
// already painted. Chromium cannot decode HEIC or video, so for those files it is
// the only preview available.
const DRAG_ICON_SIZE = 64

// Last-resort drag icon. startDrag rejects an empty icon outright, so there always
// has to be something to hand it.
const GENERIC_DRAG_ICON = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAACXBIWXMAAAABAAAAAQBPJcTWAAAA0ElEQVR4nOXasW3DQBQE0TEwBThkASpEzTBz6FKUsRmVoAJUgEOXoEiAAycWaUjDe/kBt7eXfOC/TZ8XyiROdhPg63ymYzoef2ng8H2i4Pr+sccvFCVxEidxEidxEidxEidxEidxEidxEidjBpjneeubsCzLA6dGbWDNm23bp8RJnMRJnMRJnMRJnMRJnMRJnMRJnIwcYP6HueyvRm1g2WgWW2/UBl6HxEmcxEmcxEmcxEmcxEmcxEmcxEmc7CnA9cc+Zi/AdN+FbZE4n32BtW47xxNKbkKkYwAAAABJRU5ErkJggg=='

function fitDragIcon(image) {
  const { width, height } = image.getSize()
  const scale = DRAG_ICON_SIZE / Math.max(width, height)
  if (scale >= 1) return image
  return image.resize({
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale))
  })
}

function buildDragIcon(previewDataUrl, filePath) {
  const sources = []
  if (previewDataUrl && previewDataUrl.startsWith('data:')) {
    sources.push(() => nativeImage.createFromDataURL(previewDataUrl))
  }
  // JPEG and PNG decode natively, so the file is its own best icon.
  if (filePath && fs.existsSync(filePath)) {
    sources.push(() => nativeImage.createFromPath(filePath))
  }
  sources.push(() => nativeImage.createFromDataURL(GENERIC_DRAG_ICON))

  for (const load of sources) {
    try {
      const image = load()
      // createFromPath returns an empty image for formats Chromium cannot decode.
      // Passing that to startDrag throws and the drag never begins — which is what
      // used to break dragging HEIC photos and videos.
      if (!image.isEmpty()) return fitDragIcon(image)
    } catch {}
  }
  return null
}

ipcMain.on('start-native-drag', (event, { filePath, previewDataUrl } = {}) => {
  if (!fromOurWindow(event) || !isMediaFile(filePath) || !fs.existsSync(filePath)) return

  const icon = buildDragIcon(previewDataUrl, filePath)
  if (!icon) return

  try {
    event.sender.startDrag({ file: filePath, icon })
  } catch (err) {
    console.error(`[drag] could not start drag for ${filePath}: ${err.message}`)
  }
})

ipcMain.handle('select-folder', async event => {
  if (!fromOurWindow(event)) return null
  const { dialog } = require('electron')
  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openDirectory']
  })
  return result.canceled ? null : result.filePaths[0]
})

ipcMain.on('reveal-in-explorer', (event, filePath) => {
  if (!fromOurWindow(event) || typeof filePath !== 'string' || !path.isAbsolute(filePath)) return
  shell.showItemInFolder(filePath)
})

// Hands the file to whatever the OS uses for it — the escape hatch for videos
// Chromium cannot decode.
// A JPEG or PNG from disk pastes into Telegram. HEIC and the rest come back empty,
// and the page sends a PNG it decoded through the thumbnail API instead.
ipcMain.handle('copy-image', (event, { filePath, dataUrl } = {}) => {
  if (!fromOurWindow(event)) return false
  let image = nativeImage.createEmpty()
  if (isMediaFile(filePath) && fs.existsSync(filePath)) {
    try { image = nativeImage.createFromPath(filePath) } catch { image = nativeImage.createEmpty() }
  }
  if (image.isEmpty() && typeof dataUrl === 'string' && dataUrl.startsWith('data:image/')) {
    try { image = nativeImage.createFromDataURL(dataUrl) } catch { image = nativeImage.createEmpty() }
  }
  if (image.isEmpty()) return false
  clipboard.writeImage(image)
  return true
})

ipcMain.handle('set-api-origin', async (event, raw) => {
  if (!fromOurWindow(event)) throw new Error('not allowed')
  const next = normalizeApiOrigin(raw)
  if (!next) throw new Error('bad origin')
  const file = originFile()
  if (isLoopback(next)) {
    try { fs.unlinkSync(file) } catch { /* nothing saved yet */ }
  } else {
    fs.mkdirSync(path.dirname(file), { recursive: true })
    fs.writeFileSync(file, next)
  }
  const previous = API_URL
  applyApiOrigin(next)
  if (!isLoopback(next)) {
    dotnetProcess?.kill()
    dotnetProcess = null
  } else if (process.env.NODE_ENV !== 'development' && !dotnetProcess) {
    startDotnetApi()
    try { await waitForApi() } catch { /* the window reloads either way */ }
  }
  if (previous !== next) mainWindow?.webContents.reload()
  return next
})

ipcMain.on('open-file', (event, filePath) => {
  if (!fromOurWindow(event) || !isMediaFile(filePath)) return
  shell.openPath(filePath).then(err => {
    // Resolves with a non-empty string when nothing could open the file.
    if (err) console.error(`[open-file] ${filePath}: ${err}`)
  })
})

app.whenReady().then(async () => {
  protocol.handle(APP_SCHEME, serveClient)
  if (!API_URL) applyApiOrigin(readSavedOrigin() || 'http://localhost:5001')
  // A remote API (the NAS) is already running; only a loopback URL is started here.
  if (!DEV && isLoopback(API_URL)) startDotnetApi()
  await createWindow()
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow()
  })
})

app.on('window-all-closed', () => {
  dotnetProcess?.kill()
  if (process.platform !== 'darwin') app.quit()
})

app.on('before-quit', () => {
  dotnetProcess?.kill()
})
