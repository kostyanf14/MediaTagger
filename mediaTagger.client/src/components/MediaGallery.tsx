import React, { useState, useMemo } from 'react'
import {
  Box, ImageList, ImageListItem, Typography, Chip,
  Skeleton, IconButton, Menu, MenuItem, Select,
  FormControl, InputLabel, Tooltip, Dialog,
  DialogTitle, DialogContent, DialogActions, Button
} from '@mui/material'
import PlayCircleOutlineIcon from '@mui/icons-material/PlayCircleOutline'
import DragIndicatorIcon from '@mui/icons-material/DragIndicator'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import DeleteIcon from '@mui/icons-material/Delete'
import SortIcon from '@mui/icons-material/Sort'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import { MediaFile, mediaApi } from '../api/client'
import { useElectron } from '../hooks/useElectron'

export type SortField = 'name' | 'modified' | 'dateTaken' | 'size'
export type SortDir = 'asc' | 'desc'

interface Props {
  files: MediaFile[]
  loading: boolean
  selectedFile: MediaFile | null
  onSelect: (file: MediaFile) => void
  onDoubleClick: (file: MediaFile) => void
  onDelete: (file: MediaFile) => void
}

function sortFiles(files: MediaFile[], field: SortField, dir: SortDir): MediaFile[] {
  const sorted = [...files].sort((a, b) => {
    let va: any, vb: any
    if (field === 'name')      { va = a.fileName.toLowerCase(); vb = b.fileName.toLowerCase() }
    if (field === 'modified')  { va = new Date(a.modified).getTime(); vb = new Date(b.modified).getTime() }
    if (field === 'dateTaken') { va = a.exif?.dateTaken ? new Date(a.exif.dateTaken).getTime() : 0; vb = b.exif?.dateTaken ? new Date(b.exif.dateTaken).getTime() : 0 }
    if (field === 'size')      { va = a.size; vb = b.size }
    if (va < vb) return -1
    if (va > vb) return 1
    return 0
  })
  return dir === 'desc' ? sorted.reverse() : sorted
}

