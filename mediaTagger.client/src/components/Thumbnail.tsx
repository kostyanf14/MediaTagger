import { useEffect, useRef, useState } from 'react'
import { Box, SxProps, Theme } from '@mui/material'
import BrokenImageOutlinedIcon from '@mui/icons-material/BrokenImageOutlined'
import VideocamOutlinedIcon from '@mui/icons-material/VideocamOutlined'

// A preview can fail while the server is converting a HEIC or video under load.
// One quiet retry turns most of those into a picture instead of a broken tile.
const MAX_RETRIES = 1
const RETRY_DELAY_MS = 700

interface Props {
  src: string
  alt?: string
  /** Picks the placeholder icon shown when the image cannot be loaded. */
  type?: 'image' | 'video'
  objectFit?: 'cover' | 'contain'
  /** Size of the fallback icon, in px. */
  iconSize?: number
  /** Grid tiles stay lazy; a viewer's main subject should load straight away. */
  loading?: 'lazy' | 'eager'
  /** Applied to the <img>; the placeholder always fills the parent instead. */
  sx?: SxProps<Theme>
  /** Fired once the picture itself has arrived. */
  onLoad?: () => void
}

/**
 * <img> that degrades to a placeholder instead of the browser's broken-image glyph,
 * and retries once before giving up.
 */
export function Thumbnail({
  src, alt, type = 'image', objectFit = 'cover', iconSize = 28, loading = 'lazy', sx, onLoad
}: Props) {
  const [attempt, setAttempt] = useState(0)
  const [failed, setFailed] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout>>()

  // A new file means a clean slate, otherwise a previous failure sticks to it.
  useEffect(() => {
    setAttempt(0)
    setFailed(false)
    return () => clearTimeout(timer.current)
  }, [src])

  const handleError = () => {
    if (attempt < MAX_RETRIES) {
      timer.current = setTimeout(() => setAttempt(a => a + 1), RETRY_DELAY_MS)
    } else {
      setFailed(true)
    }
  }

  if (failed) {
    const FallbackIcon = type === 'video' ? VideocamOutlinedIcon : BrokenImageOutlinedIcon
    return (
      <Box sx={{
        width: '100%', height: '100%', minWidth: 48, minHeight: 48, bgcolor: 'action.hover',
        display: 'flex', alignItems: 'center', justifyContent: 'center'
      }}>
        <FallbackIcon sx={{ fontSize: iconSize, color: 'text.disabled' }} />
      </Box>
    )
  }

  return (
    <Box
      component="img"
      // Must precede src: it lets the gallery read the decoded pixels back out of a
      // canvas for the drag icon. The API answers with Access-Control-Allow-Origin: *.
      crossOrigin="anonymous"
      // The retry marker also bypasses the browser's cache of the failed response.
      src={attempt > 0 ? `${src}${src.includes('?') ? '&' : '?'}retry=${attempt}` : src}
      alt={alt}
      loading={loading}
      // Decoding off the main thread keeps scrolling smooth while tiles fill in.
      decoding="async"
      onLoad={onLoad}
      onError={handleError}
      sx={[
        {
          width: '100%', height: '100%', objectFit,
          display: 'block', bgcolor: 'action.hover'
        },
        ...(Array.isArray(sx) ? sx : [sx])
      ]}
    />
  )
}
