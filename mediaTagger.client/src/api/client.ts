import axios from 'axios'

const api = axios.create({ baseURL: 'http://localhost:5001/api' })

export interface Coordinates { lat: number; lng: number }
export interface FileTags {
  people: string[]
  place: string
  groups: string[]
  albums: string[]
  coordinates?: Coordinates
  notes: string
}
export interface ExifData {
  gpsCoordinates?: Coordinates
  dateTaken?: string
  camera?: string
}
export interface MediaFile {
  fileName: string
  filePath: string
  relativePath: string
  type: 'image' | 'video'
  size: number
  modified: string
  tags: FileTags
  exif?: ExifData
}
export interface FolderNode { name: string; path: string; children: FolderNode[] }
export interface AllTagsResponse { people: string[]; groups: string[]; albums: string[] }
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

export const mediaApi = {
  getFiles: (folderPath: string, recursive = false) =>
    api.get<MediaFile[]>('/media/files', { params: { folderPath, recursive } }),
  getFolderTree: (rootPath: string) =>
    api.get<FolderNode>('/media/folder-tree', { params: { rootPath } }),
  updateTags: (filePath: string, tags: FileTags) =>
    api.put('/media/tags', { filePath, tags }),
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
  getThumbnailUrl: (filePath: string) =>
    `http://localhost:5001/api/media/thumbnail?filePath=${encodeURIComponent(filePath)}`
}

export const settingsApi = {
  get: () => api.get<AppSettings>('/settings'),
  save: (settings: AppSettings) => api.put('/settings', settings)
}
