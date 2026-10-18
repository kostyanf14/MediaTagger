import axios from 'axios'

const api = axios.create({ baseURL: 'http://localhost:5001/api' })

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
    `http://localhost:5001/api/media/thumbnail?filePath=${encodeURIComponent(filePath)}` +
    (maxSize ? `&maxSize=${maxSize}` : ''),

  // Stream — for video playback, supports HTTP Range (seeking)
  getStreamUrl: (filePath: string) =>
    `http://localhost:5001/api/media/stream?filePath=${encodeURIComponent(filePath)}`
}

export const settingsApi = {
  get: () => api.get<AppSettings>('/settings'),
  save: (settings: AppSettings) => api.put('/settings', settings)
}
