import axios from 'axios'

// Where the UI calls the API. Electron's runtime value wins (a packaged app
// pointed at a NAS), then the Vite env, then this machine.
const API_ORIGIN: string = (
  (typeof window !== 'undefined' ? window.electronAPI?.apiOrigin : undefined) ||
  import.meta.env.MEDIATAGGER_API_URL ||
  import.meta.env.VITE_API_ORIGIN ||
  'http://localhost:5001'
).replace(/\/$/, '')

function apiHostIsLocal(origin: string): boolean {
  try {
    const host = new URL(origin).hostname
    return host === 'localhost' || host === '127.0.0.1' || host === '::1'
  } catch {
    return true
  }
}

/** False when MEDIATAGGER_API_URL (or the Electron override) points at another machine. */
export const apiIsLocal = apiHostIsLocal(API_ORIGIN)

const api = axios.create({ baseURL: `${API_ORIGIN}/api` })

const accessKeyName = `mediatagger.accessKey:${API_ORIGIN}`

function readStoredKey(): string {
  try { return localStorage.getItem(accessKeyName)?.trim() ?? '' } catch { return '' }
}

function writeStoredKey(key: string) {
  try {
    const trimmed = key.trim()
    if (trimmed) localStorage.setItem(accessKeyName, trimmed)
    else localStorage.removeItem(accessKeyName)
  } catch { /* private mode: the key lasts for this page only if we kept it, and we don't */ }
}

export function readAccessKey(): string {
  return readStoredKey()
}

// img and video URLs cannot carry a header. The key is added when the URL is built,
// which is at render time, so a key entered later is picked up on the next paint.
function withAccessKey(url: string): string {
  const key = readStoredKey()
  if (!key) return url
  const join = url.includes('?') ? '&' : '?'
  return `${url}${join}access_key=${encodeURIComponent(key)}`
}

/** Picks one frame of a video; photos (no time) get nothing. */
function frameParam(time?: number | null): string {
  return time == null ? '' : `&t=${time.toFixed(3)}`
}

const accessDenied = new Set<() => void>()

/** Fired when a network API rejects the saved key. Localhost never does this. */
export function onAccessDenied(listener: () => void): () => void {
  accessDenied.add(listener)
  return () => accessDenied.delete(listener)
}

api.interceptors.request.use(config => {
  const key = readStoredKey()
  if (key) config.headers['X-MediaTagger-Key'] = key
  return config
})

api.interceptors.response.use(response => response, error => {
  if (!apiIsLocal && axios.isAxiosError(error) && error.response?.status === 401)
    accessDenied.forEach(listener => listener())
  return Promise.reject(error)
})

export type ServerReach = 'open' | 'locked' | 'down'

/** `/health` is open on purpose, so the window can tell "needs a key" from "nothing is listening". */
export async function checkServer(): Promise<ServerReach> {
  try {
    const res = await axios.get(`${API_ORIGIN}/health`, { timeout: 5000 })
    return res.data?.auth === true ? 'locked' : 'open'
  } catch {
    return 'down'
  }
}

export async function unlockServer(key: string): Promise<'ok' | 'rejected' | 'down'> {
  writeStoredKey(key)
  try {
    await api.get('/settings')
    return 'ok'
  } catch (error) {
    writeStoredKey('')
    if (axios.isAxiosError(error) && error.response?.status === 401) return 'rejected'
    return 'down'
  }
}

