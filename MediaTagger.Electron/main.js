const { app, BrowserWindow, ipcMain, shell, nativeImage } = require('electron')
const path = require('path')
const { spawn } = require('child_process')
const fs = require('fs')
const http = require('http')

let mainWindow
let dotnetProcess

const API_URL = 'http://localhost:5001'
const CLIENT_URL = process.env.NODE_ENV === 'development'
  ? 'http://localhost:3000'
  : `file://${path.join(__dirname, '../mediaTagger.client/build/index.html')}`

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
  dotnetProcess = fs.existsSync(PUBLISHED_API)
    ? spawn(PUBLISHED_API, [], { cwd: apiPath, stdio: 'pipe' })
    : spawn('dotnet', ['run'], { cwd: apiPath, stdio: 'pipe' })
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
      http.get(`${API_URL}/health`, res => { res.resume(); resolve() })
        .on('error', () => {
          if (Date.now() < deadline) setTimeout(attempt, API_POLL_MS)
          else reject(new Error('API did not start'))
        })
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

  try { await waitForApi() } catch {}

  mainWindow.loadURL(CLIENT_URL)

  if (process.env.NODE_ENV === 'development') {
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

ipcMain.on('start-native-drag', (event, { filePath, previewDataUrl }) => {
  if (!filePath || !fs.existsSync(filePath)) return

  const icon = buildDragIcon(previewDataUrl, filePath)
  if (!icon) return

  try {
    event.sender.startDrag({ file: filePath, icon })
  } catch (err) {
    console.error(`[drag] could not start drag for ${filePath}: ${err.message}`)
  }
})

ipcMain.handle('select-folder', async () => {
  const { dialog } = require('electron')
  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openDirectory']
  })
  return result.canceled ? null : result.filePaths[0]
})

ipcMain.on('reveal-in-explorer', (event, filePath) => {
  shell.showItemInFolder(filePath)
})

// Hands the file to whatever the OS uses for it — the escape hatch for videos
// Chromium cannot decode.
ipcMain.on('open-file', (event, filePath) => {
  shell.openPath(filePath).then(err => {
    // Resolves with a non-empty string when nothing could open the file.
    if (err) console.error(`[open-file] ${filePath}: ${err}`)
  })
})

app.whenReady().then(async () => {
  if (process.env.NODE_ENV !== 'development') startDotnetApi()
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
