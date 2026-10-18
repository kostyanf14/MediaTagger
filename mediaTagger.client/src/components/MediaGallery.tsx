import React, { memo, useCallback, useLayoutEffect, useMemo, useRef, useState } from 'react'
import {
  Box, Typography, Chip,
  Skeleton, IconButton, Menu, MenuItem, Select,
  FormControl, Dialog, Checkbox, FormControlLabel, Tooltip,
  DialogTitle, DialogContent, DialogActions, Button, Divider
} from '@mui/material'
import { styled } from '@mui/material/styles'
import { useVirtualizer } from '@tanstack/react-virtual'
import PlayCircleOutlineIcon from '@mui/icons-material/PlayCircleOutline'
import DragIndicatorIcon from '@mui/icons-material/DragIndicator'
import MoreVertIcon from '@mui/icons-material/MoreVert'
import DeleteIcon from '@mui/icons-material/Delete'
import SortIcon from '@mui/icons-material/Sort'
import GroupWorkIcon from '@mui/icons-material/GroupWork'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import { MediaFile, mediaApi, THUMB_SIZE } from '../api/client'
import { FileGroup, GroupField, SortDir, SortField } from '../utils/mediaOrder'
import { Thumbnail } from './Thumbnail'
import { useElectron } from '../hooks/useElectron'

// Tiles keep this shape at every window size — widening the window adds columns
// rather than stretching the previews.
const TILE_ASPECT = 4 / 3
// Columns are laid out from this width, so tiles stay roughly the same size
// whether the window is narrow or maximised.
const TILE_MIN_WIDTH = 200
const GRID_GAP = 8
const GRID_PADDING = 16
const GROUP_HEADER_HEIGHT = 48
// Rows kept mounted beyond each edge of the viewport, so a normal scroll never
// outruns the tiles.
const OVERSCAN_ROWS = 3
// Matches the size the main process shrinks drag icons to.
const DRAG_ICON_SIZE = 64

interface Props {
  /** Already sorted and grouped — the order here is the order on screen. */
  groups: FileGroup[]
  /** The same files as one run, for range selection. */
  orderedFiles: MediaFile[]
  loading: boolean
  recursive: boolean
  onRecursiveChange: (value: boolean) => void
  sortField: SortField
  sortDir: SortDir
  groupBy: GroupField
  onSortField: (value: SortField) => void
  onSortDir: (value: SortDir) => void
  onGroupBy: (value: GroupField) => void
  selectedPaths: string[]
  onSelectionChange: (paths: string[]) => void
  onDoubleClick: (file: MediaFile) => void
  onDelete: (file: MediaFile) => void
}

/**
 * Grabs the tile's already-decoded preview as a data URL for the OS drag icon.
 * Electron cannot decode HEIC or video itself, and startDrag has to happen inside
 * the gesture, so the pixels have to come from the DOM rather than another request.
 */
function captureDragPreview(tile: Element): string | undefined {
  const img = tile.querySelector('img')
  if (!img?.complete || !img.naturalWidth) return undefined
  try {
    const scale = DRAG_ICON_SIZE / Math.max(img.naturalWidth, img.naturalHeight)
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(img.naturalWidth * scale))
    canvas.height = Math.max(1, Math.round(img.naturalHeight * scale))
    canvas.getContext('2d')?.drawImage(img, 0, 0, canvas.width, canvas.height)
    return canvas.toDataURL('image/png')
  } catch {
    // Tainted canvas — the main process falls back to a generic icon.
    return undefined
  }
}

/** What the scroller lays out: a group heading, or one line of tiles. */
type Row =
  | { kind: 'header'; key: string; group: FileGroup }
  | { kind: 'tiles'; key: string; files: MediaFile[] }

function buildRows(groups: FileGroup[], columns: number): Row[] {
  const rows: Row[] = []
  for (const group of groups) {
    if (group.label) rows.push({ kind: 'header', key: `h:${group.label}`, group })
    for (let i = 0; i < group.files.length; i += columns) {
      const files = group.files.slice(i, i + columns)
      rows.push({ kind: 'tiles', key: `t:${files[0].filePath}`, files })
    }
  }
  return rows
}

