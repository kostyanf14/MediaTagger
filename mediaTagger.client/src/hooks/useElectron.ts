// Typed bridge to Electron APIs exposed via preload.js
declare global {
  interface Window {
    electronAPI?: {
      startNativeDrag: (filePath: string, previewDataUrl?: string) => void
      selectFolder: () => Promise<string | null>
      revealInExplorer: (filePath: string) => void
      openFile: (filePath: string) => void
      isElectron: boolean
      /** Set when this window was launched with MEDIATAGGER_API_URL. */
      apiOrigin?: string
    }
  }
}

export function useElectron() {
  const api = window.electronAPI
  const isElectron = !!api?.isElectron

  return {
    isElectron,
    selectFolder: () => api?.selectFolder() ?? Promise.resolve(null),
    startNativeDrag: (filePath: string, previewDataUrl?: string) =>
      api?.startNativeDrag(filePath, previewDataUrl),
    revealInExplorer: (filePath: string) => api?.revealInExplorer(filePath),
    openFile: (filePath: string) => api?.openFile(filePath)
  }
}
