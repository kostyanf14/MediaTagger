import axios from 'axios'

// Overridable so a dev API can run beside the installed app, which holds port 5001.
const API_ORIGIN: string = import.meta.env.VITE_API_ORIGIN ?? 'http://localhost:5001'

const api = axios.create({ baseURL: `${API_ORIGIN}/api` })

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
}
export interface AlbumStat  { name: string; count: number; coverPath?: string }
export interface PersonStat { name: string; count: number; coverPath?: string }
export interface PlaceStat  { name: string; count: number; coverPath?: string }
export interface GroupStat  { name: string; count: number; coverPath?: string }
export interface LibraryStats {
  albums: AlbumStat[]; people: PersonStat[]
  places: PlaceStat[]; groups: GroupStat[]
}
export interface AppSettings { defaultFolder: string; recentFolders: string[] }
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
/** A face in a file. The box is relative (0..1); width/height are the pixels it was measured on. */
export interface FaceSuggestion {
  faceId: number; filePath: string; fileName: string; type: 'image' | 'video'
  x: number; y: number; w: number; h: number
  width: number; height: number
  person: string; score: number
}
export interface PersonSuggestions { person: string; total: number; items: FaceSuggestion[] }
export interface LearnedPerson { name: string; faces: number; cover?: FaceSuggestion }
export interface FaceSuggestionsResponse {
  people: PersonSuggestions[]; learned: LearnedPerson[]; facesTotal: number
}
export interface FileFace {
  faceId: number; x: number; y: number; w: number; h: number
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
  getAllTags: (rootPath: string) =>
    api.get<AllTagsResponse>('/media/all-tags', { params: { rootPath } }),
  getMapFiles: (rootPath: string) =>
    api.get<MediaFile[]>('/media/map-files', { params: { rootPath } }),
  getLibraryStats: (rootPath: string) =>
    api.get<LibraryStats>('/media/library-stats', { params: { rootPath } }),

  // Thumbnail — JPEG for all formats (HEIC converted, video frame extracted).
  // maxSize bounds the longest edge; pass it everywhere except the lightbox, which
  // needs the original. The server caches each size separately.
  getThumbnailUrl: (filePath: string, maxSize?: number) =>
    `${API_ORIGIN}/api/media/thumbnail?filePath=${encodeURIComponent(filePath)}` +
    (maxSize ? `&maxSize=${maxSize}` : ''),

  // Stream — for video playback, supports HTTP Range (seeking)
  getStreamUrl: (filePath: string) =>
    `${API_ORIGIN}/api/media/stream?filePath=${encodeURIComponent(filePath)}`
}

export const facesApi = {
  // Idempotent: files already analysed are skipped, so this is safe to call on every open.
  scan: (rootPath: string) =>
    api.post<FaceScanStatus>('/faces/scan', null, { params: { rootPath } }),
  stop: () => api.post<FaceScanStatus>('/faces/stop'),
  status: () => api.get<FaceScanStatus>('/faces/status'),
  suggestions: (rootPath: string, perPerson?: number) =>
    api.get<FaceSuggestionsResponse>('/faces/suggestions', { params: { rootPath, perPerson } }),
  file: (rootPath: string, filePath: string) =>
    api.get<FileFacesResponse>('/faces/file', { params: { rootPath, filePath } }),
  // applyTags=false when the caller writes the tags itself (the side panel does).
  accept: (items: FaceDecision[], applyTags = true) =>
    api.post<number>('/faces/accept', { items, applyTags }),
  reject: (items: FaceDecision[]) =>
    api.post<number>('/faces/reject', items),
  // A square JPEG around one face, cut from the file's thumbnail.
  getCropUrl: (filePath: string, box: { x: number; y: number; w: number; h: number }, size: number) =>
    `${API_ORIGIN}/api/faces/crop?filePath=${encodeURIComponent(filePath)}` +
    `&x=${box.x.toFixed(4)}&y=${box.y.toFixed(4)}&w=${box.w.toFixed(4)}&h=${box.h.toFixed(4)}&size=${size}`
}

// Accepting a place is a plain bulk tag write — mediaApi.bulkAddTags with `place`.
export const placesApi = {
  suggestions: (rootPath: string) =>
    api.get<PlaceSuggestionsResponse>('/places/suggestions', { params: { rootPath } }),
  nearby: (rootPath: string, lat: number, lng: number) =>
    api.get<PlaceCandidate[]>('/places/nearby', { params: { rootPath, lat, lng } })
}

export const settingsApi = {
  get: () => api.get<AppSettings>('/settings'),
  save: (settings: AppSettings) => api.put('/settings', settings)
}
