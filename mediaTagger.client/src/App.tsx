import React, { useState, useEffect, useCallback } from 'react'
import {
  Box, CssBaseline, ThemeProvider, createTheme,
  Typography, Tabs, Tab, Divider, Button,
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
import { FolderTreeNode } from './components/FolderTree'
import { MediaGallery } from './components/MediaGallery'
import { TagsSidePanel } from './components/TagsSidePanel'
import { SearchBar } from './components/SearchBar'
import { MapView } from './components/MapView'
import { LibraryPanel } from './components/LibraryPanel'
import { LightboxViewer } from './components/LightboxViewer'
import {
  mediaApi, settingsApi, AppSettings,
  MediaFile, FolderNode, AllTagsResponse, SearchRequest
} from './api/client'
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

export default function App() {
  const { selectFolder } = useElectron()
  const [settings, setSettings] = useState<AppSettings>({ defaultFolder: '', recentFolders: [] })
  const [rootPath, setRootPath] = useState('')
  const [folderTree, setFolderTree] = useState<FolderNode | null>(null)
  const [currentFolder, setCurrentFolder] = useState('')
  const [files, setFiles] = useState<MediaFile[]>([])
  const [selectedFile, setSelectedFile] = useState<MediaFile | null>(null)
  const [lightboxFile, setLightboxFile] = useState<MediaFile | null>(null)
  const [allTags, setAllTags] = useState<AllTagsResponse>({ people: [], groups: [], albums: [] })
  const [loadingFiles, setLoadingFiles] = useState(false)
  const [tab, setTab] = useState(0) // 0=gallery 1=library 2=map
  const [isSearching, setIsSearching] = useState(false)
  const [recentAnchor, setRecentAnchor] = useState<null | HTMLElement>(null)

  useEffect(() => {
    settingsApi.get().then(res => {
      const s = res.data
      setSettings(s)
      if (s.defaultFolder) openFolder(s.defaultFolder, s)
    }).catch(() => {})
  }, [])

  const saveSettings = useCallback((updated: AppSettings) => {
    setSettings(updated)
    settingsApi.save(updated).catch(() => {})
  }, [])

  const addToRecent = useCallback((path: string, current: AppSettings): AppSettings => {
    const recent = [path, ...current.recentFolders.filter(f => f !== path)].slice(0, MAX_RECENT)
    return { ...current, recentFolders: recent }
  }, [])

  const loadFolderTree = useCallback(async (path: string) => {
    const res = await mediaApi.getFolderTree(path)
    setFolderTree(res.data)
  }, [])

  const loadFiles = useCallback(async (folder: string) => {
    setLoadingFiles(true)
    setIsSearching(false)
    try {
      const res = await mediaApi.getFiles(folder)
      setFiles(res.data)
      setSelectedFile(null)
    } finally {
      setLoadingFiles(false)
    }
  }, [])

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
    const base = currentSettings ?? settings
    saveSettings(addToRecent(path, base))
  }, [settings, addToRecent, saveSettings, loadFolderTree, loadFiles, loadAllTags])

  const handleSelectRoot = async () => {
    const path = await selectFolder()
    if (path) openFolder(path)
  }

  const handleDelete = async (file: MediaFile) => {
    await mediaApi.deleteFile(file.filePath)
    if (selectedFile?.filePath === file.filePath) setSelectedFile(null)
    if (lightboxFile?.filePath === file.filePath) setLightboxFile(null)
    loadFiles(currentFolder)
    loadAllTags(rootPath)
  }

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

  const handleTagsSaved = () => {
    loadFiles(currentFolder)
    if (rootPath) loadAllTags(rootPath)
    if (selectedFile) {
      mediaApi.getFiles(currentFolder).then(res => {
        const updated = res.data.find(f => f.filePath === selectedFile.filePath)
        if (updated) setSelectedFile(updated)
      })
    }
  }

  const handleLibraryFilter = (req: SearchRequest) => {
    setTab(0)
    handleSearch(req)
  }

  const isDefault = !!rootPath && settings.defaultFolder === rootPath

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
            <Button fullWidth variant="outlined" size="small"
              startIcon={<FolderIcon />} onClick={handleSelectRoot}>
              {rootPath ? 'Змінити папку' : 'Відкрити папку'}
            </Button>
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
            </Tabs>
            <Divider orientation="vertical" flexItem sx={{ mx: 1 }} />
            {tab === 0 && (
              <Box sx={{ flex: 1 }}>
                <SearchBar allTags={allTags} rootPath={rootPath} onSearch={handleSearch} />
              </Box>
            )}
          </Box>

          <Box sx={{ flex: 1, overflow: 'hidden', display: 'flex' }}>
            <Box sx={{ flex: 1, overflow: 'auto', transition: 'margin-right 0.2s',
              mr: selectedFile ? '340px' : 0 }}>
              {tab === 0 && (
                <>
                  {currentFolder && (
                    <Typography variant="caption" color="text.secondary"
                      sx={{ px: 2, pt: 1, display: 'block' }}>
                      {isSearching ? `Результати пошуку: ${files.length} файлів` : currentFolder}
                    </Typography>
                  )}
                  <MediaGallery
                    files={files} loading={loadingFiles}
                    selectedFile={selectedFile}
                    onSelect={setSelectedFile}
                    onDoubleClick={setLightboxFile}
                    onDelete={handleDelete}
                  />
                </>
              )}
              {tab === 1 && (
                <LibraryPanel rootPath={rootPath} onFilter={handleLibraryFilter} />
              )}
              {tab === 2 && <MapView rootPath={rootPath} />}
            </Box>

            <TagsSidePanel
              file={selectedFile}
              onClose={() => setSelectedFile(null)}
              onSaved={handleTagsSaved}
              allTags={allTags}
            />
          </Box>
        </Box>
      </Box>

      {/* Lightbox */}
      <LightboxViewer
        file={lightboxFile}
        files={files}
        onClose={() => setLightboxFile(null)}
        onNavigate={setLightboxFile}
        onOpenTags={f => { setSelectedFile(f); setLightboxFile(null) }}
      />
    </ThemeProvider>
  )
}
