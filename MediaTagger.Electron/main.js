const { app, BrowserWindow, ipcMain, shell, nativeImage } = require('electron')
const path = require('path')
const { spawn } = require('child_process')
const http = require('http')

let mainWindow
let dotnetProcess

const API_URL = 'http://localhost:5001'
const CLIENT_URL = process.env.NODE_ENV === 'development'
  ? 'http://localhost:3000'
  : `file://${path.join(__dirname, '../mediaTagger.client/build/index.html')}`

function startDotnetApi() {
  const apiPath = path.join(__dirname, '../MediaTagger.API')
  dotnetProcess = spawn('dotnet', ['run'], {
    cwd: apiPath,
    stdio: 'pipe'
  })

  dotnetProcess.stdout.on('data', (data) => {
    console.log(`[API] ${data}`)
  })

  dotnetProcess.stderr.on('data', (data) => {
    console.error(`[API ERR] ${data}`)
  })
}

function waitForApi(retries = 20) {
  return new Promise((resolve, reject) => {
    const attempt = () => {
      http.get(`${API_URL}/api/media/files?folderPath=.`, (res) => {
        resolve()
      }).on('error', () => {
        if (retries-- > 0) setTimeout(attempt, 1000)
        else reject(new Error('API did not start'))
      })
    }
    attempt()
  })
}

async function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 900,
    minHeight: 600,
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

  try {
    await waitForApi()
  } catch (e) {
    console.warn('API not ready, loading anyway')
  }

  mainWindow.loadURL(CLIENT_URL)

  if (process.env.NODE_ENV === 'development') {
    mainWindow.webContents.openDevTools()
  }
}

// Native drag & drop — this is the key feature
// Electron's startDrag sends a real OS-level file drag
ipcMain.on('start-native-drag', async (event, { filePath, thumbnailPath }) => {
  let icon
  try {
    icon = nativeImage.createFromPath(thumbnailPath || filePath)
    if (icon.isEmpty()) {
      icon = nativeImage.createFromPath(path.join(__dirname, 'assets/file-icon.png'))
    }
    // Resize to reasonable drag icon size
    icon = icon.resize({ width: 64, height: 64 })
  } catch {
    icon = nativeImage.createEmpty()
  }

  event.sender.startDrag({
    file: filePath,
    icon
  })
})

// Open folder dialog
ipcMain.handle('select-folder', async () => {
  const { dialog } = require('electron')
  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openDirectory']
  })
  return result.canceled ? null : result.filePaths[0]
})

// Reveal file in Finder/Explorer
ipcMain.on('reveal-in-explorer', (event, filePath) => {
  shell.showItemInFolder(filePath)
})

// Open file with default app
ipcMain.on('open-file', (event, filePath) => {
  shell.openPath(filePath)
})

app.whenReady().then(async () => {
  if (process.env.NODE_ENV !== 'development') {
    startDotnetApi()
  }
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
