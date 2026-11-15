/**
 * Controls that need the media file on this computer.
 * Hidden while the API is on another machine (a NAS). Review this list when adding a button.
 * Typing a server path still works, including any move that takes a path.
 */
export const REMOTE_HIDDEN_ACTIONS = [
  {
    id: 'select-library-folder',
    label: 'Відкрити папку / Додати папку',
    where: 'sidebar',
    reason: 'The folder dialog picks a directory on this PC. Type the path the server sees.'
  },
  {
    id: 'move-pick-folder',
    label: 'Обрати у діалозі переміщення',
    where: 'move dialog',
    reason: 'The dialog picks a directory on this PC. The destination can still be typed, and the move still runs.'
  },
  {
    id: 'reveal-in-explorer',
    label: 'Показати у провіднику',
    where: 'gallery menu, side panel, viewer',
    reason: 'Explorer would look for the server path on this PC.'
  },
  {
    id: 'open-local-file',
    label: 'Відкрити файл / зовнішній програвач',
    where: 'gallery menu, side panel, viewer',
    reason: 'The OS would open the path as this PC sees it.'
  },
  {
    id: 'native-drag',
    label: 'Перетягування файлу в іншу програму',
    where: 'gallery tile',
    reason: 'A native drag needs the file on this PC.'
  }
] as const

export type RemoteHiddenId = typeof REMOTE_HIDDEN_ACTIONS[number]['id']

export function remoteHides(id: RemoteHiddenId, apiIsLocal: boolean): boolean {
  return !apiIsLocal && REMOTE_HIDDEN_ACTIONS.some(action => action.id === id)
}
