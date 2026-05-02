import React, { useState, useEffect } from 'react'
import {
  Drawer, Box, Typography, TextField, Autocomplete, Chip,
  IconButton, Divider, Button, Stack, Tooltip
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import SaveIcon from '@mui/icons-material/Save'
import LocationOnIcon from '@mui/icons-material/LocationOn'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import { MediaFile, FileTags, mediaApi } from '../api/client'
import { useElectron } from '../hooks/useElectron'

interface Props {
  file: MediaFile | null
  onClose: () => void
  onSaved: () => void
  allTags: { people: string[]; groups: string[]; albums: string[] }
}

export function TagsSidePanel({ file, onClose, onSaved, allTags }: Props) {
  const { revealInExplorer, openFile } = useElectron()
  const [tags, setTags] = useState<FileTags>({
    people: [], place: '', groups: [], albums: [], notes: ''
  })
  const [coordStr, setCoordStr] = useState('')
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    if (file) {
      setTags({ ...file.tags })
      setCoordStr(
        file.tags.coordinates
          ? `${file.tags.coordinates.lat}, ${file.tags.coordinates.lng}`
          : ''
      )
    }
  }, [file])

  const handleSave = async () => {
    if (!file) return
    setSaving(true)
    try {
      const finalTags = { ...tags }
      if (coordStr.trim()) {
        const parts = coordStr.split(',').map(s => parseFloat(s.trim()))
        if (parts.length === 2 && !isNaN(parts[0]) && !isNaN(parts[1])) {
          finalTags.coordinates = { lat: parts[0], lng: parts[1] }
        }
      } else {
        finalTags.coordinates = undefined
      }
      await mediaApi.updateTags(file.filePath, finalTags)
      onSaved()
    } finally {
      setSaving(false)
    }
  }

  return (
    <Drawer
      anchor="right"
      open={!!file}
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

          {/* Thumbnail */}
          {file.type === 'image' && (
            <Box sx={{ px: 2, pb: 1 }}>
              <Box
                component="img"
                src={mediaApi.getThumbnailUrl(file.filePath)}
                alt={file.fileName}
                sx={{
                  width: '100%',
                  maxHeight: 180,
                  objectFit: 'cover',
                  borderRadius: 1,
                  bgcolor: 'action.hover'
                }}
              />
            </Box>
          )}

          {file.exif?.camera && (
            <Typography variant="caption" color="text.secondary" sx={{ px: 2, pb: 0.5 }}>
              📷 {file.exif.camera}
              {file.exif.dateTaken && ` · ${new Date(file.exif.dateTaken).toLocaleDateString()}`}
            </Typography>
          )}

          <Divider />

          {/* Fields */}
          <Box sx={{ flex: 1, overflowY: 'auto', p: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <Autocomplete
              multiple freeSolo
              options={allTags.people}
              value={tags.people}
              onChange={(_, v) => setTags(t => ({ ...t, people: v as string[] }))}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Люди" size="small" placeholder="Додати людину..." />
              )}
            />

            <TextField
              label="Місце"
              size="small"
              value={tags.place}
              onChange={e => setTags(t => ({ ...t, place: e.target.value }))}
              placeholder="Київ, Хрещатик"
            />

            <Autocomplete
              multiple freeSolo
              options={allTags.groups}
              value={tags.groups}
              onChange={(_, v) => setTags(t => ({ ...t, groups: v as string[] }))}
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
              onChange={(_, v) => setTags(t => ({ ...t, albums: v as string[] }))}
              renderTags={(val, getProps) =>
                val.map((opt, i) => <Chip label={opt} size="small" color="info" {...getProps({ index: i })} key={i} />)
              }
              renderInput={(params) => (
                <TextField {...params} label="Альбоми" size="small" placeholder="Літо 2023..." />
              )}
            />

            <TextField
              label="Координати"
              size="small"
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
  )
}