export interface Coordinates { lat: number; lng: number }
export interface FileTags {
  people: string[]; place: string; groups: string[]
  albums: string[]; coordinates?: Coordinates; notes: string
  dateTaken?: string
}
export interface ExifData {
  gpsCoordinates?: Coordinates; dateTaken?: string; camera?: string
}
export interface MediaFile {
  fileName: string; filePath: string; relativePath: string
  type: 'image' | 'video'; size: number; modified: string
  tags: FileTags; exif?: ExifData
}
export interface FolderNode { name: string; path: string; children: FolderNode[] }
export interface AllTagsResponse { people: string[]; groups: string[]; albums: string[]; places: string[] }
export interface SearchRequest {
  query?: string; people?: string[]; place?: string
  albums?: string[]; groups?: string[]; rootPath: string
  rootPaths?: string[]
}
export interface AlbumStat  { name: string; count: number; coverPath?: string }
export interface PersonStat { name: string; count: number; coverPath?: string }
export interface PlaceStat  { name: string; count: number; coverPath?: string }
export interface GroupStat  { name: string; count: number; coverPath?: string }
export interface LibraryStats {
  albums: AlbumStat[]; people: PersonStat[]
  places: PlaceStat[]; groups: GroupStat[]
}
/** How the face scan looks at videos: skip them, cover frame only, frames across the clip, or more of them. */
export type VideoFaceMode = 'Off' | 'Cover' | 'Frames' | 'Detailed'
export interface AppSettings {
  defaultFolder: string; recentFolders: string[]; libraryFolders?: string[]
  videoFaces?: VideoFaceMode
}
export interface ScanProgress {
  active: boolean
  processed: number
  total: number
  folder: string
}
export interface BulkTagsRequest {
  filePaths: string[]
  people: string[]
  groups: string[]
  albums: string[]
  place?: string
}

export interface FaceScanStatus {
  available: boolean
  active: boolean
  root: string
  processed: number
  total: number
}
/** Where a face is: a box relative (0..1) to the picture, and for a video the frame (seconds in) it is on. */
export interface FaceBox { x: number; y: number; w: number; h: number; time?: number | null }
/** A face in a file. Width/height are the pixels the box was measured on. */
export interface FaceSuggestion extends FaceBox {
  faceId: number; filePath: string; fileName: string; type: 'image' | 'video'
  width: number; height: number
  person: string; score: number
}
export interface PersonSuggestions { person: string; total: number; items: FaceSuggestion[] }
export interface LearnedPerson { name: string; faces: number; cover?: FaceSuggestion }
export interface FaceSuggestionsResponse {
  people: PersonSuggestions[]; learned: LearnedPerson[]; facesTotal: number
}
export interface FileFace extends FaceBox {
  faceId: number
  person?: string; suggestion?: string; score: number
}
export interface FileFacesResponse { width: number; height: number; faces: FileFace[] }
export interface FaceDecision { faceId: number; filePath: string; person: string }

export interface PlaceCandidate {
  place: string
  /** Share (0..1) of the nearby evidence pointing at this place. */
  confidence: number
  /** Metres to the closest photo already tagged with it. */
  distance: number
  support: number
  /** Clear winner with a tagged photo right there. */
  confident: boolean
}
export interface PlaceSuggestion {
  filePath: string; fileName: string; type: 'image' | 'video'
  place: string; confidence: number; distance: number; confident: boolean
  alternatives: string[]
}
export interface PlaceSuggestionGroup { place: string; items: PlaceSuggestion[] }
export interface PlaceSuggestionsResponse {
  places: PlaceSuggestionGroup[]; knownPlaces: number; unplaced: number
}

// Two preview sizes cover the whole app. Every view that shows a tile-sized picture
// asks for the same one, so it is made once and then comes out of the browser cache;
// the server cuts the small one from it rather than from the original.
export function asRoots(root: string | string[] | undefined | null): string[] {
  if (!root) return []
  return (Array.isArray(root) ? root : [root]).filter(Boolean)
}

function rootQuery(root: string | string[], extra?: Record<string, string | number | undefined>) {
  const params = new URLSearchParams()
  for (const path of asRoots(root)) params.append('rootPath', path)
  if (extra) {
    for (const [key, value] of Object.entries(extra)) {
      if (value !== undefined && value !== '') params.append(key, String(value))
    }
  }
  return params
}

export const THUMB_SIZE = 480
export const THUMB_SIZE_SMALL = 160

