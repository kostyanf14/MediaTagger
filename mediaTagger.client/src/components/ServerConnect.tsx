import { useState } from 'react'
import { Box, Button, TextField, Typography } from '@mui/material'
import { useElectron } from '../hooks/useElectron'

/** Address of the API this window talks to. Empty means the API on this PC. */
export function ServerConnect() {
  const { apiOrigin, setApiOrigin } = useElectron()
  const [draft, setDraft] = useState(apiOrigin)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  if (!setApiOrigin) {
    return (
      <Typography variant="caption" color="text.secondary" display="block">
        Адресу API у браузері задає змінна MEDIATAGGER_API_URL.
      </Typography>
    )
  }

  const submit = async () => {
    setError('')
    setBusy(true)
    try {
      await setApiOrigin(draft)
      // A new address reloads the window. The same address does not.
      setBusy(false)
    } catch {
      setError('Адреса має бути як https://192.168.1.10:5001')
      setBusy(false)
    }
  }

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
      <TextField
        size="small"
        label="Адреса API"
        placeholder="https://192.168.1.10:5001"
        value={draft}
        onChange={e => { setDraft(e.target.value); setError('') }}
        onKeyDown={e => { if (e.key === 'Enter') void submit() }}
        error={!!error}
        helperText={error || 'Порожнє поле — API на цьому комп’ютері. Після зміни вікно перезавантажиться.'}
      />
      <Button variant="outlined" size="small" onClick={() => void submit()} disabled={busy}>
        {busy ? 'Підключення…' : 'Застосувати адресу'}
      </Button>
    </Box>
  )
}
