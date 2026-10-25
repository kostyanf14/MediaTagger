import React, { useCallback, useEffect, useRef, useState } from 'react'
import { Box, Typography, Button, IconButton, Tooltip, Chip, Divider, Skeleton } from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import DoneAllIcon from '@mui/icons-material/DoneAll'
import RefreshIcon from '@mui/icons-material/Refresh'
import AddLocationAltIcon from '@mui/icons-material/AddLocationAlt'
import {
  placesApi, mediaApi, PlaceSuggestionsResponse, PlaceSuggestion, BulkTagsRequest, THUMB_SIZE_SMALL
} from '../api/client'
import { Thumbnail } from './Thumbnail'

const TILE_W = 120
const TILE_H = 90
// A neighbourhood can collect thousands of photos; drawing them all at once helps nobody.
const PAGE_SIZE = 60

interface Props {
  roots: string[]
  /** Bumped whenever tags change anywhere — a new place is new evidence. */
  refreshKey: number
  /** Only the visible tab refetches. */
  active: boolean
  /** Fired after places were applied, so the grid and the other views catch up. */
  onTagsAdded: (applied: BulkTagsRequest) => void
}

function formatDistance(meters: number): string {
  return meters < 1000 ? `${Math.round(meters)} м` : `${(meters / 1000).toFixed(1)} км`
}

