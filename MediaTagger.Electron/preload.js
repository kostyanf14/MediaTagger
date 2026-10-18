const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('electronAPI', {
  // Native drag — sends real file path to OS drag system.
  // previewDataUrl carries the drag icon for formats Chromium cannot decode.
  startNativeDrag: (filePath, previewDataUrl) => {
    ipcRenderer.send('start-native-drag', { filePath, previewDataUrl })
  },

  // Open folder picker dialog
  selectFolder: () => ipcRenderer.invoke('select-folder'),

  // Show file in Finder/Explorer
  revealInExplorer: (filePath) => {
    ipcRenderer.send('reveal-in-explorer', filePath)
  },

  // Open with default app
  openFile: (filePath) => {
    ipcRenderer.send('open-file', filePath)
  },

  // Check if running inside Electron
  isElectron: true
})
