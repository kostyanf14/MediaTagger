import { useEffect, useState } from 'react'
import {
  Dialog, DialogTitle, DialogContent, DialogActions, Button, Typography, Box, Alert, CircularProgress
} from '@mui/material'
import { MediaFile, mediaApi, THUMB_SIZE_SMALL } from '../api/client'
import { Thumbnail } from './Thumbnail'

const PREVIEW_COUNT = 6

interface Props {
  /** What to delete; null keeps the dialog closed. */
  files: MediaFile[] | null
  onCancel: () => void
  /**
   * Paths that are gone from disk, even when a later one failed. `complete` is false
   * then, and the dialog stays open to say which one and how many went.
   */
  onDeleted: (paths: string[], complete: boolean) => void
}

/** Asks before deleting photos and videos from disk, then deletes them one by one. */
export function DeleteFilesDialog({ files, onCancel, onDeleted }: Props) {
  // How many would be gone for good rather than land in the recycle bin; null while asking.
  const [permanent, setPermanent] = useState<number | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!files?.length) return
    setPermanent(null)
    setError('')
    let cancelled = false
    mediaApi.deleteCheck(files.map(f => f.filePath))
      // Unknown is treated as the worse case: better a needless warning than a lost file.
      .then(r => { if (!cancelled) setPermanent(r.data) })
      .catch(() => { if (!cancelled) setPermanent(files.length) })
    return () => { cancelled = true }
  }, [files])

  const confirm = async () => {
    if (!files) return
    setDeleting(true)
    setError('')
    const done: string[] = []
    try {
      for (const file of files) {
        await mediaApi.deleteFile(file.filePath)
        done.push(file.filePath)
      }
    } catch {
      setError(`Не вдалося видалити ${files[done.length].fileName}. Видалено: ${done.length} з ${files.length}.`)
    } finally {
      setDeleting(false)
      if (done.length) onDeleted(done, done.length === files.length)
    }
  }

  const count = files?.length ?? 0
  const failed = !!error

  return (
    <Dialog open={!!files} onClose={() => { if (!deleting) onCancel() }} maxWidth="xs" fullWidth>
      <DialogTitle>{count === 1 ? 'Видалити файл?' : `Видалити файли (${count})?`}</DialogTitle>
      <DialogContent>
        <Box sx={{ display: 'flex', gap: 0.5, mb: 1.5, flexWrap: 'wrap' }}>
          {files?.slice(0, PREVIEW_COUNT).map(f => (
            <Box key={f.filePath} sx={{ width: 56, height: 42, borderRadius: 0.5, overflow: 'hidden' }}>
              <Thumbnail src={mediaApi.getThumbnailUrl(f.filePath, THUMB_SIZE_SMALL)}
                alt={f.fileName} type={f.type} iconSize={16} />
            </Box>
          ))}
          {count > PREVIEW_COUNT && (
            <Box sx={{
              width: 56, height: 42, borderRadius: 0.5, bgcolor: 'action.hover',
              display: 'flex', alignItems: 'center', justifyContent: 'center'
            }}>
              <Typography variant="caption" color="text.secondary">+{count - PREVIEW_COUNT}</Typography>
            </Box>
          )}
        </Box>

        {count === 1 && (
          <Typography variant="body2" sx={{ mb: 1, wordBreak: 'break-all' }}>
            <strong>{files![0].fileName}</strong>
          </Typography>
        )}

        {permanent === null ? (
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <CircularProgress size={14} />
            <Typography variant="body2" color="text.secondary">Перевірка…</Typography>
          </Box>
        ) : permanent === 0 ? (
          <Typography variant="body2">
            {count === 1 ? 'Файл буде переміщено' : 'Файли буде переміщено'} до кошика разом із тегами.
          </Typography>
        ) : (
          <Alert severity="warning">
            {permanent === count
              ? (count === 1 ? 'Файл буде' : 'Файли буде') + ' видалено назавжди, без кошика'
              : `${permanent} з ${count} буде видалено назавжди, без кошика`}
            {' '}(мережевий диск або NAS). Відновити не вийде.
          </Alert>
        )}

        {failed && <Alert severity="error" sx={{ mt: 1.5 }}>{error}</Alert>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={deleting}>{failed ? 'Закрити' : 'Скасувати'}</Button>
        {!failed && (
          <Button onClick={confirm} color="error" variant="contained" disabled={deleting || permanent === null}>
            {deleting ? 'Видалення…' : 'Видалити'}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  )
}
