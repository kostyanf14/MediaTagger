import React, { useState, useEffect, useCallback, useMemo } from 'react'
import {
  Box, CssBaseline, ThemeProvider, createTheme, LinearProgress,
  Typography, Tabs, Tab, Divider, Button, TextField,
  Menu, MenuItem, Tooltip, IconButton, Chip
} from '@mui/material'
import FolderIcon from '@mui/icons-material/Folder'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import MapIcon from '@mui/icons-material/Map'
import PhotoLibraryIcon from '@mui/icons-material/PhotoLibrary'
import BookmarkIcon from '@mui/icons-material/Bookmark'
import BookmarkBorderIcon from '@mui/icons-material/BookmarkBorder'
import HistoryIcon from '@mui/icons-material/History'
import CollectionsIcon from '@mui/icons-material/Collections'
import FaceIcon from '@mui/icons-material/Face'
import AddLocationAltIcon from '@mui/icons-material/AddLocationAlt'
import { FolderTreeNode } from './components/FolderTree'
import { MediaGallery } from './components/MediaGallery'
import { TagsSidePanel } from './components/TagsSidePanel'
import { SearchBar } from './components/SearchBar'
import { MapView } from './components/MapView'
import { LibraryPanel } from './components/LibraryPanel'
import { LightboxViewer } from './components/LightboxViewer'
import { FacesPanel } from './components/FacesPanel'
import { PlacesPanel } from './components/PlacesPanel'
import {
  mediaApi, settingsApi, facesApi, apiIsLocal, AppSettings, ScanProgress,
  MediaFile, FileTags, FolderNode, AllTagsResponse, SearchRequest, BulkTagsRequest
} from './api/client'
import { sortFiles, groupFiles, SortField, SortDir, GroupField } from './utils/mediaOrder'
import { useElectron } from './hooks/useElectron'

const darkTheme = createTheme({
  palette: {
    mode: 'dark',
    primary: { main: '#2196f3' },
    background: { default: '#0f0f0f', paper: '#1a1a1a' }
  },
  shape: { borderRadius: 8 },
  typography: { fontFamily: '"Inter", "Roboto", sans-serif' }
})

const MAX_RECENT = 8
// Often enough to look live, rare enough to stay out of the scan's way.
const SCAN_POLL_MS = 300

/** Drops a trailing separator so the same folder is not stored two ways. */
function normalizeFolder(path: string): string {
  return path.replace(/[\\/]+$/, '')
}

/**
 * Windows treats paths case-insensitively and ignores a trailing separator, so one
 * folder can arrive as several different strings — the picker returns the on-disk
 * casing, while a remembered entry may have another. Compared raw, reopening a
 * folder appended a second copy instead of moving the existing one to the front.
 */
