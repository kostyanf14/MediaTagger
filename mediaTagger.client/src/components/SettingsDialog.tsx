import {
  Dialog, DialogTitle, DialogContent, DialogActions, Button, Typography,
  RadioGroup, FormControlLabel, Radio, Box
} from '@mui/material'
import { AppSettings, VideoFaceMode } from '../api/client'

const VIDEO_MODES: { value: VideoFaceMode; label: string; hint: string }[] = [
  { value: 'Frames', label: 'Усе відео', hint: 'Кадр кожні ~2 с, до 16 на відео. Знаходить і тих, хто з\'являється посередині.' },
  { value: 'Detailed', label: 'Усе відео, детально', hint: 'Кадр щосекунди, до 48 на відео. Більше ракурсів кожної людини, але сканування в кілька разів довше.' },
  { value: 'Cover', label: 'Лише обкладинка', hint: 'Один кадр, той, що видно в галереї. Найшвидше, але багатьох людей пропускає.' },
  { value: 'Off', label: 'Не шукати у відео', hint: 'Відео пропускаються. Те, що в них уже знайдено, лишається.' },
]

interface Props {
  open: boolean
  settings: AppSettings
  onChange: (settings: AppSettings) => void
  onClose: () => void
}

/** App-wide preferences. Each change is saved as soon as it is made. */
export function SettingsDialog({ open, settings, onChange, onClose }: Props) {
  const videoFaces = settings.videoFaces ?? 'Frames'

  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>Налаштування</DialogTitle>
      <DialogContent>
        <Typography variant="subtitle2" gutterBottom>Обличчя у відео</Typography>
        <RadioGroup value={videoFaces}
          onChange={e => onChange({ ...settings, videoFaces: e.target.value as VideoFaceMode })}>
          {VIDEO_MODES.map(mode => (
            <FormControlLabel key={mode.value} value={mode.value} control={<Radio size="small" />}
              sx={{ alignItems: 'flex-start', mb: 1, '& .MuiRadio-root': { pt: 0.25 } }}
              label={
                <Box>
                  <Typography variant="body2">{mode.label}</Typography>
                  <Typography variant="caption" color="text.secondary" display="block" lineHeight={1.4}>
                    {mode.hint}
                  </Typography>
                </Box>
              } />
          ))}
        </RadioGroup>
        <Typography variant="caption" color="text.secondary">
          Після зміни пошук облич перезапускається і доробляє відео, оброблені простіше, ніж обрано.
          Підтверджені обличчя зберігаються.
        </Typography>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Закрити</Button>
      </DialogActions>
    </Dialog>
  )
}
