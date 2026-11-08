import { useEffect, useState } from 'react'
import {
  Dialog, DialogTitle, DialogContent, DialogActions, Button, TextField, Typography
} from '@mui/material'
import axios from 'axios'
import { mediaApi, RenameResponse } from '../api/client'

export interface RenameTarget {
  path: string
  name: string
  kind: 'file' | 'folder'
}

interface Props {
  target: RenameTarget | null
  onCancel: () => void
  onRenamed: (oldPath: string, result: RenameResponse) => void
}

/** Asks for a new file or folder name, and whether to merge when that folder already exists. */
export function RenameDialog({ target, onCancel, onRenamed }: Props) {
  const [name, setName] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [askMerge, setAskMerge] = useState(false)

  useEffect(() => {
    setName(target?.name ?? '')
    setError('')
    setBusy(false)
    setAskMerge(false)
  }, [target])

  const submit = async (merge: boolean) => {
    if (!target) return
    const next = name.trim()
    if (!next || next === target.name) { onCancel(); return }
    setBusy(true)
    setError('')
    try {
      const res = await mediaApi.rename(target.path, next, merge)
      onRenamed(target.path, res.data)
    } catch (e) {
      const data = axios.isAxiosError(e) ? e.response?.data as { code?: string; name?: string } : null
      if (data?.code === 'merge') { setAskMerge(true); return }
      if (data?.code === 'exists') {
        setAskMerge(false)
        setError(`«${data.name ?? next}» уже існує. Нічого не змінено.`)
        return
      }
      setError(e && axios.isAxiosError(e) && e.response?.status === 400
        ? 'Недопустима назва'
        : 'Не вдалося перейменувати')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={!!target} onClose={busy ? undefined : onCancel} maxWidth="xs" fullWidth>
      <DialogTitle>{target?.kind === 'folder' ? 'Перейменувати папку' : 'Перейменувати файл'}</DialogTitle>
      <DialogContent>
        {askMerge ? (
          <Typography variant="body2" sx={{ mt: 1 }}>
            Папка «{name.trim()}» уже існує. Об'єднати вміст «{target?.name}» з нею?
            Якщо всередині є файли з однаковими назвами, об'єднання не почнеться.
          </Typography>
        ) : (
          <TextField
            autoFocus fullWidth size="small" sx={{ mt: 1 }}
            label="Нова назва" value={name}
            onChange={e => setName(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); void submit(false) } }}
          />
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
          <Button variant="contained" disabled={busy || !name.trim()} onClick={() => void submit(false)}>
            Перейменувати
          </Button>
        )}
      </DialogActions>
    </Dialog>
  )
}
