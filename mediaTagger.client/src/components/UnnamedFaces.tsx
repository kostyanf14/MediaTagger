import { useCallback, useEffect, useState } from 'react'
import { Box, Button, TextField, Typography } from '@mui/material'
import { facesApi, FaceSuggestion } from '../api/client'
import { FaceAvatar } from './FaceAvatar'

interface Props {
  roots: string[]
  /** Names already learned, offered as the field’s suggestions. */
  names: string[]
  onNamed: (filePath: string, person: string) => void
}

/** One unnamed photo face at a time. Mounted only while the queue is open, so nothing is fetched before that. */
export function UnnamedFaces({ roots, names, onNamed }: Props) {
  const [queue, setQueue] = useState<FaceSuggestion[] | null>(null)
  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const current = queue?.[0]

  const load = useCallback(() => {
    facesApi.unnamed(roots)
      .then(r => setQueue(r.data))
      .catch(() => setQueue([]))
  }, [roots])

  useEffect(() => { load() }, [load])

  const advance = (rest: FaceSuggestion[]) => {
    setDraft('')
    if (rest.length > 0) setQueue(rest)
    else load()
  }

  const addName = async () => {
    if (!current || !draft.trim() || busy) return
    const person = draft.trim()
    setBusy(true)
    try {
      await facesApi.accept([{ faceId: current.faceId, filePath: current.filePath, person }])
      onNamed(current.filePath, person)
      advance(queue!.slice(1))
    } catch { /* leave the face so the name can be tried again */ }
    finally { setBusy(false) }
  }

  const skip = async () => {
    if (!current || busy) return
    setBusy(true)
    try {
      await facesApi.skipUnnamed([current.faceId])
      advance(queue!.slice(1))
    } catch { /* keep it */ }
    finally { setBusy(false) }
  }

  if (!current) {
    return (
      <Typography variant="body2" color="text.secondary" sx={{ px: 2, py: 1.5 }}>
        {queue == null ? 'Шукаю безіменні фото…' : 'Безіменних облич на фото більше немає.'}
      </Typography>
    )
  }

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, px: 2, py: 1.5,
      borderBottom: '1px solid', borderColor: 'divider' }}>
      <FaceAvatar filePath={current.filePath} box={current} size={96} variant="rounded" />
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>
          Хто це? Лише фото. {current.fileName}
        </Typography>
        <TextField size="small" fullWidth autoFocus placeholder="Ім’я" value={draft}
          onChange={e => setDraft(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void addName() }}
          inputProps={{ list: 'unnamed-face-names' }}
          sx={{ mt: 0.5 }} />
        <datalist id="unnamed-face-names">
          {names.map(name => <option key={name} value={name} />)}
        </datalist>
        <Box sx={{ display: 'flex', gap: 1, mt: 0.75 }}>
          <Button size="small" variant="contained" disabled={busy || !draft.trim()} onClick={() => void addName()}>
            Додати ім’я
          </Button>
          <Button size="small" disabled={busy} onClick={() => void skip()}>Пропустити</Button>
        </Box>
      </Box>
    </Box>
  )
}