function folderKey(path: string): string {
  return normalizeFolder(path).replace(/\//g, '\\').toLowerCase()
}

/** Appends what is new, ignoring case-only repeats — the same rule the server applies. */
function mergeTagList(existing: string[], additions: string[]): string[] {
  const result = [...existing]
  for (const addition of additions) {
    const value = addition.trim()
    if (!value) continue
    if (!result.some(tag => tag.toLowerCase() === value.toLowerCase())) result.push(value)
  }
  return result
}

/** Adds values to a sorted suggestion list, returning the same array when nothing is new. */
function withSuggestions(list: string[], extra: string[], ignoreCase = false): string[] {
  const known = new Set(ignoreCase ? list.map(v => v.toLowerCase()) : list)
  const fresh: string[] = []
  for (const raw of extra) {
    const value = raw.trim()
    const key = ignoreCase ? value.toLowerCase() : value
    if (!value || known.has(key)) continue
    known.add(key)
    fresh.push(value)
  }
  return fresh.length ? [...list, ...fresh].sort((a, b) => a.localeCompare(b)) : list
}

export default function App() {
  const { selectFolder } = useElectron()
  const [settings, setSettings] = useState<AppSettings>({ defaultFolder: '', recentFolders: [] })
  const [rootPath, setRootPath] = useState('')
  const [pathDraft, setPathDraft] = useState('')
  const [folderTree, setFolderTree] = useState<FolderNode | null>(null)
  const [currentFolder, setCurrentFolder] = useState('')
  const [files, setFiles] = useState<MediaFile[]>([])
  const [selectedPaths, setSelectedPaths] = useState<string[]>([])
  const [lightboxFile, setLightboxFile] = useState<MediaFile | null>(null)
  const [allTags, setAllTags] = useState<AllTagsResponse>({ people: [], groups: [], albums: [], places: [] })
  const [loadingFiles, setLoadingFiles] = useState(false)
  const [tab, setTab] = useState(0) // 0=gallery 1=library 2=map 3=faces 4=places
  const [isSearching, setIsSearching] = useState(false)
  const [recentAnchor, setRecentAnchor] = useState<null | HTMLElement>(null)
  // Browsing a folder includes everything below it unless the user says otherwise.
  const [recursive, setRecursive] = useState(true)
  const [scan, setScan] = useState<ScanProgress | null>(null)
  const [sortField, setSortField] = useState<SortField>('dateTaken')
  // Oldest first: a capture-date view reads as a timeline running forwards.
  const [sortDir, setSortDir] = useState<SortDir>('asc')
  const [groupBy, setGroupBy] = useState<GroupField>('date')
  // Bumped on every tag change so views built from tags — the library counts, the
  // map pins — know their data is stale and can refetch when next shown.
  const [tagsVersion, setTagsVersion] = useState(0)
  const markTagsChanged = useCallback(() => setTagsVersion(v => v + 1), [])

  // Only poll while a load is in flight. The first scan of a large library reads
  // metadata from every file and takes seconds; cached ones finish before the first
  // tick, so the bar simply never appears.
  useEffect(() => {
    if (!loadingFiles) { setScan(null); return }
    let cancelled = false
    const tick = () => {
      mediaApi.getScanProgress()
        .then(r => { if (!cancelled) setScan(r.data.active ? r.data : null) })
        .catch(() => {})
    }
    const timer = setInterval(tick, SCAN_POLL_MS)
    tick()
    return () => { cancelled = true; clearInterval(timer) }
  }, [loadingFiles])

  // The selection is a set of paths; the file objects come from the current list, so
  // they stay fresh when tags change without anything having to re-point them.
  const selection = useMemo(() => {
    const chosen = new Set(selectedPaths)
    return files.filter(f => chosen.has(f.filePath))
  }, [files, selectedPaths])

  // The order on screen. It lives here rather than in the gallery because the
  // lightbox has to step through photos in the same order the grid shows them.
  const sorted = useMemo(() => sortFiles(files, sortField, sortDir), [files, sortField, sortDir])
  const groups = useMemo(() => groupFiles(sorted, groupBy), [sorted, groupBy])
  const orderedFiles = useMemo(
    () => (groupBy === 'none' ? sorted : groups.flatMap(g => g.files)),
    [sorted, groups, groupBy]
  )

  useEffect(() => {
    settingsApi.get().then(async res => {
      const s = res.data
      setSettings(s)
      // Prefer the pinned folder, then the most recently opened one. Each is checked
      // first: a library on an external drive may simply not be plugged in today.
      const candidates = [s.defaultFolder, ...s.recentFolders].filter(Boolean)
      for (const candidate of candidates) {
        const reachable = await mediaApi.folderExists(candidate)
          .then(r => r.data)
          // If the probe itself fails, trying the folder beats opening nothing.
          .catch(() => true)
        if (reachable) { openFolder(candidate, s); return }
      }
    }).catch(() => {})
  }, [])

  const saveSettings = useCallback((updated: AppSettings) => {
    setSettings(updated)
    settingsApi.save(updated).catch(() => {})
  }, [])

  const addToRecent = useCallback((path: string, current: AppSettings): AppSettings => {
    const folder = normalizeFolder(path)

    // Keep the newest first, then everything else in order, skipping anything that
    // is the same folder written differently. This also clears out duplicates that
    // an earlier build may have left in the list.
    const seen = new Set([folderKey(folder)])
    const recent = [folder]
    for (const previous of current.recentFolders) {
      const key = folderKey(previous)
      if (seen.has(key)) continue
      seen.add(key)
      recent.push(previous)
    }

    return { ...current, recentFolders: recent.slice(0, MAX_RECENT) }
  }, [])

  const loadFolderTree = useCallback(async (path: string) => {
    const res = await mediaApi.getFolderTree(path)
    setFolderTree(res.data)
  }, [])

  // withSubfolders is passed explicitly so a toggle can load with the new value
  // without waiting for the state update to land.
  const loadFiles = useCallback(async (folder: string, withSubfolders = recursive) => {
    setLoadingFiles(true)
    setIsSearching(false)
    try {
      const res = await mediaApi.getFiles(folder, withSubfolders)
      setFiles(res.data)
      setSelectedPaths([])
    } finally {
      setLoadingFiles(false)
    }
  }, [recursive])

  const loadAllTags = useCallback(async (path: string) => {
    const res = await mediaApi.getAllTags(path)
    setAllTags(res.data)
  }, [])

  const openFolder = useCallback(async (path: string, currentSettings?: AppSettings) => {
    setRootPath(path)
    setCurrentFolder(path)
    loadFolderTree(path)
    loadFiles(path)
    loadAllTags(path)
    // Finding faces runs in the background and skips files already done, so starting
    // it on every open is how new photos get picked up.
    facesApi.scan(path).catch(() => {})
    const base = currentSettings ?? settings
    saveSettings(addToRecent(path, base))
  }, [settings, addToRecent, saveSettings, loadFolderTree, loadFiles, loadAllTags])

  const handleRecursiveChange = (withSubfolders: boolean) => {
    setRecursive(withSubfolders)
    if (currentFolder) loadFiles(currentFolder, withSubfolders)
  }

  const handleSelectRoot = async () => {
    const path = await selectFolder()
    if (path) openFolder(path)
  }

  const handleDelete = async (file: MediaFile) => {
    await mediaApi.deleteFile(file.filePath)
    setSelectedPaths(prev => prev.filter(p => p !== file.filePath))
    if (lightboxFile?.filePath === file.filePath) setLightboxFile(null)
    // Drop it from the list directly — re-reading the folder would blank the grid.
    setFiles(prev => prev.filter(f => f.filePath !== file.filePath))
    loadAllTags(rootPath)
    markTagsChanged()
  }

  // Called from map lightbox "show in app" button
  const handleShowInApp = useCallback((file: MediaFile) => {
    const folder = file.filePath.replace(/[\/\\][^\/\\]+$/, '')
    setCurrentFolder(folder)
    loadFiles(folder)
    setTab(0)
    // Select the file after files load
    setTimeout(() => setSelectedPaths([file.filePath]), 300)
  }, [loadFiles])

  const handleSearch = async (req: SearchRequest | null) => {
    if (!req) { setIsSearching(false); loadFiles(currentFolder); return }
    setLoadingFiles(true)
    setIsSearching(true)
    try {
      const res = await mediaApi.search({ ...req, rootPath: rootPath || currentFolder })
      setFiles(res.data)
    } finally {
      setLoadingFiles(false)
    }
  }

  // Newly used tags go straight into the suggestion lists. Asking the server to
  // recount the whole library after every chip would say the same thing, later.
  const rememberTags = useCallback((used: { people?: string[]; groups?: string[]; albums?: string[]; place?: string }) => {
    setAllTags(prev => {
      const people = withSuggestions(prev.people, used.people ?? [])
      const groups = withSuggestions(prev.groups, used.groups ?? [])
      const albums = withSuggestions(prev.albums, used.albums ?? [])
      const places = withSuggestions(prev.places, used.place ? [used.place] : [], true)
      const unchanged = people === prev.people && groups === prev.groups
        && albums === prev.albums && places === prev.places
      return unchanged ? prev : { people, groups, albums, places }
    })
  }, [])

  // A bulk add is the same merge on every selected file, so it is applied to what
  // is already loaded instead of downloading the folder again.
  const handleTagsSaved = useCallback((applied: BulkTagsRequest) => {
    const touched = new Set(applied.filePaths)
    const place = applied.place?.trim()
    setFiles(prev => prev.map(f => (touched.has(f.filePath)
      ? {
          ...f,
          tags: {
            ...f.tags,
            people: mergeTagList(f.tags.people, applied.people),
            groups: mergeTagList(f.tags.groups, applied.groups),
            albums: mergeTagList(f.tags.albums, applied.albums),
            place: place || f.tags.place
          }
        }
      : f)))
    rememberTags(applied)
    markTagsChanged()
  }, [rememberTags, markTagsChanged])

  // A tag chip saved itself. Patch what is already loaded rather than reloading the
  // folder — loadFiles clears the selection, which would close the panel mid-edit.
  const handleTagsAutoSaved = useCallback((filePath: string, updated: FileTags) => {
    setFiles(prev => prev.map(f => (f.filePath === filePath ? { ...f, tags: updated } : f)))
    rememberTags(updated)
    markTagsChanged()
  }, [rememberTags, markTagsChanged])

  const handleLibraryFilter = (req: SearchRequest) => {
    setTab(0)
    handleSearch(req)
  }

  const isDefault = !!rootPath && folderKey(settings.defaultFolder) === folderKey(rootPath)

  return (
    <ThemeProvider theme={darkTheme}>
      <CssBaseline />
      <Box sx={{ display: 'flex', height: '100vh', overflow: 'hidden', bgcolor: 'background.default' }}>

        {/* Sidebar */}
        <Box sx={{
          width: 240, flexShrink: 0, bgcolor: 'background.paper',
          borderRight: '1px solid', borderColor: 'divider',
          display: 'flex', flexDirection: 'column'
        }}>
          <Box sx={{ p: 1.5, display: 'flex', alignItems: 'center', gap: 1, borderBottom: '1px solid', borderColor: 'divider' }}>
            <PhotoLibraryIcon sx={{ color: 'primary.main', fontSize: 20 }} />
            <Typography variant="subtitle2" fontWeight={700} sx={{ flex: 1 }}>MediaTagger</Typography>
          </Box>

          <Box sx={{ p: 1, display: 'flex', flexDirection: 'column', gap: 0.5 }}>
            {apiIsLocal && (
              <Button fullWidth variant="outlined" size="small"
                startIcon={<FolderIcon />} onClick={handleSelectRoot}>
                {rootPath ? 'Змінити папку' : 'Відкрити папку'}
              </Button>
            )}
            <TextField size="small" fullWidth placeholder="Шлях на сервері" value={pathDraft}
              onChange={e => setPathDraft(e.target.value)}
              onKeyDown={e => {
                if (e.key !== 'Enter' || !pathDraft.trim()) return
                openFolder(pathDraft.trim())
                setPathDraft('')
              }} />
            {settings.recentFolders.length > 0 && (
              <>
                <Button fullWidth variant="text" size="small"
                  startIcon={<HistoryIcon />}
                  onClick={e => setRecentAnchor(e.currentTarget)}
                  sx={{ justifyContent: 'flex-start', fontSize: '0.75rem', color: 'text.secondary' }}>
                  Нещодавні папки
                </Button>
                <Menu anchorEl={recentAnchor} open={!!recentAnchor}
                  onClose={() => setRecentAnchor(null)}
                  PaperProps={{ sx: { maxWidth: 360 } }}>
                  {settings.recentFolders.map(f => (
                    <MenuItem key={f} onClick={() => { openFolder(f); setRecentAnchor(null) }}
                      sx={{ fontSize: '0.8rem' }}>
                      <FolderOpenIcon fontSize="small" sx={{ mr: 1, color: 'warning.main', flexShrink: 0 }} />
                      <Typography variant="body2" noWrap title={f}>{f}</Typography>
                      {settings.defaultFolder === f && (
                        <BookmarkIcon fontSize="small" sx={{ ml: 1, color: 'primary.main', flexShrink: 0 }} />
                      )}
                    </MenuItem>
                  ))}
                </Menu>
              </>
            )}
          </Box>

          {rootPath && (
            <Box sx={{ px: 1, pb: 0.5, display: 'flex', alignItems: 'center', gap: 0.5 }}>
              <Typography variant="caption" color="text.secondary" noWrap sx={{ flex: 1 }} title={rootPath}>
                {rootPath.split(/[\\/]/).pop()}
              </Typography>
              <Tooltip title={isDefault ? 'Прибрати як дефолтну' : 'Встановити як дефолтну'}>
                <IconButton size="small"
                  onClick={() => saveSettings({ ...settings, defaultFolder: isDefault ? '' : rootPath })}
                  sx={{ color: isDefault ? 'primary.main' : 'text.disabled' }}>
                  {isDefault ? <BookmarkIcon fontSize="small" /> : <BookmarkBorderIcon fontSize="small" />}
                </IconButton>
              </Tooltip>
            </Box>
          )}

          {!rootPath && settings.defaultFolder && (
            <Box sx={{ px: 1, pb: 1 }}>
              <Chip icon={<BookmarkIcon />}
                label={settings.defaultFolder.split(/[\\/]/).pop()}
                size="small" color="primary" variant="outlined"
                onClick={() => openFolder(settings.defaultFolder)}
                title={settings.defaultFolder}
                sx={{ maxWidth: '100%', fontSize: '0.7rem' }} />
            </Box>
          )}

          <Box sx={{ flex: 1, overflowY: 'auto', py: 0.5 }}>
            {folderTree ? (
              <FolderTreeNode node={folderTree} selectedPath={currentFolder}
                onSelect={path => { setCurrentFolder(path); loadFiles(path) }} />
            ) : (
              <Typography variant="caption" color="text.secondary" sx={{ p: 2, display: 'block' }}>
                Оберіть кореневу папку з медіафайлами
              </Typography>
            )}
          </Box>
        </Box>

        {/* Main */}
        <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <Box sx={{ display: 'flex', alignItems: 'center', borderBottom: '1px solid',
            borderColor: 'divider', bgcolor: 'background.paper' }}>
            <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ minHeight: 44 }}>
              <Tab icon={<PhotoLibraryIcon fontSize="small" />} iconPosition="start"
                label="Галерея" sx={{ minHeight: 44, py: 0 }} />
              <Tab icon={<CollectionsIcon fontSize="small" />} iconPosition="start"
                label="Бібліотека" sx={{ minHeight: 44, py: 0 }} />
              <Tab icon={<MapIcon fontSize="small" />} iconPosition="start"
                label="Мапа" sx={{ minHeight: 44, py: 0 }} />
              <Tab icon={<FaceIcon fontSize="small" />} iconPosition="start"
                label="Обличчя" sx={{ minHeight: 44, py: 0 }} />
              <Tab icon={<AddLocationAltIcon fontSize="small" />} iconPosition="start"
                label="Місця" sx={{ minHeight: 44, py: 0 }} />
            </Tabs>
            <Divider orientation="vertical" flexItem sx={{ mx: 1 }} />
            {tab === 0 && (
              <Box sx={{ flex: 1 }}>
                <SearchBar allTags={allTags} rootPath={rootPath} onSearch={handleSearch} />
              </Box>
            )}
          </Box>

          {/* Indexing progress — the first scan of a folder is the slow one */}
          {scan && (
            <Box sx={{ px: 2, py: 1, borderBottom: '1px solid', borderColor: 'divider' }}>
              <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 0.5 }}>
                <Typography variant="caption" color="text.secondary">
                  Індексація медіафайлів…
                </Typography>
                <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>
                  {scan.processed.toLocaleString('uk-UA')} / {scan.total.toLocaleString('uk-UA')}
                </Typography>
              </Box>
              <LinearProgress
                // Total is known up front, so this can show real progress rather than
                // an endless barber pole.
                variant={scan.total > 0 ? 'determinate' : 'indeterminate'}
                value={scan.total > 0 ? (scan.processed / scan.total) * 100 : 0}
                sx={{ height: 4, borderRadius: 2 }}
              />
            </Box>
          )}

          <Box sx={{ flex: 1, overflow: 'hidden', display: 'flex' }}>
            <Box sx={{ flex: 1, overflow: 'auto', transition: 'margin-right 0.2s',
              mr: selection.length > 0 ? '340px' : 0 }}>

              {/* Gallery — always mounted, hidden when not active. It fills the pane
                  and scrolls inside itself, which is what lets it draw only the
                  rows in view. */}
              <Box sx={{ display: tab === 0 ? 'flex' : 'none', flexDirection: 'column', height: '100%' }}>
                {currentFolder && (
                  <Typography variant="caption" color="text.secondary"
                    sx={{ px: 2, pt: 1, display: 'block', flexShrink: 0 }}>
                    {isSearching ? `Результати пошуку: ${files.length} файлів` : currentFolder}
                  </Typography>
                )}
                <MediaGallery
                  groups={groups} orderedFiles={orderedFiles} loading={loadingFiles}
                  recursive={recursive} onRecursiveChange={handleRecursiveChange}
                  sortField={sortField} sortDir={sortDir} groupBy={groupBy}
                  onSortField={setSortField} onSortDir={setSortDir} onGroupBy={setGroupBy}
                  selectedPaths={selectedPaths}
                  onSelectionChange={setSelectedPaths}
                  onDoubleClick={setLightboxFile}
                  onDelete={handleDelete}
                />
              </Box>

              {/* Library — always mounted, hidden when not active */}
              <Box sx={{ display: tab === 1 ? 'block' : 'none', height: '100%' }}>
                <LibraryPanel rootPath={rootPath} refreshKey={tagsVersion}
                  active={tab === 1} onFilter={handleLibraryFilter} />
              </Box>

              {/* Map — always mounted, hidden when not active — preserves zoom/pan state */}
              <Box sx={{ display: tab === 2 ? 'block' : 'none', height: '100%' }}>
                <MapView rootPath={rootPath} currentFolder={currentFolder}
                  refreshKey={tagsVersion} active={tab === 2} onShowInApp={handleShowInApp} />
              </Box>

              {/* Faces — suggestions learned from the people already tagged */}
              <Box sx={{ display: tab === 3 ? 'block' : 'none', height: '100%' }}>
                <FacesPanel rootPath={rootPath} refreshKey={tagsVersion}
                  active={tab === 3} onTagsAdded={handleTagsSaved} />
              </Box>

              {/* Places — suggested from places already written on photos taken nearby */}
              <Box sx={{ display: tab === 4 ? 'block' : 'none', height: '100%' }}>
                <PlacesPanel rootPath={rootPath} refreshKey={tagsVersion}
                  active={tab === 4} onTagsAdded={handleTagsSaved} />
              </Box>
            </Box>

            <TagsSidePanel
              selection={selection}
              onClose={() => setSelectedPaths([])}
              onSaved={handleTagsSaved}
              onAutoSaved={handleTagsAutoSaved}
              allTags={allTags}
              rootPath={rootPath}
            />
          </Box>
        </Box>
      </Box>

      {/* Lightbox */}
      <LightboxViewer
        file={lightboxFile}
        files={orderedFiles}
        onClose={() => setLightboxFile(null)}
        onNavigate={setLightboxFile}
        onOpenTags={f => { setSelectedPaths([f.filePath]); setLightboxFile(null) }}
      />
    </ThemeProvider>
  )
}
