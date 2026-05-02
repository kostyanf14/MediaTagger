const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('electronAPI', {
  // Native drag — sends real file path to OS drag system
  startNativeDrag: (filePath, thumbnailPath) => {
    ipcRenderer.send('start-native-drag', { filePath, thumbnailPath })
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