// Plain CSS rather than sx: these styles are identical for every tile, and sx would
// work them out again for each one on each render.
const TileRoot = styled('div')(({ theme }) => ({
  cursor: 'pointer',
  borderRadius: theme.shape.borderRadius,
  overflow: 'hidden',
  position: 'relative',
  height: '100%',
  minWidth: 0,
  outline: '2px solid transparent',
  outlineOffset: 2,
  transition: 'transform 0.15s, outline-color 0.15s, opacity 0.15s',
  '&:hover': { transform: 'scale(1.02)' },
  '&:hover .drag-hint, &:hover .menu-btn': { opacity: 1 },
  '&[data-selected="true"]': { outlineColor: theme.palette.primary.main },
  '&[data-dragging="true"]': { opacity: 0.5 }
}))

interface TileProps {
  file: MediaFile
  selected: boolean
  dragging: boolean
  onClick: (e: React.MouseEvent, file: MediaFile) => void
  onDoubleClick: (file: MediaFile) => void
  onDragStart: (e: React.DragEvent, file: MediaFile) => void
  onMenu: (e: React.MouseEvent<HTMLElement>, file: MediaFile) => void
}

// Memoised so that selecting one photo re-renders that tile, not the whole screen.
const Tile = memo(function Tile({ file, selected, dragging, onClick, onDoubleClick, onDragStart, onMenu }: TileProps) {
  return (
    <TileRoot
      draggable
      data-selected={selected}
      data-dragging={dragging}
      onDragStart={e => onDragStart(e, file)}
      onClick={e => onClick(e, file)}
      onDoubleClick={() => onDoubleClick(file)}
    >
      <Thumbnail
        src={mediaApi.getThumbnailUrl(file.filePath, THUMB_SIZE)}
        alt={file.fileName}
        type={file.type}
        iconSize={40}
      />

      {/* Play badge — the frame alone does not read as a video */}
      {file.type === 'video' && (
        <Box sx={{
          position: 'absolute', inset: 0, pointerEvents: 'none',
          display: 'flex', alignItems: 'center', justifyContent: 'center'
        }}>
          <PlayCircleOutlineIcon sx={{
            fontSize: 48, color: 'rgba(255,255,255,0.92)',
            filter: 'drop-shadow(0 2px 6px rgba(0,0,0,0.7))'
          }} />
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
        onClick={e => onMenu(e, file)}
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
    </TileRoot>
  )
})

export function MediaGallery({
  groups, orderedFiles, loading, recursive, onRecursiveChange,
  sortField, sortDir, groupBy, onSortField, onSortDir, onGroupBy,
  selectedPaths, onSelectionChange, onDoubleClick, onDelete
}: Props) {
  const { startNativeDrag, isElectron, revealInExplorer, openFile } = useElectron()
  const [draggingPath, setDraggingPath] = useState<string | null>(null)
  const [menuAnchor, setMenuAnchor] = useState<{ el: HTMLElement; file: MediaFile } | null>(null)
  const [deleteConfirm, setDeleteConfirm] = useState<MediaFile | null>(null)
  // Held in state, not a ref: the scroller only exists once there are files to show,
  // and both the virtualizer and the width observer need to hear about it arriving.
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null)
  const [width, setWidth] = useState(0)

  const selectedSet = useMemo(() => new Set(selectedPaths), [selectedPaths])
  // Anchor for range selection: the last tile picked without shift.
  const anchor = useRef<string | null>(null)

  useLayoutEffect(() => {
    if (!scroller) return
    const measure = () => {
      // A hidden tab measures as zero; keep the last real width so the layout is
      // still there when the tab comes back.
      if (scroller.clientWidth > 0) setWidth(scroller.clientWidth)
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(scroller)
    return () => observer.disconnect()
  }, [scroller])

  const columns = Math.max(1, Math.floor((width - 2 * GRID_PADDING + GRID_GAP) / (TILE_MIN_WIDTH + GRID_GAP)))
  const tileWidth = Math.max(0, (width - 2 * GRID_PADDING - GRID_GAP * (columns - 1)) / columns)
  const tileHeight = Math.round(tileWidth / TILE_ASPECT)
  const rows = useMemo(() => buildRows(groups, columns), [groups, columns])

  // Only the rows near the viewport are mounted. Every row's height is known up
  // front, so the scrollbar is exact without measuring anything.
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scroller,
    estimateSize: index => (rows[index].kind === 'header' ? GROUP_HEADER_HEIGHT : tileHeight + GRID_GAP),
    overscan: OVERSCAN_ROWS,
    paddingStart: groupBy === 'none' ? GRID_PADDING : 0,
    paddingEnd: GRID_PADDING
  })

  // Row heights follow the window width and the grouping, so cached positions
  // have to be thrown away when either changes.
  useLayoutEffect(() => { virtualizer.measure() }, [virtualizer, rows, tileHeight])

  // The tile callbacks below must keep their identity or every tile re-renders, so
  // they read whatever is current from here instead of closing over it.
  const latest = useRef({ selectedPaths, selectedSet, orderedFiles, onSelectionChange, isElectron, startNativeDrag })
  latest.current = { selectedPaths, selectedSet, orderedFiles, onSelectionChange, isElectron, startNativeDrag }

  const handleTileClick = useCallback((e: React.MouseEvent, file: MediaFile) => {
    const { selectedPaths, selectedSet, orderedFiles, onSelectionChange } = latest.current
    const path = file.filePath

    if (e.shiftKey && anchor.current) {
      // Shift-select follows the order the tiles are actually drawn in, which is
      // the sorted list after grouping — not the order the server returned.
      const from = orderedFiles.findIndex(f => f.filePath === anchor.current)
      const to = orderedFiles.findIndex(f => f.filePath === path)
      if (from !== -1 && to !== -1) {
        const [start, end] = from < to ? [from, to] : [to, from]
        onSelectionChange(orderedFiles.slice(start, end + 1).map(f => f.filePath))
        return
      }
    }

    anchor.current = path

    if (e.ctrlKey || e.metaKey) {
      onSelectionChange(
        selectedSet.has(path)
          ? selectedPaths.filter(p => p !== path)
          : [...selectedPaths, path]
      )
      return
    }

    onSelectionChange([path])
  }, [])

  const handleDragStart = useCallback((e: React.DragEvent, file: MediaFile) => {
    const { isElectron, startNativeDrag } = latest.current
    if (isElectron) {
      e.preventDefault()
      setDraggingPath(file.filePath)
      startNativeDrag?.(file.filePath, captureDragPreview(e.currentTarget))
      setTimeout(() => setDraggingPath(null), 500)
    } else {
      e.dataTransfer.setData('text/plain', file.filePath)
    }
  }, [])

  const handleMenuOpen = useCallback((e: React.MouseEvent<HTMLElement>, file: MediaFile) => {
    e.stopPropagation()
    setMenuAnchor({ el: e.currentTarget, file })
  }, [])

  const handleDeleteConfirmed = () => {
    if (deleteConfirm) { onDelete(deleteConfirm); setDeleteConfirm(null) }
  }

  const renderHeader = (group: FileGroup) => {
    const chosen = group.files.reduce((n, f) => n + (selectedSet.has(f.filePath) ? 1 : 0), 0)
    const allChosen = chosen > 0 && chosen === group.files.length

    // The checkbox owns this group's membership and leaves the rest of the
    // selection alone, so several groups can be picked one after another.
    const toggleGroup = () => {
      const groupPaths = group.files.map(f => f.filePath)
      if (allChosen) {
        const dropped = new Set(groupPaths)
        onSelectionChange(selectedPaths.filter(p => !dropped.has(p)))
      } else {
        onSelectionChange([...selectedPaths, ...groupPaths.filter(p => !selectedSet.has(p))])
      }
    }

    return (
      <Box sx={{ display: 'flex', alignItems: 'flex-end', gap: 0.5, px: 2, pb: 0.5, height: '100%' }}>
        <Tooltip title={allChosen ? 'Зняти виділення групи' : 'Виділити всю групу'}>
          <Checkbox
            size="small"
            checked={allChosen}
            indeterminate={chosen > 0 && !allChosen}
            onChange={toggleGroup}
            sx={{ p: 0.5 }}
          />
        </Tooltip>
        <Typography variant="subtitle2" fontWeight={600} sx={{ pb: 0.5 }}>
          {group.label}
          <Typography component="span" variant="caption" color="text.secondary" sx={{ ml: 1 }}>
            ({group.files.length})
          </Typography>
        </Typography>
      </Box>
    )
  }

  const sortBar = (
    <SortBar sortField={sortField} sortDir={sortDir} onSortField={onSortField} onSortDir={onSortDir}
      groupBy={groupBy} onGroupBy={onGroupBy}
      recursive={recursive} onRecursiveChange={onRecursiveChange}
      selectedCount={selectedPaths.length} onClearSelection={() => onSelectionChange([])} />
  )

  let body: React.ReactNode
  if (loading) {
    body = (
      <Box sx={{
        display: 'grid', gap: 1, p: 2,
        gridTemplateColumns: `repeat(auto-fill, minmax(${TILE_MIN_WIDTH}px, 1fr))`
      }}>
        {Array.from({ length: 12 }).map((_, i) => (
          <Skeleton key={i} variant="rectangular"
            sx={{ width: '100%', height: 'auto', aspectRatio: '4 / 3', borderRadius: 1 }} />
        ))}
      </Box>
    )
  } else if (orderedFiles.length === 0) {
    body = (
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: 300 }}>
        <Typography color="text.secondary">
          {recursive
            ? 'Немає медіафайлів у цій папці та підпапках'
            : 'Немає медіафайлів у цій папці'}
        </Typography>
      </Box>
    )
  } else {
    body = (
      <div ref={setScroller} style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
          {virtualizer.getVirtualItems().map(item => {
            const row = rows[item.index]
            if (!row) return null
            return (
              <div
                key={row.key}
                style={{
                  position: 'absolute', top: 0, left: 0, width: '100%',
                  height: item.size, transform: `translateY(${item.start}px)`
                }}
              >
                {row.kind === 'header' ? renderHeader(row.group) : (
                  <div style={{
                    display: 'grid', gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
                    gap: GRID_GAP, padding: `0 ${GRID_PADDING}px`, height: tileHeight
                  }}>
                    {row.files.map(file => (
                      <Tile
                        key={file.filePath}
                        file={file}
                        selected={selectedSet.has(file.filePath)}
                        dragging={draggingPath === file.filePath}
                        onClick={handleTileClick}
                        onDoubleClick={onDoubleClick}
                        onDragStart={handleDragStart}
                        onMenu={handleMenuOpen}
                      />
                    ))}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      </div>
    )
  }

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {sortBar}
      {body}

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
    </Box>
  )
}

function SortBar({ sortField, sortDir, onSortField, onSortDir, groupBy, onGroupBy, recursive, onRecursiveChange, selectedCount, onClearSelection }: {
  sortField: SortField; sortDir: SortDir
  onSortField: (v: SortField) => void; onSortDir: (v: SortDir) => void
  groupBy: GroupField; onGroupBy: (v: GroupField) => void
  recursive: boolean; onRecursiveChange: (v: boolean) => void
  selectedCount: number; onClearSelection: () => void
}) {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, pt: 1, pb: 0.5, flexWrap: 'wrap', flexShrink: 0 }}>
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

      <Divider orientation="vertical" flexItem sx={{ my: 0.5 }} />

      <GroupWorkIcon fontSize="small" sx={{ color: 'text.secondary' }} />
      <FormControl size="small" sx={{ minWidth: 140 }}>
        <Select
          value={groupBy}
          onChange={e => onGroupBy(e.target.value as GroupField)}
          displayEmpty
          sx={{ fontSize: '0.8rem' }}
        >
          <MenuItem value="none">Без групування</MenuItem>
          <MenuItem value="date">За датою знімання</MenuItem>
          <MenuItem value="folder">За папкою</MenuItem>
        </Select>
      </FormControl>

      <Tooltip title="Показувати також файли з усіх вкладених папок">
        <FormControlLabel
          sx={{ ml: 0.5, mr: 0 }}
          control={
            <Checkbox
              size="small"
              checked={recursive}
              onChange={e => onRecursiveChange(e.target.checked)}
            />
          }
          label={
            <Typography variant="caption" color="text.secondary">
              Включно з підпапками
            </Typography>
          }
        />
      </Tooltip>

      {selectedCount > 1 ? (
        <Chip
          size="small"
          color="primary"
          label={`Вибрано: ${selectedCount}`}
          onDelete={onClearSelection}
          sx={{ ml: 'auto' }}
        />
      ) : (
        <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>
          Ctrl+клік — кілька, Shift+клік — діапазон
        </Typography>
      )}
    </Box>
  )
}
