import React, { useState, useEffect, useCallback, useRef } from 'react'
import {
  Drawer, Box, Typography, TextField, Autocomplete, Chip,
  IconButton, Divider, Button, Stack, Tooltip
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import SaveIcon from '@mui/icons-material/Save'
import LocationOnIcon from '@mui/icons-material/LocationOn'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import MapIcon from '@mui/icons-material/Map'
import { MediaFile, FileTags, Coordinates, BulkTagsRequest, mediaApi, THUMB_SIZE, THUMB_SIZE_SMALL } from '../api/client'
import { CoordinatePicker } from './CoordinatePicker'
import { Thumbnail } from './Thumbnail'
import { PhotoFaces } from './PhotoFaces'
import { PlaceHint } from './PlaceHint'
import { useElectron } from '../hooks/useElectron'

// Long enough that adding three tags in a row is one write, short enough that
// closing the panel straight after a click still lands.
const AUTOSAVE_DELAY_MS = 600
// How many thumbnails the bulk header shows before collapsing into a count.
const BULK_PREVIEW_COUNT = 5

interface Props {
  /** One file opens the full editor; several switch to adding tags in bulk. */
  selection: MediaFile[]
  onClose: () => void
  /** Fired after a bulk add, with what was added, so the grid can apply the same change. */
  onSaved: (applied: BulkTagsRequest) => void
  /** Fired after a tag chip saved itself, so the grid can catch up without a rescan. */
  onAutoSaved?: (filePath: string, tags: FileTags) => void
  allTags: { people: string[]; groups: string[]; albums: string[]; places: string[] }
  /** The open library; face suggestions are learned from the tags across all of it. */
  rootPath: string
}

type SaveState = 'idle' | 'saving' | 'saved' | 'error'

// <input type="datetime-local"> wants "YYYY-MM-DDTHH:mm" in local time, with no
// timezone suffix — Date's own ISO output is UTC and won't populate the field.
function toDateTimeLocal(iso?: string): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (isNaN(d.getTime())) return ''
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

// Reads back whatever is currently typed, so reopening the picker starts from the
// pin the user last placed rather than from the saved value.
function parseCoordinates(text: string): Coordinates | null {
  const parts = text.split(',').map(p => parseFloat(p.trim()))
  if (parts.length !== 2 || parts.some(isNaN)) return null
  const [lat, lng] = parts
  if (Math.abs(lat) > 90 || Math.abs(lng) > 180) return null
  return { lat, lng }
}

export function TagsSidePanel({ selection, onClose, onSaved, onAutoSaved, allTags, rootPath }: Props) {
  const file = selection.length === 1 ? selection[0] : null
  const bulk = selection.length > 1
  const { revealInExplorer, openFile } = useElectron()
  const [tags, setTags] = useState<FileTags>({
    people: [], place: '', groups: [], albums: [], notes: ''
  })
  const [coordStr, setCoordStr] = useState('')
  const [pickerOpen, setPickerOpen] = useState(false)
  const [dateTakenStr, setDateTakenStr] = useState('')
  const [saving, setSaving] = useState(false)
  const [autoSave, setAutoSave] = useState<SaveState>('idle')
  const [bulkPeople, setBulkPeople] = useState<string[]>([])
  const [bulkGroups, setBulkGroups] = useState<string[]>([])
  const [bulkAlbums, setBulkAlbums] = useState<string[]>([])
  const [bulkPlace, setBulkPlace] = useState('')
  const [bulkResult, setBulkResult] = useState<string | null>(null)
  const autoSaveTimer = useRef<ReturnType<typeof setTimeout>>()
  // Holds a write that has been promised to the user but not yet sent.
  const pending = useRef<{ path: string; tags: FileTags } | null>(null)
  const onAutoSavedRef = useRef(onAutoSaved)
  onAutoSavedRef.current = onAutoSaved

  // Keyed on the path, not the object: App patches the selected file after an
  // autosave, and re-seeding then would discard whatever is being edited.
  useEffect(() => {
    if (file) {
      setTags({ ...file.tags })
      setCoordStr(
        file.tags.coordinates
          ? `${file.tags.coordinates.lat}, ${file.tags.coordinates.lng}`
          : ''
      )
      setDateTakenStr(toDateTimeLocal(file.tags.dateTaken))
      setAutoSave('idle')
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [file?.filePath])

  // A different selection starts from empty fields, and drops any stale result note.
  useEffect(() => {
    setBulkPeople([]); setBulkGroups([]); setBulkAlbums([]); setBulkPlace('')
    setBulkResult(null)
  }, [selection.length, selection[0]?.filePath])

  const handleBulkApply = async () => {
    if (!bulk) return
    setSaving(true)
    try {
      const request: BulkTagsRequest = {
        filePaths: selection.map(f => f.filePath),
        people: bulkPeople,
        groups: bulkGroups,
        albums: bulkAlbums,
        place: bulkPlace.trim() || undefined
      }
      const res = await mediaApi.bulkAddTags(request)
      setBulkResult(`Оновлено файлів: ${res.data}`)
      setBulkPeople([]); setBulkGroups([]); setBulkAlbums([]); setBulkPlace('')
      onSaved(request)
    } catch {
      setBulkResult('Не вдалося застосувати теги')
    } finally {
      setSaving(false)
    }
  }

  // Combines the chip state with the free-text fields into what actually gets written.
  const buildTags = useCallback((source: FileTags, coordText: string = coordStr): FileTags => {
    const finalTags = { ...source }
    if (coordText.trim()) {
      const parts = coordText.split(',').map(s => parseFloat(s.trim()))
      if (parts.length === 2 && !isNaN(parts[0]) && !isNaN(parts[1])) {
        finalTags.coordinates = { lat: parts[0], lng: parts[1] }
      }
    } else {
      finalTags.coordinates = undefined
    }
    // datetime-local has no timezone — new Date() interprets it as local time,
    // matching what the picker displayed.
    finalTags.dateTaken = dateTakenStr ? new Date(dateTakenStr).toISOString() : undefined
    return finalTags
  }, [coordStr, dateTakenStr])

  const write = useCallback(async (path: string, payload: FileTags) => {
    await mediaApi.updateTags(path, payload)
    onAutoSavedRef.current?.(path, payload)
  }, [])

  /** Sends a promised write immediately — used when the panel is about to go away. */
  const flushPending = useCallback(() => {
    clearTimeout(autoSaveTimer.current)
    const owed = pending.current
    if (!owed) return
    pending.current = null
    write(owed.path, owed.tags).catch(() => {})
  }, [write])

  // Switching files or closing the panel must not swallow a tag the user just added.
  useEffect(() => flushPending, [file?.filePath, flushPending])

  /**
   * A finished tag edit — a chip added or removed, a place picked from the list —
   * saves itself. Typing does not: nobody wants half a word written to disk on
   * every keystroke, so free text still waits for the button.
   */
  const applyTagChange = (patch: Partial<FileTags>, coordText?: string) => {
    if (!file) return
    const next = { ...tags, ...patch }
    setTags(next)

    const payload = buildTags(next, coordText)
    pending.current = { path: file.filePath, tags: payload }
    setAutoSave('saving')

    clearTimeout(autoSaveTimer.current)
    autoSaveTimer.current = setTimeout(async () => {
      const owed = pending.current
      if (!owed) return
      pending.current = null
      try {
        await write(owed.path, owed.tags)
        setAutoSave('saved')
      } catch {
        setAutoSave('error')
      }
    }, AUTOSAVE_DELAY_MS)
  }

  const handleSave = async () => {
    if (!file) return
    setSaving(true)
    // The button covers everything, so any chip write still owed is redundant.
    clearTimeout(autoSaveTimer.current)
    pending.current = null
    try {
      // write() reports the new tags upward, which patches the grid in place. Asking
      // for a folder reload here instead would blank the view and drop the selection,
      // which looks like the whole page reloading.
      await write(file.filePath, buildTags(tags))
      setAutoSave('saved')
    } catch {
      setAutoSave('error')
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
    <Drawer
      anchor="right"
      open={selection.length > 0}
      onClose={onClose}
      variant="persistent"
      sx={{
        '& .MuiDrawer-paper': {
          width: 340,
          bgcolor: 'background.paper',
          borderLeft: '1px solid',
          borderColor: 'divider',
          display: 'flex',
          flexDirection: 'column'
        }
      }}
    >
      {bulk && (
        <>
          <Box sx={{ p: 2, display: 'flex', alignItems: 'center', gap: 1 }}>
            <Typography variant="subtitle1" fontWeight={600} sx={{ flex: 1 }}>
              Вибрано {selection.length} файлів
            </Typography>
            <IconButton size="small" onClick={onClose}>
              <CloseIcon fontSize="small" />
            </IconButton>
          </Box>

          {/* A strip of what is selected, so it is obvious what will be tagged */}
          <Box sx={{ px: 2, pb: 1, display: 'flex', gap: 0.5, overflowX: 'auto' }}>
            {selection.slice(0, BULK_PREVIEW_COUNT).map(f => (
              <Box key={f.filePath} sx={{ flexShrink: 0, width: 56, height: 42, borderRadius: 0.5, overflow: 'hidden' }}>
                <Thumbnail
                  src={mediaApi.getThumbnailUrl(f.filePath, THUMB_SIZE_SMALL)}
                  alt={f.fileName}
                  type={f.type}
                  iconSize={16}
                />
              </Box>
            ))}
            {selection.length > BULK_PREVIEW_COUNT && (
              <Box sx={{
                flexShrink: 0, width: 56, height: 42, borderRadius: 0.5,
                bgcolor: 'action.hover', display: 'flex', alignItems: 'center', justifyContent: 'center'
              }}>
                <Typography variant="caption" color="text.secondary">
                  +{selection.length - BULK_PREVIEW_COUNT}
                </Typography>
              </Box>
            )}
          </Box>

          <Divider />

          <Box sx={{ flex: 1, overflowY: 'auto', p: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <Typography variant="caption" color="text.secondary">
              Теги <strong>додаються</strong> до наявних. Дата, координати та нотатки
              кожного файлу лишаються без змін.
            </Typography>

            <Autocomplete
              multiple freeSolo
              options={allTags.people}
              value={bulkPeople}
              onChange={(_, v) => setBulkPeople(v as string[])}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Люди" size="small" placeholder="Додати людину..." />
              )}
            />

            <Autocomplete
              multiple freeSolo
              options={allTags.groups}
              value={bulkGroups}
              onChange={(_, v) => setBulkGroups(v as string[])}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" color="secondary" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Група людей" size="small" placeholder="Сім'я, Друзі..." />
              )}
            />

            <Autocomplete
              multiple freeSolo
              options={allTags.albums}
              value={bulkAlbums}
              onChange={(_, v) => setBulkAlbums(v as string[])}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" color="info" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Альбоми" size="small" placeholder="Літо 2023..." />
              )}
            />

            <Autocomplete
              freeSolo
              selectOnFocus
              handleHomeEndKeys
              options={allTags.places}
              inputValue={bulkPlace}
              onInputChange={(_, value, reason) => { if (reason === 'input') setBulkPlace(value) }}
              onChange={(_, value) => setBulkPlace(typeof value === 'string' ? value : '')}
              renderInput={(params) => (
                <TextField
                  {...params}
                  label="Місце"
                  size="small"
                  placeholder="Київ, Хрещатик"
                  helperText="Заповніть, щоб перезаписати місце в усіх вибраних"
                />
              )}
            />
          </Box>

          <Divider />
          <Box sx={{ p: 2 }}>
            {bulkResult && (
              <Typography variant="caption" color="text.secondary"
                sx={{ display: 'block', mb: 1, textAlign: 'center' }}>
                {bulkResult}
              </Typography>
            )}
            <Button
              fullWidth variant="contained"
              startIcon={<SaveIcon />}
              onClick={handleBulkApply}
              disabled={saving || (!bulkPeople.length && !bulkGroups.length && !bulkAlbums.length && !bulkPlace.trim())}
            >
              {saving ? 'Застосування...' : `Додати теги до ${selection.length} файлів`}
            </Button>
          </Box>
        </>
      )}

      {file && (
        <>
          {/* Header */}
          <Box sx={{ p: 2, display: 'flex', alignItems: 'center', gap: 1 }}>
            <Typography variant="subtitle1" fontWeight={600} noWrap sx={{ flex: 1 }}>
              {file.fileName}
            </Typography>
            <Tooltip title="Показати у провіднику">
              <IconButton size="small" onClick={() => revealInExplorer(file.filePath)}>
                <FolderOpenIcon fontSize="small" />
              </IconButton>
            </Tooltip>
            <Tooltip title="Відкрити файл">
              <IconButton size="small" onClick={() => openFile(file.filePath)}>
                <OpenInNewIcon fontSize="small" />
              </IconButton>
            </Tooltip>
            <IconButton size="small" onClick={onClose}>
              <CloseIcon fontSize="small" />
            </IconButton>
          </Box>

          {/* Thumbnail — videos show their extracted frame */}
          <Box sx={{ px: 2, pb: 1 }}>
            <Thumbnail
              src={mediaApi.getThumbnailUrl(file.filePath, THUMB_SIZE)}
              alt={file.fileName}
              type={file.type}
              iconSize={32}
              sx={{
                width: '100%',
                height: 180,
                borderRadius: 1
              }}
            />
          </Box>

          {file.exif?.camera && (
            <Typography variant="caption" color="text.secondary" sx={{ px: 2, pb: 0.5 }}>
              📷 {file.exif.camera}
            </Typography>
          )}

          <Divider />

          {/* Fields */}
          <Box sx={{ flex: 1, overflowY: 'auto', p: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <TextField
              label="Дата зйомки"
              type="datetime-local"
              size="small"
              value={dateTakenStr}
              onChange={e => setDateTakenStr(e.target.value)}
              InputLabelProps={{ shrink: true }}
              helperText={
                file.exif?.dateTaken
                  ? `EXIF: ${new Date(file.exif.dateTaken).toLocaleString()}`
                  : 'Не визначено з EXIF — можна вказати вручну'
              }
            />

            <Autocomplete
              multiple freeSolo
              options={allTags.people}
              value={tags.people}
              onChange={(_, v) => applyTagChange({ people: v as string[] })}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Люди" size="small" placeholder="Додати людину..." />
              )}
            />

            <PhotoFaces
              rootPath={rootPath}
              file={file}
              people={tags.people}
              allPeople={allTags.people}
              onAddPerson={person => applyTagChange({ people: [...tags.people, person] })}
            />

            <Autocomplete
              freeSolo
              selectOnFocus
              handleHomeEndKeys
              options={allTags.places}
              inputValue={tags.place}
              onInputChange={(_, value, reason) => {
                // Typing only. Picking an option or clearing arrives via onChange,
                // which also triggers the save.
                if (reason === 'input') setTags(t => ({ ...t, place: value }))
              }}
              onChange={(_, value) => applyTagChange({ place: typeof value === 'string' ? value : '' })}
              renderInput={(params) => (
                <TextField {...params} label="Місце" size="small" placeholder="Київ, Хрещатик" />
              )}
            />

            <PlaceHint
              rootPath={rootPath}
              coordinates={parseCoordinates(coordStr) ?? file.exif?.gpsCoordinates ?? null}
              place={tags.place}
              onPick={place => applyTagChange({ place })}
            />

            <Autocomplete
              multiple freeSolo
              options={allTags.groups}
              value={tags.groups}
              onChange={(_, v) => applyTagChange({ groups: v as string[] })}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" color="secondary" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Група людей" size="small" placeholder="Сім'я, Друзі..." />
              )}
            />

            <Autocomplete
              multiple freeSolo
              options={allTags.albums}
              value={tags.albums}
              onChange={(_, v) => applyTagChange({ albums: v as string[] })}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" color="info" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Альбоми" size="small" placeholder="Літо 2023..." />
              )}
            />

            <Box sx={{ display: 'flex', alignItems: 'flex-start', gap: 1 }}>
              <TextField
                label="Координати"
                size="small"
                sx={{ flex: 1 }}
                value={coordStr}
                onChange={e => setCoordStr(e.target.value)}
                placeholder="50.4501, 30.5234"
                InputProps={{
                  startAdornment: <LocationOnIcon fontSize="small" sx={{ mr: 0.5, color: 'text.secondary' }} />
                }}
                helperText={
                  file.exif?.gpsCoordinates && !coordStr
                    ? `EXIF: ${file.exif.gpsCoordinates.lat.toFixed(4)}, ${file.exif.gpsCoordinates.lng.toFixed(4)}`
                    : 'lat, lng'
                }
              />
              <Tooltip title="Вибрати точку на мапі">
                <IconButton size="small" onClick={() => setPickerOpen(true)} sx={{ mt: 0.5 }}>
                  <MapIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Box>

            <TextField
              label="Нотатки"
              size="small"
              multiline rows={3}
              value={tags.notes}
              onChange={e => setTags(t => ({ ...t, notes: e.target.value }))}
            />
          </Box>

          <Divider />
          <Box sx={{ p: 2 }}>
            {autoSave !== 'idle' && (
              <Typography
                variant="caption"
                color={autoSave === 'error' ? 'error.main' : 'text.secondary'}
                sx={{ display: 'block', mb: 1, textAlign: 'center' }}
              >
                {autoSave === 'saving' && 'Збереження тегів…'}
                {autoSave === 'saved' && '✓ Теги збережено'}
                {autoSave === 'error' && 'Не вдалося зберегти — натисніть «Зберегти теги»'}
              </Typography>
            )}
            <Button
              fullWidth variant="contained"
              startIcon={<SaveIcon />}
              onClick={handleSave}
              disabled={saving}
            >
              {saving ? 'Збереження...' : 'Зберегти теги'}
            </Button>
          </Box>
        </>
      )}
    </Drawer>

      {file && (
        <CoordinatePicker
          open={pickerOpen}
          initial={parseCoordinates(coordStr) ?? file.tags.coordinates ?? file.exif?.gpsCoordinates ?? null}
          fileName={file.fileName}
          onClose={() => setPickerOpen(false)}
          onConfirm={point => {
            const text = `${point.lat.toFixed(6)}, ${point.lng.toFixed(6)}`
            setCoordStr(text)
            applyTagChange({ coordinates: point }, text)
          }}
        />
      )}
    </>
  )
}
