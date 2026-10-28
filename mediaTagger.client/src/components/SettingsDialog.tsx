import { useEffect, useState } from 'react'
import {
  Dialog, DialogTitle, DialogContent, DialogActions, Button, Typography,
  RadioGroup, FormControlLabel, Radio, Box, Slider, Switch, Divider, CircularProgress,
  Autocomplete, TextField
} from '@mui/material'
import { AppSettings, VideoFaceMode, FaceDeviceInfo, facesApi } from '../api/client'

const VIDEO_MODES: { value: VideoFaceMode; label: string; hint: string }[] = [
  { value: 'Frames', label: 'Усе відео', hint: 'Кадр кожні ~2 с, до 16 на відео. Знаходить і тих, хто з\'являється посередині.' },
  { value: 'Detailed', label: 'Усе відео, детально', hint: 'Кадр щосекунди, до 48 на відео. Більше ракурсів кожної людини, але сканування в кілька разів довше.' },
  { value: 'Cover', label: 'Лише обкладинка', hint: 'Один кадр, той, що видно в галереї. Найшвидше, але багатьох людей пропускає.' },
  { value: 'Off', label: 'Не шукати у відео', hint: 'Відео пропускаються. Те, що в них уже знайдено, лишається.' },
]

interface Props {
  open: boolean
  settings: AppSettings
  /** Known people, offered for the default photographer. */
  people: string[]
  onChange: (settings: AppSettings) => Promise<unknown> | void
  onClose: () => void
}

