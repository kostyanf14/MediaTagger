import React, { useEffect, useState, useCallback, useRef } from 'react'
import {
  Box, IconButton, Typography, Backdrop, Fade, Chip, Tooltip, Button
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import PhotoLibraryIcon from '@mui/icons-material/PhotoLibrary'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import VideocamOffOutlinedIcon from '@mui/icons-material/VideocamOffOutlined'
import ZoomInIcon from '@mui/icons-material/ZoomIn'
import ZoomOutIcon from '@mui/icons-material/ZoomOut'
import ZoomOutMapIcon from '@mui/icons-material/ZoomOutMap'
import LocationOnIcon from '@mui/icons-material/LocationOn'
import PhotoCameraIcon from '@mui/icons-material/PhotoCamera'
import { MediaFile, mediaApi, THUMB_SIZE, THUMB_SIZE_SMALL } from '../api/client'
import { Thumbnail } from './Thumbnail'
import { useElectron } from '../hooks/useElectron'

// Zoom limits for photo review. MIN_ZOOM is 'fits the screen'.
const MIN_ZOOM = 1
const MAX_ZOOM = 8
// Per wheel notch / button press.
const ZOOM_STEP = 1.25
const DOUBLE_CLICK_ZOOM = 2.5

interface Props {
  file: MediaFile | null
  files: MediaFile[]
  onClose: () => void
  onNavigate: (file: MediaFile) => void
  onOpenTags: (file: MediaFile) => void
  // Optional: label shown above filmstrip (e.g. "Поруч на мапі")
  contextLabel?: string
  // Navigate to file in gallery tab
  onShowInApp?: (file: MediaFile) => void
}

export function LightboxViewer({ file, files, onClose, onNavigate, onOpenTags, contextLabel, onShowInApp }: Props) {
  const { revealInExplorer, openFile, isElectron } = useElectron()
  // Set when the <video> element gives up on the file's codec.
  const [videoError, setVideoError] = useState(false)
  const [view, setView] = useState({ zoom: MIN_ZOOM, x: 0, y: 0 })
  const [panning, setPanning] = useState(false)
  // Which file's full-size picture has arrived; until then its tile preview stands in.
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  // Held so the browser does not drop the neighbours' downloads half-way.
  const preloaded = useRef<HTMLImageElement[]>([])
  const stageRef = useRef<HTMLDivElement | null>(null)
  const panStart = useRef<{ x: number; y: number; ox: number; oy: number } | null>(null)
  const currentIndex = file ? files.findIndex(f => f.filePath === file.filePath) : -1

  // Keeps the picture from being dragged off the stage entirely: the further it is
  // scaled up, the further it may travel.
  const clampOffset = useCallback((x: number, y: number, scale: number) => {
    const stage = stageRef.current
    const img = stage?.querySelector('img')
    if (!stage || !img) return { x, y }
    const maxX = Math.max(0, (img.offsetWidth * scale - stage.clientWidth) / 2)
    const maxY = Math.max(0, (img.offsetHeight * scale - stage.clientHeight) / 2)
    return {
      x: Math.min(maxX, Math.max(-maxX, x)),
      y: Math.min(maxY, Math.max(-maxY, y))
    }
  }, [])

  /**
   * Scales by `factor`, holding the point under (cx, cy) still — those are measured
   * from the centre of the stage, which is where the transform origin sits.
   */
  const zoomBy = useCallback((factor: number, cx = 0, cy = 0) => {
    setView(prev => {
      const zoom = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, prev.zoom * factor))
      if (zoom === prev.zoom) return prev
      if (zoom === MIN_ZOOM) return { zoom, x: 0, y: 0 }
      const ratio = zoom / prev.zoom
      const { x, y } = clampOffset(
        cx - (cx - prev.x) * ratio,
        cy - (cy - prev.y) * ratio,
        zoom
      )
      return { zoom, x, y }
    })
  }, [clampOffset])

  const resetZoom = useCallback(() => setView({ zoom: MIN_ZOOM, x: 0, y: 0 }), [])

  const goPrev = useCallback(() => {
    if (currentIndex > 0) onNavigate(files[currentIndex - 1])
  }, [currentIndex, files, onNavigate])

  const goNext = useCallback(() => {
    if (currentIndex < files.length - 1) onNavigate(files[currentIndex + 1])
  }, [currentIndex, files, onNavigate])

  // A new file deserves a fresh attempt at the built-in player, at 100%.
  useEffect(() => {
    setVideoError(false)
    setLoadedPath(null)
    resetZoom()
  }, [file?.filePath, resetZoom])

  // Stepping through photos is nearly always to the next or the previous one, so
  // both are fetched while the current one is being looked at.
  useEffect(() => {
    if (currentIndex === -1) return
    preloaded.current = [files[currentIndex + 1], files[currentIndex - 1]]
      .filter(f => f?.type === 'image')
      .map(f => {
        const img = new Image()
        // Same mode as the viewer's own <img>, so the two share a cache entry.
        img.crossOrigin = 'anonymous'
        img.decoding = 'async'
        img.src = mediaApi.getThumbnailUrl(f.filePath)
        return img
      })
  }, [currentIndex, files])

  // Registered by hand because React's onWheel is passive — it could not stop the
  // window itself from zooming on ctrl+wheel.
  useEffect(() => {
    const stage = stageRef.current
    if (!stage) return
    const onWheel = (e: WheelEvent) => {
      e.preventDefault()
      const rect = stage.getBoundingClientRect()
      zoomBy(
        e.deltaY < 0 ? ZOOM_STEP : 1 / ZOOM_STEP,
        e.clientX - (rect.left + rect.width / 2),
        e.clientY - (rect.top + rect.height / 2)
      )
    }
    stage.addEventListener('wheel', onWheel, { passive: false })
    return () => stage.removeEventListener('wheel', onWheel)
  }, [file?.filePath, file?.type, zoomBy])

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (!file) return
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowLeft') goPrev()
      if (e.key === 'ArrowRight') goNext()
      if (e.key === '+' || e.key === '=') zoomBy(ZOOM_STEP)
      if (e.key === '-' || e.key === '_') zoomBy(1 / ZOOM_STEP)
      if (e.key === '0') resetZoom()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [file, goPrev, goNext, onClose, zoomBy, resetZoom])

  if (!file) return null

  return (
    <Backdrop
      open={!!file}
      onClick={onClose}
      sx={{ zIndex: 1300, bgcolor: 'rgba(0,0,0,0.95)' }}
    >
      <Fade in={!!file}>
        <Box
          onClick={e => e.stopPropagation()}
          sx={{
            position: 'relative', display: 'flex', flexDirection: 'column',
            alignItems: 'center', width: '100vw', height: '100vh'
          }}
        >
          {/* Top bar */}
          <Box sx={{
            position: 'absolute', top: 0, left: 0, right: 0, zIndex: 10,
            display: 'flex', alignItems: 'center', gap: 1, p: 1.5,
            background: 'linear-gradient(rgba(0,0,0,0.75), transparent)'
          }}>
            <Typography variant="body2" color="white" sx={{ flex: 1 }} noWrap>
              {file.fileName}
              <Typography component="span" variant="caption" color="grey.400" sx={{ ml: 1 }}>
                {currentIndex + 1} / {files.length}
                {contextLabel && (
                  <Typography component="span" variant="caption" color="primary.light" sx={{ ml: 1 }}>
                    · {contextLabel}
                  </Typography>
                )}
              </Typography>
            </Typography>

            {/* Tags chips */}
            {file.tags.people.slice(0, 2).map(p => (
              <Chip key={p} label={p} size="small"
                sx={{ height: 20, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.15)', color: 'white' }} />
            ))}
            {file.tags.photographer && (
              <Tooltip title="Фотограф">
                <Chip
                  icon={<PhotoCameraIcon sx={{ fontSize: '12px !important', color: 'white !important' }} />}
                  label={file.tags.photographer} size="small"
                  sx={{ height: 20, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.1)', color: 'white' }}
                />
              </Tooltip>
            )}
            {file.tags.place && (
              <Chip
                icon={<LocationOnIcon sx={{ fontSize: '12px !important', color: 'white !important' }} />}
                label={file.tags.place} size="small"
                sx={{ height: 20, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.1)', color: 'white' }}
              />
            )}

            {/* Zoom — photos only */}
            {file.type === 'image' && (
              <>
                {view.zoom > MIN_ZOOM && (
                  <Typography variant="caption" color="grey.400" sx={{ minWidth: 44, textAlign: 'right' }}>
                    {Math.round(view.zoom * 100)}%
                  </Typography>
                )}
                <Tooltip title="Зменшити (−)">
                  <span>
                    <IconButton size="small" onClick={() => zoomBy(1 / ZOOM_STEP)}
                      disabled={view.zoom <= MIN_ZOOM} sx={{ color: 'white' }}>
                      <ZoomOutIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
                <Tooltip title="Збільшити (+)">
                  <span>
                    <IconButton size="small" onClick={() => zoomBy(ZOOM_STEP)}
                      disabled={view.zoom >= MAX_ZOOM} sx={{ color: 'white' }}>
                      <ZoomInIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
                <Tooltip title="Вмістити у вікно (0)">
                  <span>
                    <IconButton size="small" onClick={resetZoom}
                      disabled={view.zoom <= MIN_ZOOM} sx={{ color: 'white' }}>
                      <ZoomOutMapIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
              </>
            )}

            {/* External player — the built-in one cannot decode every codec */}
            {file.type === 'video' && isElectron && (
              <Tooltip title="Відкрити у зовнішньому програвачі">
                <IconButton size="small" onClick={() => openFile?.(file.filePath)}
                  sx={{ color: 'white' }}>
                  <OpenInNewIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            )}

            {/* Show in folder on disk */}
            <Tooltip title="Показати у провіднику (диск)">
              <IconButton size="small" onClick={() => revealInExplorer?.(file.filePath)}
                sx={{ color: 'white' }}>
                <FolderOpenIcon fontSize="small" />
              </IconButton>
            </Tooltip>

            {/* Show in app gallery */}
            {onShowInApp && (
              <Tooltip title="Показати у застосунку (галерея)">
                <IconButton size="small" onClick={() => { onShowInApp(file); onClose() }}
                  sx={{ color: 'white' }}>
                  <PhotoLibraryIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            )}

            {/* Tags panel */}
            <Tooltip title="Редагувати теги">
              <IconButton size="small" onClick={() => onOpenTags(file)} sx={{ color: 'white' }}>
                <InfoOutlinedIcon fontSize="small" />
              </IconButton>
            </Tooltip>

            <IconButton size="small" onClick={onClose} sx={{ color: 'white' }}>
              <CloseIcon />
            </IconButton>
          </Box>

          {/* Media */}
          <Box sx={{
            flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center',
            width: '100%', pt: 6, pb: 2
          }}>
            {file.type === 'image' ? (
              // Full resolution here — no maxSize — since this is the actual viewer.
              <Box
                ref={stageRef}
                onDoubleClick={() => (view.zoom > MIN_ZOOM ? resetZoom() : zoomBy(DOUBLE_CLICK_ZOOM))}
                onPointerDown={e => {
                  if (view.zoom <= MIN_ZOOM) return
                  // Capture keeps the drag alive past the edge of the stage, but a
                  // refusal must not cost us the pan itself.
                  try { e.currentTarget.setPointerCapture(e.pointerId) } catch { /* ignore */ }
                  panStart.current = { x: e.clientX, y: e.clientY, ox: view.x, oy: view.y }
                  setPanning(true)
                }}
                onPointerMove={e => {
                  const start = panStart.current
                  if (!start) return
                  setView(v => ({
                    ...v,
                    ...clampOffset(
                      start.ox + (e.clientX - start.x),
                      start.oy + (e.clientY - start.y),
                      v.zoom
                    )
                  }))
                }}
                onPointerUp={e => {
                  try { e.currentTarget.releasePointerCapture(e.pointerId) } catch { /* ignore */ }
                  panStart.current = null
                  setPanning(false)
                }}
                sx={{
                  width: '90vw', height: '85vh',
                  display: 'flex', alignItems: 'center', justifyContent: 'center',
                  overflow: 'hidden', touchAction: 'none', position: 'relative',
                  cursor: view.zoom > MIN_ZOOM ? (panning ? 'grabbing' : 'grab') : 'zoom-in'
                }}
              >
                {/* The grid has already loaded this preview, so it appears at once
                    and covers the wait for the original. */}
                {loadedPath !== file.filePath && (
                  <Box
                    component="img"
                    crossOrigin="anonymous"
                    src={mediaApi.getThumbnailUrl(file.filePath, THUMB_SIZE)}
                    alt=""
                    sx={{
                      position: 'absolute', inset: 0, width: '100%', height: '100%',
                      objectFit: 'contain', pointerEvents: 'none', userSelect: 'none'
                    }}
                  />
                )}
                <Thumbnail
                  src={mediaApi.getThumbnailUrl(file.filePath)}
                  alt={file.fileName}
                  objectFit="contain"
                  iconSize={64}
                  loading="eager"
                  onLoad={() => setLoadedPath(file.filePath)}
                  sx={{
                    width: 'auto', height: 'auto',
                    maxWidth: '100%', maxHeight: '100%',
                    bgcolor: 'transparent', borderRadius: 1, userSelect: 'none',
                    // The stage handles every pointer gesture, so the image must not
                    // start a native drag of its own.
                    pointerEvents: 'none',
                    transform: `translate(${view.x}px, ${view.y}px) scale(${view.zoom})`,
                    transformOrigin: 'center center',
                    transition: panning ? 'none' : 'transform 0.12s ease-out',
                    willChange: 'transform'
                  }}
                />
              </Box>
            ) : videoError ? (
              // Chromium only decodes a handful of codecs; the rest have to be
              // handed to whatever the system uses for video.
              <Box sx={{
                display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 2,
                p: 4, maxWidth: 480, textAlign: 'center'
              }}>
                <VideocamOffOutlinedIcon sx={{ fontSize: 56, color: 'grey.600' }} />
                <Typography variant="body1" color="grey.300">
                  Вбудований програвач не підтримує цей формат
                </Typography>
                <Typography variant="caption" color="grey.500">
                  {file.fileName}
                </Typography>
                {isElectron && (
                  <Button
                    variant="contained"
                    startIcon={<OpenInNewIcon />}
                    onClick={() => openFile?.(file.filePath)}
                  >
                    Відкрити у зовнішньому програвачі
                  </Button>
                )}
              </Box>
            ) : (
              <Box
                component="video"
                src={mediaApi.getStreamUrl(file.filePath)}
                controls autoPlay
                onError={() => setVideoError(true)}
                sx={{ maxWidth: '90vw', maxHeight: '85vh', outline: 'none' }}
              />
            )}
          </Box>

          {/* Prev / Next */}
          {currentIndex > 0 && (
            <IconButton onClick={goPrev} sx={{
              position: 'absolute', left: 16, top: '50%', transform: 'translateY(-50%)',
              bgcolor: 'rgba(0,0,0,0.5)', color: 'white',
              '&:hover': { bgcolor: 'rgba(0,0,0,0.85)' }
            }}>
              <ChevronLeftIcon sx={{ fontSize: 36 }} />
            </IconButton>
          )}
          {currentIndex < files.length - 1 && (
            <IconButton onClick={goNext} sx={{
              position: 'absolute', right: 16, top: '50%', transform: 'translateY(-50%)',
              bgcolor: 'rgba(0,0,0,0.5)', color: 'white',
              '&:hover': { bgcolor: 'rgba(0,0,0,0.85)' }
            }}>
              <ChevronRightIcon sx={{ fontSize: 36 }} />
            </IconButton>
          )}

          {/* Filmstrip */}
          {files.length > 1 && (
            <Box sx={{ pb: 1 }}>
              {contextLabel && (
                <Typography variant="caption" color="grey.500"
                  sx={{ display: 'block', textAlign: 'center', mb: 0.5 }}>
                  {contextLabel}
                </Typography>
              )}
              <Box sx={{
                display: 'flex', gap: 0.5, overflowX: 'auto', px: 2,
                maxWidth: '100vw', scrollbarWidth: 'none'
              }}>
                {files.slice(Math.max(0, currentIndex - 5), currentIndex + 6).map(f => (
                  <Box
                    key={f.filePath}
                    onClick={() => onNavigate(f)}
                    sx={{
                      flexShrink: 0, width: 56, height: 40, position: 'relative',
                      borderRadius: 0.5, overflow: 'hidden', cursor: 'pointer',
                      opacity: f.filePath === file.filePath ? 1 : 0.45,
                      outline: f.filePath === file.filePath ? '2px solid #2196f3' : 'none',
                      '&:hover': { opacity: 0.9 }, transition: 'opacity 0.15s'
                    }}
                  >
                    <Thumbnail src={mediaApi.getThumbnailUrl(f.filePath, THUMB_SIZE_SMALL)}
                      alt={f.fileName} type={f.type} iconSize={18} />
                    {f.type === 'video' && (
                      <Box sx={{
                        position: 'absolute', inset: 0, pointerEvents: 'none',
                        display: 'flex', alignItems: 'center', justifyContent: 'center'
                      }}>
                        <PlayArrowIcon sx={{
                          fontSize: 20, color: 'rgba(255,255,255,0.95)',
                          filter: 'drop-shadow(0 1px 3px rgba(0,0,0,0.8))'
                        }} />
                      </Box>
                    )}
                  </Box>
                ))}
              </Box>
            </Box>
          )}
        </Box>
      </Fade>
    </Backdrop>
  )
}
