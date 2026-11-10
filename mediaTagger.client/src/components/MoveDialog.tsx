import { useEffect, useState } from 'react'
import {
  Dialog, DialogTitle, DialogContent, DialogActions, Button, TextField, Typography, Box
} from '@mui/material'
import axios from 'axios'
import { mediaApi, RenameResponse } from '../api/client'
import { useElectron } from '../hooks/useElectron'

export interface MoveTarget {
  path: string
  name: string
  kind: 'file' | 'folder'
}

interface Props {
  target: MoveTarget | null
  onCancel: () => void
  onMoved: (oldPath: string, result: RenameResponse) => void
}

/** Picks a folder and moves the file or folder there. A folder name that already exists can be merged. */
export function MoveDialog({ target, onCancel, onMoved }: Props) {
  const { selectFolder, isElectron } = useElectron()
  const [destination, setDestination] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [askMerge, setAskMerge] = useState(false)

  useEffect(() => {
    setDestination('')
    setError('')
    setBusy(false)
    setAskMerge(false)
  }, [target])

  const browse = async () => {
    const path = await selectFolder()
    if (path) setDestination(path)
  }

  const submit = async (merge: boolean) => {
    if (!target) return
    const dest = destination.trim()
    if (!dest) return
    setBusy(true)
    setError('')
    try {
      const res = await mediaApi.move(target.path, dest, merge)
      onMoved(target.path, res.data)
    } catch (e) {
      const data = axios.isAxiosError(e) ? e.response?.data as { code?: string; name?: string } : null
      if (data?.code === 'merge') { setAskMerge(true); return }
      if (data?.code === 'exists') {
        setAskMerge(false)
        setError(`«${data.name ?? target.name}» уже є в цій папці. Нічого не переміщено.`)
        return
      }
      setError(axios.isAxiosError(e) && e.response?.status === 404
        ? 'Папку призначення не знайдено'
        : 'Не вдалося перемістити')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={!!target} onClose={busy ? undefined : onCancel} maxWidth="xs" fullWidth>
      <DialogTitle>Перемістити «{target?.name}»</DialogTitle>
      <DialogContent>
        {askMerge ? (
          <Typography variant="body2" sx={{ mt: 1 }}>
            У вибраній папці вже є «{target?.name}». Об'єднати вміст?
            Якщо всередині є файли з однаковими назвами, об'єднання не почнеться.
          </Typography>
        ) : (
          <Box sx={{ display: 'flex', gap: 1, mt: 1 }}>
            <TextField
              autoFocus fullWidth size="small" label="Папка призначення"
              value={destination} onChange={e => setDestination(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); void submit(false) } }}
            />
            {isElectron && (
              <Button onClick={() => void browse()} disabled={busy} sx={{ flexShrink: 0 }}>Обрати</Button>
            )}
          </Box>
        )}
        {error && (
          <Typography variant="body2" color="error" sx={{ mt: 1 }}>{error}</Typography>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel} disabled={busy}>Скасувати</Button>
        {askMerge ? (
          <Button variant="contained" disabled={busy} onClick={() => void submit(true)}>Об'єднати</Button>
        ) : (
          <Button variant="contained" disabled={busy || !destination.trim()} onClick={() => void submit(false)}>
            Перемістити
          </Button>
        )}
      </DialogActions>
    </Dialog>
  )
}
