import { useEffect, useState } from 'react'
import { Box, Button, Typography } from '@mui/material'
import { mediaApi, MediaFile, RecapGroup, RecapItem, FEATURES, FEATURE_UNSUPPORTED, THUMB_SIZE } from '../api/client'
import { isFeatureUnsupported } from '../features'
import { useFeature } from '../useFeature'
import { Thumbnail } from './Thumbnail'
import { LightboxViewer } from './LightboxViewer'

interface Props {
  roots: string[]
  active: boolean
  closeViewerOnOutsideClick?: boolean
  copyPhoto?: boolean
}

function asFile(item: RecapItem): MediaFile {
  return {
    fileName: item.fileName, filePath: item.filePath, relativePath: '',
    type: item.type, size: 0, modified: item.taken,
    tags: { people: [], place: '', groups: [], albums: [], notes: '', dateTaken: item.taken }
  }
}

/** Memories from capture dates. Nothing is asked for until the tab is open. */
export function RecapsPanel({ roots, active, closeViewerOnOutsideClick, copyPhoto }: Props) {
  const [groups, setGroups] = useState<RecapGroup[] | null>(null)
  const [unsupported, setUnsupported] = useState(false)
  const [failed, setFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const [open, setOpen] = useState<{ files: MediaFile[]; file: MediaFile } | null>(null)
  const recaps = useFeature(FEATURES.recaps)

  useEffect(() => {
    if (!active || !roots.length || recaps !== true) return
    let cancelled = false
    setUnsupported(false)
    setFailed(false)
    mediaApi.recaps(roots)
      .then(r => { if (!cancelled) setGroups(r.data) })
      .catch(error => {
        if (cancelled) return
        if (isFeatureUnsupported(error)) setUnsupported(true)
        else setFailed(true)
      })
    return () => { cancelled = true }
  }, [active, roots, recaps, attempt])

  if (recaps === false || unsupported) {
    return <Box sx={{ p: 3, textAlign: 'center' }}><Typography color="text.secondary">{FEATURE_UNSUPPORTED}</Typography></Box>
  }

  if (failed) {
    return (
      <Box sx={{ p: 3, textAlign: 'center' }}>
        <Typography color="text.secondary" sx={{ mb: 1 }}>Не вдалося завантажити спогади.</Typography>
        <Button size="small" onClick={() => setAttempt(n => n + 1)}>Спробувати знову</Button>
      </Box>
    )
  }

  if (!roots.length) {
    return <Box sx={{ p: 3, textAlign: 'center' }}><Typography color="text.secondary">Оберіть папку</Typography></Box>
  }

  return (
    <Box sx={{ height: '100%', overflowY: 'auto', px: 2, py: 1.5 }}>
      {groups == null ? (
        <Typography color="text.secondary" variant="body2">Збираю спогади…</Typography>
      ) : groups.length === 0 ? (
        <Typography color="text.secondary" variant="body2" sx={{ textAlign: 'center', mt: 4 }}>
          Немає фото з датою знімання для цих спогадів.
        </Typography>
      ) : groups.map(group => (
        <Box key={group.id} sx={{ mb: 2.5 }}>
          <Typography variant="subtitle2">
            {group.title}
            <Typography component="span" variant="caption" color="text.secondary" sx={{ ml: 1 }}>
              {group.count}
            </Typography>
          </Typography>
          <Box sx={{ display: 'flex', gap: 1, overflowX: 'auto', py: 1 }}>
            {group.items.map(item => (
              <Box key={item.filePath} onClick={() => {
                const files = group.items.map(asFile)
                setOpen({ files, file: asFile(item) })
              }} sx={{ width: 160, flexShrink: 0, cursor: 'pointer' }}>
                <Thumbnail src={mediaApi.getThumbnailUrl(item.filePath, THUMB_SIZE)} alt={item.fileName}
                  type={item.type} sx={{ width: 160, height: 120 }} />
                <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>
                  {new Date(item.taken).toLocaleDateString('uk-UA')}
                </Typography>
              </Box>
            ))}
          </Box>
        </Box>
      ))}
      <LightboxViewer
        file={open?.file ?? null}
        files={open?.files ?? []}
        onClose={() => setOpen(null)}
        onNavigate={file => setOpen(prev => prev ? { ...prev, file } : null)}
        onOpenTags={() => setOpen(null)}
        closeOnOutsideClick={closeViewerOnOutsideClick}
        copyPhoto={copyPhoto}
      />
    </Box>
  )
}
