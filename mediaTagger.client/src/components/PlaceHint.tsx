import { useEffect, useState } from 'react'
import { Box, Chip, Tooltip, Typography } from '@mui/material'
import PlaceIcon from '@mui/icons-material/Place'
import { placesApi, Coordinates, PlaceCandidate } from '../api/client'

// Lets a pin being dragged or coordinates being typed settle before asking.
const DEBOUNCE_MS = 300

interface Props {
  rootPath: string
  /** Where the photo was taken, as currently shown in the editor. */
  coordinates: Coordinates | null
  /** Only an empty place gets hints; a written one is the user's word. */
  place: string
  onPick: (place: string) => void
}

/** Places already written on photos taken around the same spot, one click to use. */
export function PlaceHint({ rootPath, coordinates, place, onPick }: Props) {
  const [candidates, setCandidates] = useState<PlaceCandidate[]>([])
  const lat = coordinates?.lat
  const lng = coordinates?.lng
  const wanted = !!rootPath && lat != null && lng != null && !place.trim()

  useEffect(() => {
    if (!wanted) { setCandidates([]); return }
    let cancelled = false
    const timer = setTimeout(() => {
      placesApi.nearby(rootPath, lat!, lng!)
        .then(r => { if (!cancelled) setCandidates(r.data) })
        .catch(() => { if (!cancelled) setCandidates([]) })
    }, DEBOUNCE_MS)
    return () => { cancelled = true; clearTimeout(timer) }
  }, [wanted, rootPath, lat, lng])

  if (!wanted || candidates.length === 0) return null

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, flexWrap: 'wrap', mt: -1 }}>
      <Typography variant="caption" color="text.secondary">Поруч:</Typography>
      {candidates.map(c => (
        <Tooltip key={c.place}
          title={`${c.distance < 1000 ? `${Math.round(c.distance)} м` : `${(c.distance / 1000).toFixed(1)} км`} до найближчого фото з цим місцем · фото поруч: ${c.support} · ${Math.round(c.confidence * 100)}%`}>
          <Chip
            size="small"
            icon={<PlaceIcon />}
            label={c.place}
            color={c.confident ? 'success' : 'default'}
            variant="outlined"
            onClick={() => onPick(c.place)}
          />
        </Tooltip>
      ))}
    </Box>
  )
}