/** App-wide preferences. Each change is saved as soon as it is made. */
export function SettingsDialog({ open, settings, people, onChange, onClose }: Props) {
  const videoFaces = settings.videoFaces ?? 'Frames'
  const [device, setDevice] = useState<FaceDeviceInfo | null>(null)
  const [checkingDevice, setCheckingDevice] = useState(false)
  // The slider moves freely; only where it is let go is saved, since each save restarts the scan.
  const [threadsDraft, setThreadsDraft] = useState(settings.faceThreads ?? 0)

  const loadDevice = () => {
    setCheckingDevice(true)
    facesApi.device()
      .then(res => setDevice(res.data))
      .catch(() => setDevice(null))
      .finally(() => setCheckingDevice(false))
  }

  useEffect(() => {
    if (!open) return
    setThreadsDraft(settings.faceThreads ?? 0)
    loadDevice()
  }, [open])

  const save = async (patch: Partial<AppSettings>, recheckDevice = false) => {
    await onChange({ ...settings, ...patch })
    // Whether the card took the models is only known once the server tries.
    if (recheckDevice) loadDevice()
  }

  const maxThreads = device?.processorCount ?? 16
  const threadsLabel = threadsDraft === 0
    ? `Авто${device ? ` (${device.autoThreads})` : ''}`
    : `${threadsDraft}`

  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>Налаштування</DialogTitle>
      <DialogContent>
        <Typography variant="subtitle2" gutterBottom>Фотограф за замовчуванням</Typography>
        <Autocomplete
          freeSolo
          autoSelect
          selectOnFocus
          handleHomeEndKeys
          options={people}
          value={settings.defaultPhotographer || null}
          onChange={(_, value) => save({ defaultPhotographer: typeof value === 'string' ? value.trim() : '' })}
          renderInput={(params) => (
            <TextField {...params} size="small" placeholder="Нікого" sx={{ mt: 1 }} />
          )}
        />
        <Typography variant="caption" color="text.secondary" display="block" lineHeight={1.4} sx={{ mt: 0.5 }}>
          Підставляється на фото, де фотографа ще не вказували, і зберігається разом
          з іншими тегами цього фото. Якщо очистити поле на фото, воно лишиться порожнім.
        </Typography>

        <Divider sx={{ my: 2 }} />

        <Typography variant="subtitle2">Перегляд</Typography>
        <FormControlLabel sx={{ mt: 1, alignItems: 'flex-start', '& .MuiSwitch-root': { mt: -0.5 } }}
          control={
            <Switch size="small" checked={!!settings.closeViewerOnOutsideClick}
              onChange={e => save({ closeViewerOnOutsideClick: e.target.checked })} />
          }
          label={
            <Box>
              <Typography variant="body2">Закривати клацанням поза фото</Typography>
              <Typography variant="caption" color="text.secondary" display="block" lineHeight={1.4}>
                Клацання на темне тло навколо фото чи відео закриває перегляд. Клацання по самому фото,
                кнопках і стрічці мініатюр, як і перетягування збільшеного фото, його не закривають.
              </Typography>
            </Box>
          } />

        <Divider sx={{ my: 2 }} />

        <Typography variant="subtitle2" gutterBottom>Обличчя у відео</Typography>
        <RadioGroup value={videoFaces}
          onChange={e => save({ videoFaces: e.target.value as VideoFaceMode })}>
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

        <Divider sx={{ my: 2 }} />

        <Typography variant="subtitle2">Швидкість пошуку облич</Typography>
        <Box sx={{ display: 'flex', alignItems: 'baseline', mt: 1 }}>
          <Typography variant="body2" sx={{ flex: 1 }}>Потоки процесора</Typography>
          <Typography variant="body2" fontWeight={600}>
            {threadsLabel}{device ? ` з ${device.processorCount}` : ''}
          </Typography>
        </Box>
        <Slider size="small" min={0} max={maxThreads} step={1} value={Math.min(threadsDraft, maxThreads)}
          onChange={(_, v) => setThreadsDraft(v as number)}
          onChangeCommitted={(_, v) => save({ faceThreads: v as number })}
          marks={[{ value: 0, label: 'Авто' }, { value: maxThreads, label: `${maxThreads}` }]}
          sx={{ mx: 1, width: 'calc(100% - 16px)' }} />
        <Typography variant="caption" color="text.secondary" display="block" lineHeight={1.4}>
          Скільки файлів обробляється одночасно. Більше — швидше, але комп'ютер під час сканування
          працюватиме повільніше. «Авто» лишає більшу частину процесора вільною.
        </Typography>

        <FormControlLabel sx={{ mt: 1.5, alignItems: 'flex-start', '& .MuiSwitch-root': { mt: -0.5 } }}
          control={
            <Switch size="small" checked={!!settings.faceGpu}
              disabled={device ? !device.gpuSupported : false}
              onChange={e => save({ faceGpu: e.target.checked }, true)} />
          }
          label={
            <Box>
              <Typography variant="body2">Використовувати відеокарту</Typography>
              <Typography variant="caption" color="text.secondary" display="block" lineHeight={1.4}>
                Пошук облич на кадрі робить відеокарта (DirectML), це пришвидшує сканування на 20–30%.
                Розпізнавання, хто це, лишається на процесорі, щоб результати збігалися з уже збереженими.
              </Typography>
            </Box>
          } />
        <Box sx={{ mt: 0.5, ml: 5.5, display: 'flex', alignItems: 'center', gap: 1, minHeight: 20 }}>
          {checkingDevice ? (
            <>
              <CircularProgress size={12} />
              <Typography variant="caption" color="text.secondary">Перевірка…</Typography>
            </>
          ) : device && !device.gpuSupported ? (
            <Typography variant="caption" color="text.secondary">Доступно лише для Windows.</Typography>
          ) : device && settings.faceGpu ? (
            device.usingGpu
              ? <Typography variant="caption" color="success.main">Працює на відеокарті.</Typography>
              : <Typography variant="caption" color="warning.main" title={device.gpuError ?? undefined}>
                  Відеокарта не запрацювала, використовується процесор.
                </Typography>
          ) : null}
        </Box>

        <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 2 }}>
          Після зміни пошук облич перезапускається: доробляє відео, оброблені простіше, ніж обрано,
          і продовжує вже з новою швидкістю. Підтверджені обличчя зберігаються.
        </Typography>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Закрити</Button>
      </DialogActions>
    </Dialog>
  )
}
