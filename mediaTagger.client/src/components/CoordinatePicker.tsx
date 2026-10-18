import { useEffect, useState } from 'react'
import {
  Dialog, DialogTitle, DialogContent, DialogActions,
  Box, Button, Typography, Chip
} from '@mui/material'
import { MapContainer, TileLayer, Marker, useMapEvents } from 'react-leaflet'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import MyLocationIcon from '@mui/icons-material/MyLocation'
import { Coordinates } from '../api/client'
import { MapAutoResize } from './MapAutoResize'

// Somewhere sensible to start when a photo carries no hint at all.
const FALLBACK_CENTER: [number, number] = [48.5, 31.5]
const FALLBACK_ZOOM = 5
// Close enough to place a pin precisely once we know roughly where to look.
const KNOWN_POINT_ZOOM = 14

function ClickToPlace({ onPlace }: { onPlace: (point: Coordinates) => void }) {
  useMapEvents({
    click: e => onPlace({ lat: e.latlng.lat, lng: e.latlng.lng })
  })
  return null
}

interface Props {
  open: boolean
  /** Where to drop the initial pin — the current tag, falling back to EXIF. */
  initial?: Coordinates | null
  fileName?: string
  onClose: () => void
  onConfirm: (point: Coordinates) => void
}

export function CoordinatePicker({ open, initial, fileName, onClose, onConfirm }: Props) {
  const [point, setPoint] = useState<Coordinates | null>(initial ?? null)

  // Reopening for another photo must not keep the previous pin.
  useEffect(() => {
    if (open) setPoint(initial ?? null)
  }, [open, initial])

  const center: [number, number] = initial
    ? [initial.lat, initial.lng]
    : FALLBACK_CENTER

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle sx={{ pb: 1 }}>
        Вкажіть місце на мапі
        {fileName && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
            {fileName}
          </Typography>
        )}
      </DialogTitle>

      <DialogContent sx={{ p: 0, position: 'relative' }}>
        <Box sx={{ height: '60vh', minHeight: 320 }}>
          {/* Remounting per photo keeps Leaflet's starting view in step with the pin */}
          {open && (
            <MapContainer
              key={`${center[0]},${center[1]}`}
              center={center}
              zoom={initial ? KNOWN_POINT_ZOOM : FALLBACK_ZOOM}
              style={{ height: '100%', width: '100%' }}
            >
              <TileLayer
                attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
                url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
              />
              {/* The dialog animates open, so the first measurement is wrong without this */}
              <MapAutoResize />
              <ClickToPlace onPlace={setPoint} />
              {point && (
                <Marker
                  position={[point.lat, point.lng]}
                  draggable
                  eventHandlers={{
                    dragend: e => {
                      const { lat, lng } = (e.target as L.Marker).getLatLng()
                      setPoint({ lat, lng })
                    }
                  }}
                />
              )}
            </MapContainer>
          )}
        </Box>
      </DialogContent>

      <DialogActions sx={{ px: 2, py: 1.5, gap: 1 }}>
        <Chip
          icon={<MyLocationIcon sx={{ fontSize: '16px !important' }} />}
          size="small"
          variant="outlined"
          label={point
            ? `${point.lat.toFixed(6)}, ${point.lng.toFixed(6)}`
            : 'Клікніть по мапі, щоб поставити точку'}
          sx={{ mr: 'auto' }}
        />
        <Button onClick={onClose}>Скасувати</Button>
        <Button
          variant="contained"
          disabled={!point}
          onClick={() => { if (point) { onConfirm(point); onClose() } }}
        >
          Застосувати
        </Button>
      </DialogActions>
    </Dialog>
  )
}
