import { mediaApi } from '../api/client'

// Typed bridge to Electron APIs exposed via preload.js
declare global {
  interface Window {
    electronAPI?: {
      startNativeDrag: (filePath: string, previewDataUrl?: string) => void
      selectFolder: () => Promise<string | null>
      revealInExplorer: (filePath: string) => void
      openFile: (filePath: string) => void
      copyImage: (payload: { filePath?: string; dataUrl?: string }) => Promise<boolean>
      isElectron: boolean
      /** Set when this window was launched with MEDIATAGGER_API_URL. */
      apiOrigin?: string
    }
  }
}

/** PNG data URL of a photo the browser can decode, including one the API converted. */
async function pngDataUrl(filePath: string): Promise<string> {
  const res = await fetch(mediaApi.getThumbnailUrl(filePath))
  if (!res.ok) throw new Error('photo')
  const bitmap = await createImageBitmap(await res.blob())
  const canvas = document.createElement('canvas')
  canvas.width = bitmap.width
  canvas.height = bitmap.height
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('canvas')
  ctx.drawImage(bitmap, 0, 0)
  bitmap.close()
  return canvas.toDataURL('image/png')
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
    openFile: (filePath: string) => api?.openFile(filePath),
    // Electron writes the system clipboard Telegram pastes from. A browser tab uses
    // the clipboard API, which only some apps read.
    copyPhoto: async (filePath: string) => {
      if (api?.copyImage && await api.copyImage({ filePath })) return
      const dataUrl = await pngDataUrl(filePath)
      if (api?.copyImage) {
        if (!await api.copyImage({ dataUrl })) throw new Error('copy')
        return
      }
      const blob = await (await fetch(dataUrl)).blob()
      await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })])
    }
  }
}
