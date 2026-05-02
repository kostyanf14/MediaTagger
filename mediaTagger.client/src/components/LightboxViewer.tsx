import React, { useEffect, useCallback } from 'react'
import {
  Box, IconButton, Typography, Backdrop, Fade, Chip
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import { MediaFile, mediaApi } from '../api/client'

interface Props {
  file: MediaFile | null
  files: MediaFile[]
  onClose: () => void
  onNavigate: (file: MediaFile) => void
  onOpenTags: (file: MediaFile) => void
}

export function LightboxViewer({ file, files, onClose, onNavigate, onOpenTags }: Props) {
  const currentIndex = file ? files.findIndex(f => f.filePath === file.filePath) : -1

  const goPrev = useCallback(() => {
    if (currentIndex > 0) onNavigate(files[currentIndex - 1])
  }, [currentIndex, files, onNavigate])

  const goNext = useCallback(() => {
    if (currentIndex < files.length - 1) onNavigate(files[currentIndex + 1])
  }, [currentIndex, files, onNavigate])

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (!file) return
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowLeft') goPrev()
      if (e.key === 'ArrowRight') goNext()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [file, goPrev, goNext, onClose])

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
            position: 'relative',
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            width: '100vw',
            height: '100vh'
          }}
        >
          {/* Top bar */}
          <Box sx={{
            position: 'absolute', top: 0, left: 0, right: 0, zIndex: 10,
            display: 'flex', alignItems: 'center', gap: 1,
            p: 1.5,
            background: 'linear-gradient(rgba(0,0,0,0.7), transparent)'
          }}>
            <Typography variant="body2" color="white" sx={{ flex: 1 }} noWrap>
              {file.fileName}
              <Typography component="span" variant="caption" color="grey.400" sx={{ ml: 1 }}>
                {currentIndex + 1} / {files.length}
              </Typography>
            </Typography>
            {file.tags.people.length > 0 && file.tags.people.slice(0, 3).map(p => (
              <Chip key={p} label={p} size="small"
                sx={{ height: 20, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.15)', color: 'white' }} />
            ))}
            {file.tags.place && (
              <Chip label={file.tags.place} size="small"
                sx={{ height: 20, fontSize: '0.65rem', bgcolor: 'rgba(255,255,255,0.1)', color: 'white' }} />
            )}
            <IconButton size="small" onClick={() => onOpenTags(file)}
              sx={{ color: 'white' }}>
              <InfoOutlinedIcon fontSize="small" />
            </IconButton>
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
              <Box
                component="img"
                src={mediaApi.getThumbnailUrl(file.filePath)}
                alt={file.fileName}
                sx={{
                  maxWidth: '90vw',
                  maxHeight: '85vh',
                  objectFit: 'contain',
                  borderRadius: 1,
                  userSelect: 'none'
                }}
              />
            ) : (
              <Box
                component="video"
                src={mediaApi.getThumbnailUrl(file.filePath)}
                controls
                autoPlay
                sx={{
                  maxWidth: '90vw',
                  maxHeight: '85vh',
                  outline: 'none'
                }}
              />
            )}
          </Box>

          {/* Prev / Next */}
          {currentIndex > 0 && (
            <IconButton
              onClick={goPrev}
              sx={{
                position: 'absolute', left: 16, top: '50%', transform: 'translateY(-50%)',
                bgcolor: 'rgba(0,0,0,0.5)', color: 'white',
                '&:hover': { bgcolor: 'rgba(0,0,0,0.8)' }
              }}
            >
              <ChevronLeftIcon sx={{ fontSize: 36 }} />
            </IconButton>
          )}
          {currentIndex < files.length - 1 && (
            <IconButton
              onClick={goNext}
              sx={{
                position: 'absolute', right: 16, top: '50%', transform: 'translateY(-50%)',
                bgcolor: 'rgba(0,0,0,0.5)', color: 'white',
                '&:hover': { bgcolor: 'rgba(0,0,0,0.8)' }
              }}
            >
              <ChevronRightIcon sx={{ fontSize: 36 }} />
            </IconButton>
          )}

          {/* Bottom filmstrip */}
          <Box sx={{
            display: 'flex', gap: 0.5, overflowX: 'auto', pb: 1, px: 2,
            maxWidth: '100vw', scrollbarWidth: 'none'
          }}>
            {files.slice(Math.max(0, currentIndex - 5), currentIndex + 6).map(f => (
              <Box
                key={f.filePath}
                onClick={() => onNavigate(f)}
                sx={{
                  flexShrink: 0,
                  width: 56, height: 40,
                  borderRadius: 0.5,
                  overflow: 'hidden',
                  cursor: 'pointer',
                  opacity: f.filePath === file.filePath ? 1 : 0.5,
                  outline: f.filePath === file.filePath ? '2px solid #2196f3' : 'none',
                  '&:hover': { opacity: 0.9 },
                  transition: 'opacity 0.15s'
                }}
              >
                {f.type === 'image' ? (
                  <Box component="img"
                    src={mediaApi.getThumbnailUrl(f.filePath)}
                    sx={{ width: '100%', height: '100%', objectFit: 'cover' }}
                  />
                ) : (
                  <Box sx={{ width: '100%', height: '100%', bgcolor: 'grey.800',
                    display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                    <Typography variant="caption" color="grey.400">▶</Typography>
                  </Box>
                )}
              </Box>
            ))}
          </Box>
        </Box>
      </Fade>
    </Backdrop>
  )
}
