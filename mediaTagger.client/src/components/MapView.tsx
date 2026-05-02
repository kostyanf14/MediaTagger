import React, { useEffect, useState } from 'react'
import { MapContainer, TileLayer, Marker, Popup } from 'react-leaflet'
import { Box, Typography, CircularProgress, Chip } from '@mui/material'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import markerIcon2x from 'leaflet/dist/images/marker-icon-2x.png'
import markerIcon from 'leaflet/dist/images/marker-icon.png'
import markerShadow from 'leaflet/dist/images/marker-shadow.png'
import { MediaFile, mediaApi } from '../api/client'

// Fix default marker icons for Vite
delete (L.Icon.Default.prototype as any)._getIconUrl
L.Icon.Default.mergeOptions({
  iconRetinaUrl: markerIcon2x,
  iconUrl: markerIcon,
  shadowUrl: markerShadow,
})

function createThumbnailIcon(thumbnailUrl: string) {
  return L.divIcon({
    html: `<div style="
      width:48px; height:48px;
      border-radius:50%;
      border:2px solid #1976d2;
      overflow:hidden;
      box-shadow:0 2px 8px rgba(0,0,0,0.4);
      background:#111;
    ">
      <img src="${thumbnailUrl}" style="width:100%;height:100%;object-fit:cover;" />
    </div>`,
    className: '',
    iconSize: [48, 48],
    iconAnchor: [24, 24]
  })
}

interface Props {
  rootPath: string
}

export function MapView({ rootPath }: Props) {
  const [files, setFiles] = useState<MediaFile[]>([])
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!rootPath) return
    setLoading(true)
    mediaApi.getMapFiles(rootPath)
      .then(res => setFiles(res.data))
      .finally(() => setLoading(false))
  }, [rootPath])

  if (!rootPath) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
        <Typography color="text.secondary">Оберіть папку для відображення на мапі</Typography>
      </Box>
    )
  }

  if (loading) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
        <CircularProgress />
      </Box>
    )
  }

  if (files.length === 0) {
    return (
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
        <Typography color="text.secondary">
          Немає файлів з GPS координатами у цій папці
        </Typography>
      </Box>
    )
  }

  const center = files[0].tags.coordinates!
  const defaultCenter: [number, number] = [center.lat, center.lng]

  return (
    <Box sx={{ height: '100%', position: 'relative' }}>
      <Typography variant="caption" color="text.secondary"
        sx={{ position: 'absolute', top: 8, left: 8, zIndex: 1000, bgcolor: 'background.paper',
              px: 1, py: 0.3, borderRadius: 1, boxShadow: 1 }}>
        {files.length} файлів на мапі
      </Typography>

      <MapContainer
        center={defaultCenter}
        zoom={10}
        style={{ height: '100%', width: '100%' }}
      >
        <TileLayer
          attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
          url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
        />

        {files.map(file => {
          const coords = file.tags.coordinates!
          return (
            <Marker
              key={file.filePath}
              position={[coords.lat, coords.lng]}
              icon={file.type === 'image'
                ? createThumbnailIcon(mediaApi.getThumbnailUrl(file.filePath))
                : new L.Icon.Default()
              }
            >
              <Popup maxWidth={240}>
                <Box sx={{ minWidth: 180 }}>
                  {file.type === 'image' && (
                    <Box
                      component="img"
                      src={mediaApi.getThumbnailUrl(file.filePath)}
                      sx={{ width: '100%', height: 120, objectFit: 'cover', borderRadius: 1, mb: 1 }}
                    />
                  )}
                  <Typography variant="subtitle2" fontWeight={600}>{file.fileName}</Typography>
                  {file.tags.place && (
                    <Typography variant="caption" color="text.secondary">📍 {file.tags.place}</Typography>
                  )}
                  {file.tags.people.length > 0 && (
                    <Box sx={{ mt: 0.5, display: 'flex', flexWrap: 'wrap', gap: 0.3 }}>
                      {file.tags.people.map(p => (
                        <Chip key={p} label={p} size="small" sx={{ height: 18, fontSize: '0.65rem' }} />
                      ))}
                    </Box>
                  )}
                  {file.tags.albums.length > 0 && (
                    <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.3 }}>
                      🗂 {file.tags.albums.join(', ')}
                    </Typography>
                  )}
                </Box>
              </Popup>
            </Marker>
          )
        })}
      </MapContainer>
    </Box>
  )
}