export const mediaApi = {
  getFiles: (folderPath: string, recursive = false) =>
    api.get<MediaFile[]>('/media/files', { params: { folderPath, recursive } }),
  getFolderTree: (rootPath: string) =>
    api.get<FolderNode>('/media/folder-tree', { params: { rootPath } }),
  updateTags: (filePath: string, tags: FileTags) =>
    api.put('/media/tags', { filePath, tags }),
  // Adds tags across a selection; each file keeps its own notes, date and coordinates.
  bulkAddTags: (req: BulkTagsRequest) =>
    api.put<number>('/media/tags/bulk', req),
  getScanProgress: () =>
    api.get<ScanProgress>('/media/scan-progress'),
  folderExists: (folderPath: string) =>
    api.get<boolean>('/media/folder-exists', { params: { folderPath } }),
  deleteFile: (filePath: string) =>
    api.delete('/media/file', { params: { filePath } }),
  search: (req: SearchRequest) =>
    api.post<MediaFile[]>('/media/search', req),
  getAllTags: (roots: string | string[]) =>
    api.get<AllTagsResponse>('/media/all-tags', { params: rootQuery(roots) }),
  getMapFiles: (roots: string | string[]) =>
    api.get<MediaFile[]>('/media/map-files', { params: rootQuery(roots) }),
  getLibraryStats: (roots: string | string[]) =>
    api.get<LibraryStats>('/media/library-stats', { params: rootQuery(roots) }),

  // Thumbnail — JPEG for all formats (HEIC converted, video frame extracted).
  // maxSize bounds the longest edge; pass it everywhere except the lightbox, which
  // needs the original. The server caches each size separately.
  // For a video, time (seconds) picks the frame instead of the cover.
  getThumbnailUrl: (filePath: string, maxSize?: number, time?: number | null) =>
    withAccessKey(
      `${API_ORIGIN}/api/media/thumbnail?filePath=${encodeURIComponent(filePath)}` +
      (maxSize ? `&maxSize=${maxSize}` : '') + frameParam(time)),

  // Stream — for video playback, supports HTTP Range (seeking)
  getStreamUrl: (filePath: string) =>
    withAccessKey(`${API_ORIGIN}/api/media/stream?filePath=${encodeURIComponent(filePath)}`)
}

export const facesApi = {
  // Idempotent: files already analysed are skipped, so this is safe to call on every open.
  scan: (roots: string | string[]) =>
    api.post<FaceScanStatus>('/faces/scan', null, { params: rootQuery(roots) }),
  stop: () => api.post<FaceScanStatus>('/faces/stop'),
  status: () => api.get<FaceScanStatus>('/faces/status'),
  suggestions: (roots: string | string[], perPerson?: number) =>
    api.get<FaceSuggestionsResponse>('/faces/suggestions', { params: rootQuery(roots, { perPerson }) }),
  file: (roots: string | string[], filePath: string) =>
    api.get<FileFacesResponse>('/faces/file', { params: rootQuery(roots, { filePath }) }),
  // applyTags=false when the caller writes the tags itself (the side panel does).
  accept: (items: FaceDecision[], applyTags = true) =>
    api.post<number>('/faces/accept', { items, applyTags }),
  reject: (items: FaceDecision[]) =>
    api.post<number>('/faces/reject', items),
  // One name, or several folded into one. Tags under the roots and confirmed faces both move.
  renamePeople: (roots: string | string[], from: string[], to: string) =>
    api.post<number>('/faces/people', { rootPaths: asRoots(roots), from, to }),
  // A square JPEG around one face, cut from the file's thumbnail (a video's from the frame it was seen in).
  getCropUrl: (filePath: string, box: FaceBox, size: number) =>
    withAccessKey(
      `${API_ORIGIN}/api/faces/crop?filePath=${encodeURIComponent(filePath)}` +
      `&x=${box.x.toFixed(4)}&y=${box.y.toFixed(4)}&w=${box.w.toFixed(4)}&h=${box.h.toFixed(4)}&size=${size}` +
      frameParam(box.time))
}

// Accepting a place is a plain bulk tag write — mediaApi.bulkAddTags with `place`.
export const placesApi = {
  suggestions: (roots: string | string[]) =>
    api.get<PlaceSuggestionsResponse>('/places/suggestions', { params: rootQuery(roots) }),
  nearby: (roots: string | string[], lat: number, lng: number) =>
    api.get<PlaceCandidate[]>('/places/nearby', { params: rootQuery(roots, { lat, lng }) })
}

export const settingsApi = {
  get: () => api.get<AppSettings>('/settings'),
  save: (settings: AppSettings) => api.put('/settings', settings)
}