export function MediaGallery({ files, loading, selectedFile, onSelect, onDoubleClick, onDelete }: Props) {
  const { startNativeDrag, isElectron, revealInExplorer, openFile } = useElectron()
  const [draggingPath, setDraggingPath] = useState<string | null>(null)
  const [sortField, setSortField] = useState<SortField>('name')
  const [sortDir, setSortDir] = useState<SortDir>('asc')
  const [menuAnchor, setMenuAnchor] = useState<{ el: HTMLElement; file: MediaFile } | null>(null)
  const [deleteConfirm, setDeleteConfirm] = useState<MediaFile | null>(null)

  const sorted = useMemo(() => sortFiles(files, sortField, sortDir), [files, sortField, sortDir])

  const handleDragStart = (e: React.DragEvent, file: MediaFile) => {
    if (isElectron) {
      e.preventDefault()
      setDraggingPath(file.filePath)
      startNativeDrag?.(file.filePath, mediaApi.getThumbnailUrl(file.filePath))
      setTimeout(() => setDraggingPath(null), 500)
    } else {
      e.dataTransfer.setData('text/plain', file.filePath)
    }
  }

  const handleMenuOpen = (e: React.MouseEvent<HTMLElement>, file: MediaFile) => {
    e.stopPropagation()
    setMenuAnchor({ el: e.currentTarget, file })
  }

  const handleDeleteConfirmed = () => {
    if (deleteConfirm) { onDelete(deleteConfirm); setDeleteConfirm(null) }
  }

  if (loading) {
    return (
      <>
        <SortBar sortField={sortField} sortDir={sortDir} onSortField={setSortField} onSortDir={setSortDir} />
        <ImageList cols={4} gap={8} sx={{ p: 2 }}>
          {Array.from({ length: 12 }).map((_, i) => (
            <Skeleton key={i} variant="rectangular" height={160} sx={{ borderRadius: 1 }} />
          ))}
        </ImageList>
      </>
    )
  }

  if (files.length === 0) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: 300 }}>
        <Typography color="text.secondary">Немає медіафайлів у цій папці</Typography>
      </Box>
    )
  }

  return (
    <>
      <SortBar sortField={sortField} sortDir={sortDir} onSortField={setSortField} onSortDir={setSortDir} />

      <ImageList cols={4} gap={8} sx={{ p: 2, m: 0 }}>
        {sorted.map(file => {
          const isSelected = selectedFile?.filePath === file.filePath
          const isDragging = draggingPath === file.filePath

          return (
            <ImageListItem
              key={file.filePath}
              draggable
              onDragStart={e => handleDragStart(e, file)}
              onClick={() => onSelect(file)}
              onDoubleClick={() => onDoubleClick(file)}
              sx={{
                cursor: 'pointer', borderRadius: 1, overflow: 'hidden',
                outline: isSelected ? '2px solid' : '2px solid transparent',
                outlineColor: isSelected ? 'primary.main' : 'transparent',
                outlineOffset: 2, opacity: isDragging ? 0.5 : 1,
                transition: 'all 0.15s',
                '&:hover .drag-hint': { opacity: 1 },
                '&:hover .menu-btn': { opacity: 1 },
                '&:hover': { transform: 'scale(1.02)' },
                position: 'relative'
              }}
            >
              {file.type === 'image' ? (
                <Box component="img"
                  src={mediaApi.getThumbnailUrl(file.filePath)}
                  alt={file.fileName} loading="lazy"
                  sx={{ width: '100%', height: 160, objectFit: 'cover', display: 'block', bgcolor: 'action.hover' }}
                />
              ) : (
                <Box sx={{ width: '100%', height: 160, bgcolor: 'grey.900',
                  display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <PlayCircleOutlineIcon sx={{ fontSize: 48, color: 'grey.400' }} />
                </Box>
              )}

              {/* Drag hint */}
              <Box className="drag-hint" sx={{
                position: 'absolute', top: 4, left: 4,
                opacity: 0, transition: 'opacity 0.2s',
                bgcolor: 'rgba(0,0,0,0.6)', borderRadius: 1, p: 0.3
              }}>
                <DragIndicatorIcon sx={{ fontSize: 16, color: 'white' }} />
              </Box>

              {/* Context menu button */}
              <IconButton
                className="menu-btn"
                size="small"
                onClick={e => handleMenuOpen(e, file)}
                sx={{
                  position: 'absolute', top: 2, right: 2,
                  opacity: 0, transition: 'opacity 0.2s',
                  bgcolor: 'rgba(0,0,0,0.6)', color: 'white', p: 0.3,
                  '&:hover': { bgcolor: 'rgba(0,0,0,0.85)' }
                }}
              >
                <MoreVertIcon sx={{ fontSize: 16 }} />
              </IconButton>

              {/* Tags overlay */}
              {file.tags.people.length > 0 && (
                <Box sx={{
                  position: 'absolute', bottom: 0, left: 0, right: 0,
                  background: 'linear-gradient(transparent, rgba(0,0,0,0.75))',
                  p: 0.8, display: 'flex', flexWrap: 'wrap', gap: 0.3
                }}>
                  {file.tags.people.slice(0, 2).map(p => (
                    <Chip key={p} label={p} size="small"
                      sx={{ height: 18, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.15)', color: 'white' }} />
                  ))}
                  {file.tags.people.length > 2 && (
                    <Chip label={`+${file.tags.people.length - 2}`} size="small"
                      sx={{ height: 18, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.1)', color: 'white' }} />
                  )}
                </Box>
              )}
            </ImageListItem>
          )
        })}
      </ImageList>

      {/* Context menu */}
      <Menu
        anchorEl={menuAnchor?.el}
        open={!!menuAnchor}
        onClose={() => setMenuAnchor(null)}
        onClick={() => setMenuAnchor(null)}
      >
        <MenuItem onClick={() => menuAnchor && openFile?.(menuAnchor.file.filePath)}>
          <OpenInNewIcon fontSize="small" sx={{ mr: 1 }} /> Відкрити
        </MenuItem>
        <MenuItem onClick={() => menuAnchor && revealInExplorer?.(menuAnchor.file.filePath)}>
          <FolderOpenIcon fontSize="small" sx={{ mr: 1 }} /> Показати у провіднику
        </MenuItem>
        <MenuItem
          onClick={() => { if (menuAnchor) setDeleteConfirm(menuAnchor.file) }}
          sx={{ color: 'error.main' }}
        >
          <DeleteIcon fontSize="small" sx={{ mr: 1 }} /> Видалити
        </MenuItem>
      </Menu>

      {/* Delete confirmation */}
      <Dialog open={!!deleteConfirm} onClose={() => setDeleteConfirm(null)}>
        <DialogTitle>Видалити файл?</DialogTitle>
        <DialogContent>
          <Typography>
            <strong>{deleteConfirm?.fileName}</strong> буде переміщено до кошика.
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setDeleteConfirm(null)}>Скасувати</Button>
          <Button onClick={handleDeleteConfirmed} color="error" variant="contained">
            Видалити
          </Button>
        </DialogActions>
      </Dialog>
    </>
  )
}

function SortBar({ sortField, sortDir, onSortField, onSortDir }: {
  sortField: SortField; sortDir: SortDir
  onSortField: (v: SortField) => void; onSortDir: (v: SortDir) => void
}) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, pt: 1 }}>
      <SortIcon fontSize="small" sx={{ color: 'text.secondary' }} />
      <FormControl size="small" sx={{ minWidth: 140 }}>
        <Select
          value={sortField}
          onChange={e => onSortField(e.target.value as SortField)}
          displayEmpty
          sx={{ fontSize: '0.8rem' }}
        >
          <MenuItem value="name">За назвою</MenuItem>
          <MenuItem value="modified">За датою змінення</MenuItem>
          <MenuItem value="dateTaken">За датою знімання</MenuItem>
          <MenuItem value="size">За розміром</MenuItem>
        </Select>
      </FormControl>
      <FormControl size="small" sx={{ minWidth: 100 }}>
        <Select
          value={sortDir}
          onChange={e => onSortDir(e.target.value as SortDir)}
          sx={{ fontSize: '0.8rem' }}
        >
          <MenuItem value="asc">↑ Зростання</MenuItem>
          <MenuItem value="desc">↓ Спадання</MenuItem>
        </Select>
      </FormControl>
      <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>
        Подвійний клік — перегляд
      </Typography>
    </Box>
  )
}
