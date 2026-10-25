import React, { useEffect, useRef, useState } from 'react'
import {
  Box, Typography, Card, Button, Checkbox, TextField,
  Tabs, Tab, Skeleton, Tooltip,
  Dialog, DialogTitle, DialogContent, DialogActions
} from '@mui/material'
import PeopleIcon from '@mui/icons-material/People'
import PhotoAlbumIcon from '@mui/icons-material/PhotoAlbum'
import LocationOnIcon from '@mui/icons-material/LocationOn'
import GroupsIcon from '@mui/icons-material/Groups'
import { mediaApi, facesApi, LibraryStats, SearchRequest, THUMB_SIZE } from '../api/client'
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
  roots: string[]
  /** Bumped whenever tags change anywhere, so the counts here can catch up. */
  refreshKey: number
  /** Only the visible tab refetches; a hidden one waits until it is opened. */
  active: boolean
  onFilter: (req: SearchRequest) => void
  /** Photo tags and confirmed faces were just rewritten to this name. */
  onPeopleRenamed: (from: string[], to: string) => void
}

export function LibraryPanel({ roots, refreshKey, active, onFilter, onPeopleRenamed }: Props) {
  const [stats, setStats] = useState<LibraryStats | null>(null)
  const [loading, setLoading] = useState(false)
  const [tab, setTab] = useState(0)
  const [picked, setPicked] = useState<string[]>([])
  const [naming, setNaming] = useState<string[] | null>(null)
  const [draft, setDraft] = useState('')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState('')
  // What the currently shown numbers were built from.
  const loadedFor = useRef<string | null>(null)

  useEffect(() => {
    if (!roots.length || !active) return

    // Tagging twenty photos and then opening this tab costs one refresh, not twenty.
    const signature = `${roots.join('\n')}|${refreshKey}`
    if (loadedFor.current === signature) return
    loadedFor.current = signature

    setLoading(true)
    mediaApi.getLibraryStats(roots)
      .then(r => setStats(r.data))
      // Let a failed load be retried rather than remembered as done.
      .catch(() => { loadedFor.current = null })
      .finally(() => setLoading(false))
  }, [roots, refreshKey, active])

  useEffect(() => {
    const names = new Set((stats?.people ?? []).map(p => p.name.toLowerCase()))
    setPicked(prev => {
      const next = prev.filter(n => names.has(n.toLowerCase()))
      return next.length === prev.length ? prev : next
    })
  }, [stats])

  if (!roots.length) return (
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

  const togglePicked = (name: string) => {
    const key = name.toLowerCase()
    setPicked(prev => prev.some(p => p.toLowerCase() === key)
      ? prev.filter(p => p.toLowerCase() !== key)
      : [...prev, name])
  }

  const openNaming = (names: string[]) => {
    const ranked = [...names].sort((a, b) =>
      (stats?.people.find(p => p.name === b)?.count ?? 0) - (stats?.people.find(p => p.name === a)?.count ?? 0))
    setDraft(ranked[0] ?? '')
    setSaveError('')
    setNaming(names)
  }

  const saveNaming = async () => {
    if (!naming) return
    const to = draft.trim()
    if (!to || (naming.length === 1 && to === naming[0])) return
    setSaving(true)
    setSaveError('')
    try {
      await facesApi.renamePeople(roots, naming, to)
      setPicked([])
      setNaming(null)
      onPeopleRenamed(naming, to)
    } catch {
      setSaveError('Не вдалося зберегти')
    } finally {
      setSaving(false)
    }
  }

  const handleItemClick = (name: string) => {
    const key = current.filterKey
    const search = { rootPath: roots[0], rootPaths: roots }
    if (key === 'albums') onFilter({ albums: [name], ...search })
    else if (key === 'people') onFilter({ people: [name], ...search })
    else if (key === 'place') onFilter({ place: name, ...search })
    else if (key === 'groups') onFilter({ groups: [name], ...search })
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

      {current.filterKey === 'people' && picked.length > 0 && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 1.5, py: 1, flexShrink: 0 }}>
          <Typography variant="body2">Обрано: {picked.length}</Typography>
          <Button size="small" disabled={picked.length !== 1} onClick={() => openNaming(picked)}>Перейменувати</Button>
          <Button size="small" disabled={picked.length < 2} onClick={() => openNaming(picked)}>Об'єднати</Button>
          <Button size="small" color="inherit" onClick={() => setPicked([])}>Скасувати</Button>
        </Box>
      )}

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
                  {current.filterKey === 'people' && (
                    <Checkbox
                      size="small"
                      checked={picked.some(p => p.toLowerCase() === item.name.toLowerCase())}
                      onMouseDown={e => e.stopPropagation()}
                      onClick={e => e.stopPropagation()}
                      onChange={() => togglePicked(item.name)}
                      inputProps={{ 'aria-label': `Обрати ${item.name}` }}
                      sx={{
                        position: 'absolute', top: 4, left: 4, zIndex: 1, p: 0.25,
                        color: '#fff', bgcolor: 'rgba(0,0,0,0.45)', borderRadius: 1,
                        '&.Mui-checked': { color: 'primary.main' }
                      }}
                    />
                  )}
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

      <Dialog open={naming !== null} onClose={() => { if (!saving) setNaming(null) }} fullWidth maxWidth="xs">
        <DialogTitle>{naming && naming.length > 1 ? 'Об\'єднати людей' : 'Перейменувати'}</DialogTitle>
        <DialogContent>
          <TextField
            autoFocus fullWidth size="small" margin="dense" label="Ім'я"
            value={draft} onChange={e => setDraft(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') saveNaming() }}
            helperText={saveError || (naming && naming.length > 1 ? naming.join(', ') : 'Фото й підтверджені обличчя теж зміняться')}
            error={!!saveError}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setNaming(null)} disabled={saving}>Скасувати</Button>
          <Button onClick={saveNaming} disabled={saving || !draft.trim()
            || (naming?.length === 1 && draft.trim() === naming[0])}>
            Зберегти
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}
