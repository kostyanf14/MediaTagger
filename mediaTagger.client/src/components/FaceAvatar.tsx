import { Avatar, SxProps, Theme } from '@mui/material'
import FaceIcon from '@mui/icons-material/Face'
import { facesApi } from '../api/client'

interface Props {
  filePath: string
  /** Face box relative (0..1) to the upright picture. */
  box: { x: number; y: number; w: number; h: number }
  size?: number
  variant?: 'circular' | 'rounded' | 'square'
  sx?: SxProps<Theme>
}

/** One face cut out of a photo by the server, with a plain face icon while it loads or if it cannot. */
export function FaceAvatar({ filePath, box, size = 40, variant = 'circular', sx }: Props) {
  // Asking for twice the display size keeps it sharp on high-DPI screens.
  const src = facesApi.getCropUrl(filePath, box, size * 2)
  return (
    <Avatar
      src={src}
      variant={variant}
      imgProps={{ loading: 'lazy', crossOrigin: 'anonymous' }}
      sx={[{ width: size, height: size, bgcolor: 'action.hover' }, ...(Array.isArray(sx) ? sx : [sx])]}
    >
      <FaceIcon sx={{ fontSize: size * 0.6, color: 'text.disabled' }} />
    </Avatar>
  )
}
