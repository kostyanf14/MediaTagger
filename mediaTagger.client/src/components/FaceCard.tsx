import { ReactNode } from 'react'
import { Box, IconButton, Tooltip, Typography } from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import { FaceSuggestion, MediaFile, mediaApi, THUMB_SIZE } from '../api/client'
import { FaceAvatar } from './FaceAvatar'

export const FACE_CARD_SIZE = 104
export const CONFIDENT_SCORE = 0.55

export function faceFile(item: FaceSuggestion): MediaFile {
  return {
    fileName: item.fileName, filePath: item.filePath, relativePath: '',
    type: item.type, size: 0, modified: '',
    tags: { people: [], place: '', groups: [], albums: [], notes: '' }
  }
}

function scoreColor(score: number): string {
  if (score >= CONFIDENT_SCORE) return 'success.main'
  if (score >= 0.5) return 'warning.main'
  return 'divider'
}

/** The whole photo with the face marked, shown when the cursor rests on a card. */
function FaceContext({ item }: { item: FaceSuggestion }) {
  return (
    <Box sx={{ position: 'relative', width: 260 }}>
      <Box component="img" src={mediaApi.getThumbnailUrl(item.filePath, THUMB_SIZE, item.time)} alt={item.fileName}
        crossOrigin="anonymous" sx={{ width: '100%', display: 'block', borderRadius: 0.5 }} />
      <Box sx={{
        position: 'absolute',
        left: `${item.x * 100}%`, top: `${item.y * 100}%`,
        width: `${item.w * 100}%`, height: `${item.h * 100}%`,
        border: '2px solid', borderColor: 'warning.main', borderRadius: 0.5
      }} />
      <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
        {item.fileName} · схожість {Math.round(item.score * 100)}%
      </Typography>
    </Box>
  )
}

/** Crop, hover preview, and double-click to open the photo. Extra controls sit on top. */
export function FaceHover({ item, size = FACE_CARD_SIZE, onOpen, children }: {
  item: FaceSuggestion
  size?: number
  onOpen: (item: FaceSuggestion) => void
  children?: ReactNode
}) {
  return (
    <Box onDoubleClick={() => onOpen(item)} sx={{
      position: 'relative', width: size, flexShrink: 0, cursor: 'pointer',
      '&:hover .face-actions': { opacity: 1 }
    }}>
      <Tooltip title={<FaceContext item={item} />} placement="top" enterDelay={500}
        componentsProps={{ tooltip: { sx: { maxWidth: 'none', p: 1 } } }}>
        <Box>
          <FaceAvatar filePath={item.filePath} box={item} size={size} variant="rounded"
            sx={{ border: '2px solid', borderColor: scoreColor(item.score) }} />
        </Box>
      </Tooltip>
      {children}
    </Box>
  )
}

export function FaceCard({ item, person, onAccept, onReject, onOpen }: {
  item: FaceSuggestion
  person: string
  onAccept: (person: string, items: FaceSuggestion[]) => void
  onReject: (person: string, items: FaceSuggestion[]) => void
  onOpen: (item: FaceSuggestion) => void
}) {
  return (
    <Box sx={{ width: FACE_CARD_SIZE, flexShrink: 0 }}>
      <FaceHover item={item} onOpen={onOpen}>
        <Box className="face-actions" onDoubleClick={e => e.stopPropagation()} sx={{
          position: 'absolute', top: 4, right: 4, display: 'flex', gap: 0.5,
          opacity: 0, transition: 'opacity 0.15s'
        }}>
          <Tooltip title={`Так, це ${person}`}>
            <IconButton size="small" onClick={() => onAccept(person, [item])}
              sx={{ bgcolor: 'success.dark', p: 0.25, '&:hover': { bgcolor: 'success.main' } }}>
              <CheckIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
          <Tooltip title="Ні">
            <IconButton size="small" onClick={() => onReject(person, [item])}
              sx={{ bgcolor: 'error.dark', p: 0.25, '&:hover': { bgcolor: 'error.main' } }}>
              <CloseIcon sx={{ fontSize: 16 }} />
            </IconButton>
          </Tooltip>
        </Box>
      </FaceHover>
      <Typography variant="caption" color="text.secondary" noWrap
        sx={{ display: 'block', fontSize: '0.65rem' }} title={item.fileName}>
        {item.fileName}
      </Typography>
    </Box>
  )
}
