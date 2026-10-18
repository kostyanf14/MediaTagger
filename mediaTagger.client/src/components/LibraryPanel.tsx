import React, { useEffect, useRef, useState } from 'react'
import {
  Box, Typography, Card,
  Tabs, Tab, Skeleton, Chip, Tooltip
} from '@mui/material'
import PeopleIcon from '@mui/icons-material/People'
import PhotoAlbumIcon from '@mui/icons-material/PhotoAlbum'
import LocationOnIcon from '@mui/icons-material/LocationOn'
import GroupsIcon from '@mui/icons-material/Groups'
import { mediaApi, LibraryStats, SearchRequest, THUMB_SIZE } from '../api/client'
import { Thumbnail } from './Thumbnail'

// Fixed-size squares: covers keep their shape whatever the panel width, and a wide
// window shows more of them instead of two stretched columns.
const TILE_SIZE = 150
const tileGrid = {
  display: 'grid',
  gridTemplateColumns: `repeat(auto-fill, ${TILE_SIZE}px)`,
  gap: 1.5
} as const

interface Props {
  rootPath: string
  /** Bumped whenever tags change anywhere, so the counts here can catch up. */
  refreshKey: number
  /** Only the visible tab refetches; a hidden one waits until it is opened. */
  active: boolean
  onFilter: (req: SearchRequest) => void
}

export function LibraryPanel({ rootPath, refreshKey, active, onFilter }: Props) {
  const [stats, setStats] = useState<LibraryStats | null>(null)
  const [loading, setLoading] = useState(false)
  const [tab, setTab] = useState(0)
  // What the currently shown numbers were built from.
  const loadedFor = useRef<string | null>(null)

  useEffect(() => {
    if (!rootPath || !active) return

    // Tagging twenty photos and then opening this tab costs one refresh, not twenty.
    const signature = `${rootPath}|${refreshKey}`
    if (loadedFor.current === signature) return
    loadedFor.current = signature

    setLoading(true)
    mediaApi.getLibraryStats(rootPath)
      .then(r => setStats(r.data))
      // Let a failed load be retried rather than remembered as done.
      .catch(() => { loadedFor.current = null })
      .finally(() => setLoading(false))
  }, [rootPath, refreshKey, active])

  if (!rootPath) return (
    <Box sx={{ p: 3, textAlign: 'center' }}>
      <Typography color="text.secondary">Оберіть папку</Typography>
    </Box>
  )

  const tabs = [
    { label: 'Альбоми', icon: <PhotoAlbumIcon fontSize="small" />, items: stats?.albums ?? [], filterKey: 'albums' as const },
    { label: 'Люди',    icon: <PeopleIcon fontSize="small" />,     items: stats?.people ?? [], filterKey: 'people' as const },
    { label: 'Місця',   icon: <LocationOnIcon fontSize="small" />, items: stats?.places ?? [], filterKey: 'place' as const },
    { label: 'Групи',   icon: <GroupsIcon fontSize="small" />,     items: stats?.groups ?? [], filterKey: 'groups' as const },
  ]

  const current = tabs[tab]

  const handleItemClick = (name: string) => {
    const key = current.filterKey
    if (key === 'albums') onFilter({ albums: [name], rootPath })
    else if (key === 'people') onFilter({ people: [name], rootPath })
    else if (key === 'place') onFilter({ place: name, rootPath })
    else if (key === 'groups') onFilter({ groups: [name], rootPath })
  }

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <Tabs
        value={tab}
        onChange={(_, v) => setTab(v)}
        variant="fullWidth"
        sx={{ borderBottom: '1px solid', borderColor: 'divider', minHeight: 40 }}
      >
        {tabs.map((t, i) => (
          <Tab key={i} icon={t.icon} iconPosition="start" label={t.label}
            sx={{ minHeight: 40, fontSize: '0.75rem', py: 0 }} />
        ))}
      </Tabs>

      <Box sx={{ flex: 1, overflowY: 'auto', p: 1.5 }}>
        {loading ? (
          <Box sx={tileGrid}>
            {Array.from({ length: 12 }).map((_, i) => (
              <Skeleton key={i} variant="rounded" width={TILE_SIZE} height={TILE_SIZE} />
            ))}
          </Box>
        ) : current.items.length === 0 ? (
          <Typography color="text.secondary" variant="body2" sx={{ textAlign: 'center', mt: 4 }}>
            Немає даних
          </Typography>
        ) : (
          <Box sx={tileGrid}>
            {current.items.map((item) => (
              <Tooltip key={item.name} title={`Показати всі (${item.count})`}>
                <Card
                  onClick={() => handleItemClick(item.name)}
                  sx={{
                    position: 'relative',
                    width: TILE_SIZE, height: TILE_SIZE,
                    cursor: 'pointer',
                    bgcolor: 'background.default',
                    border: '1px solid', borderColor: 'divider',
                    '&:hover': { borderColor: 'primary.main', transform: 'scale(1.02)' },
                    transition: 'all 0.15s'
                  }}
                >
                  {item.coverPath ? (
                    <Thumbnail
                      src={mediaApi.getThumbnailUrl(item.coverPath, THUMB_SIZE)}
                      alt={item.name}
                      iconSize={24}
                    />
                  ) : (
                    <Box sx={{ height: '100%', bgcolor: 'action.hover', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                      {current.icon}
                    </Box>
                  )}
                  {/* Caption over the picture, so the tile stays square */}
                  <Box sx={{
                    position: 'absolute', left: 0, right: 0, bottom: 0,
                    px: 1, pt: 2, pb: 0.75,
                    background: 'linear-gradient(transparent, rgba(0,0,0,0.85))'
                  }}>
                    <Typography variant="caption" fontWeight={600} noWrap display="block" sx={{ color: '#fff' }}>
                      {item.name}
                    </Typography>
                    <Typography variant="caption" sx={{ color: 'rgba(255,255,255,0.7)' }}>
                      {item.count} фото
                    </Typography>
                  </Box>
                </Card>
              </Tooltip>
            ))}
          </Box>
        )}
      </Box>
    </Box>
  )
}