export function PlacesPanel({ roots, refreshKey, active, onTagsAdded }: Props) {
  const [data, setData] = useState<PlaceSuggestionsResponse | null>(null)
  const [loading, setLoading] = useState(false)
  // Files answered this session — applied, or hidden with ✗. Hidden ones come back
  // after a restart: nothing is stored for "not here", the right place simply wins once set.
  const [decided, setDecided] = useState<Set<string>>(new Set())
  const [shown, setShown] = useState<Record<string, number>>({})
  const loadedFor = useRef<string | null>(null)

  const load = useCallback((quiet = false) => {
    if (!roots.length) return
    if (!quiet) setLoading(true)
    placesApi.suggestions(roots)
      .then(r => setData(r.data))
      .catch(() => { loadedFor.current = null })
      .finally(() => setLoading(false))
  }, [roots])

  useEffect(() => {
    if (!roots.length || !active) return
    const signature = `${roots.join('\n')}|${refreshKey}`
    if (loadedFor.current === signature) return
    const quiet = loadedFor.current?.startsWith(`${roots.join('\n')}|`) ?? false
    loadedFor.current = signature
    load(quiet)
  }, [roots, refreshKey, active, load])

  useEffect(() => { setData(null); setDecided(new Set()); setShown({}) }, [roots])

  const hide = (items: PlaceSuggestion[]) =>
    setDecided(prev => { const next = new Set(prev); items.forEach(i => next.add(i.filePath)); return next })

  const apply = async (place: string, items: PlaceSuggestion[]) => {
    if (!items.length) return
    hide(items)
    const request: BulkTagsRequest = {
      filePaths: items.map(i => i.filePath), people: [], groups: [], albums: [], place
    }
    try {
      await mediaApi.bulkAddTags(request)
      onTagsAdded(request)
    } catch {
      setDecided(prev => { const next = new Set(prev); items.forEach(i => next.delete(i.filePath)); return next })
    }
  }

  if (!roots.length) return (
    <Box sx={{ p: 3, textAlign: 'center' }}>
      <Typography color="text.secondary">Оберіть папку</Typography>
    </Box>
  )

  const groups = (data?.places ?? [])
    .map(g => ({ ...g, items: g.items.filter(i => !decided.has(i.filePath)) }))
    .filter(g => g.items.length > 0)
  const suggested = groups.reduce((n, g) => n + g.items.length, 0)

  return (
    <Box sx={{ height: '100%', overflowY: 'auto' }}>
      <Box sx={{
        px: 2, py: 1.5, borderBottom: '1px solid', borderColor: 'divider',
        display: 'flex', alignItems: 'center', gap: 1
      }}>
        <AddLocationAltIcon fontSize="small" sx={{ color: 'text.secondary' }} />
        <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
          {data
            ? `Фото з GPS без місця: ${data.unplaced.toLocaleString('uk-UA')} · є підказка: ${suggested.toLocaleString('uk-UA')} · відомих місць: ${data.knownPlaces}`
            : 'Місця за координатами'}
        </Typography>
        <Tooltip title="Оновити підказки">
          <IconButton size="small" onClick={() => load()}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Box>

      <Box sx={{ p: 2, display: 'flex', flexDirection: 'column', gap: 2.5 }}>
        {loading && !data ? (
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            {Array.from({ length: 12 }).map((_, i) => (
              <Skeleton key={i} variant="rounded" width={TILE_W} height={TILE_H} />
            ))}
          </Box>
        ) : data && data.knownPlaces === 0 ? (
          <Typography color="text.secondary" variant="body2" sx={{ textAlign: 'center', mt: 4, px: 4 }}>
            Вкажіть «Місце» хоча б на кількох фото з GPS — тоді фото, зняті поруч,
            отримають підказку того ж місця.
          </Typography>
        ) : data && groups.length === 0 ? (
          <Typography color="text.secondary" variant="body2" sx={{ textAlign: 'center', mt: 4 }}>
            Немає фото без місця поблизу відомих місць
          </Typography>
        ) : groups.map(group => {
          const confident = group.items.filter(i => i.confident)
          const limit = shown[group.place] ?? PAGE_SIZE
          return (
            <Box key={group.place}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1, flexWrap: 'wrap' }}>
                <Typography variant="subtitle2" fontWeight={600}>{group.place}</Typography>
                <Typography variant="caption" color="text.secondary">
                  {group.items.length} фото поруч
                </Typography>
                <Box sx={{ flex: 1 }} />
                <Tooltip title="Лише фото, зняті не далі ~100 м від уже позначених цим місцем, де інших кандидатів майже немає (зелена рамка)">
                  <span>
                    <Button size="small" variant="outlined" startIcon={<DoneAllIcon />}
                      disabled={confident.length === 0}
                      onClick={() => apply(group.place, confident)}>
                      Застосувати до впевнених ({confident.length})
                    </Button>
                  </span>
                </Tooltip>
              </Box>

              <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
                {group.items.slice(0, limit).map(item => (
                  <Box key={item.filePath} sx={{
                    position: 'relative', width: TILE_W,
                    '&:hover .place-actions': { opacity: 1 }
                  }}>
                    <Tooltip
                      placement="top"
                      enterDelay={400}
                      title={
                        <Box>
                          <Typography variant="caption" sx={{ display: 'block' }}>
                            {item.fileName} · {formatDistance(item.distance)} від фото «{item.place}» ·
                            {' '}{Math.round(item.confidence * 100)}%
                          </Typography>
                          {item.alternatives.length > 0 && (
                            <Box sx={{ mt: 0.5, display: 'flex', gap: 0.5, flexWrap: 'wrap', alignItems: 'center' }}>
                              <Typography variant="caption">Або:</Typography>
                              {item.alternatives.map(alt => (
                                <Chip key={alt} label={alt} size="small" clickable
                                  onClick={() => apply(alt, [item])} />
                              ))}
                            </Box>
                          )}
                        </Box>
                      }
                    >
                      <Box sx={{
                        width: TILE_W, height: TILE_H, borderRadius: 1, overflow: 'hidden',
                        border: '2px solid', borderColor: item.confident ? 'success.main' : 'divider'
                      }}>
                        <Thumbnail src={mediaApi.getThumbnailUrl(item.filePath, THUMB_SIZE_SMALL)}
                          alt={item.fileName} type={item.type} iconSize={20} />
                      </Box>
                    </Tooltip>
                    <Typography variant="caption" sx={{
                      position: 'absolute', left: 4, bottom: 4, px: 0.5, borderRadius: 0.5,
                      bgcolor: 'rgba(0,0,0,0.6)', fontSize: '0.65rem'
                    }}>
                      {formatDistance(item.distance)}
                    </Typography>
                    <Box className="place-actions" sx={{
                      position: 'absolute', top: 4, right: 4, display: 'flex', gap: 0.5,
                      opacity: 0, transition: 'opacity 0.15s'
                    }}>
                      <Tooltip title={`Так, це «${item.place}»`}>
                        <IconButton size="small" onClick={() => apply(item.place, [item])}
                          sx={{ bgcolor: 'success.dark', p: 0.25, '&:hover': { bgcolor: 'success.main' } }}>
                          <CheckIcon sx={{ fontSize: 16 }} />
                        </IconButton>
                      </Tooltip>
                      <Tooltip title="Сховати">
                        <IconButton size="small" onClick={() => hide([item])}
                          sx={{ bgcolor: 'error.dark', p: 0.25, '&:hover': { bgcolor: 'error.main' } }}>
                          <CloseIcon sx={{ fontSize: 16 }} />
                        </IconButton>
                      </Tooltip>
                    </Box>
                  </Box>
                ))}
              </Box>
              {group.items.length > limit && (
                <Button size="small" sx={{ mt: 1 }}
                  onClick={() => setShown(s => ({ ...s, [group.place]: limit + PAGE_SIZE * 4 }))}>
                  Показати ще ({group.items.length - limit})
                </Button>
              )}
              <Divider sx={{ mt: 2 }} />
            </Box>
          )
        })}
      </Box>
    </Box>
  )
}
