import React, { useEffect, useState, useRef, useCallback } from 'react'
import { MapContainer, TileLayer, useMap } from 'react-leaflet'
import { Box, Typography, CircularProgress, Paper } from '@mui/material'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import 'leaflet.markercluster/dist/MarkerCluster.css'
import 'leaflet.markercluster/dist/MarkerCluster.Default.css'
import 'leaflet.markercluster'
import markerIcon2x from 'leaflet/dist/images/marker-icon-2x.png'
import markerIcon from 'leaflet/dist/images/marker-icon.png'
import markerShadow from 'leaflet/dist/images/marker-shadow.png'
import { MediaFile, mediaApi, THUMB_SIZE_SMALL } from '../api/client'
import { LightboxViewer } from './LightboxViewer'
import { MapAutoResize } from './MapAutoResize'

delete (L.Icon.Default.prototype as any)._getIconUrl
L.Icon.Default.mergeOptions({
  iconRetinaUrl: markerIcon2x,
  iconUrl: markerIcon,
  shadowUrl: markerShadow,
})

function distanceMeters(lat1: number, lng1: number, lat2: number, lng2: number): number {
  const R = 6371000
  const dLat = (lat2 - lat1) * Math.PI / 180
  const dLng = (lng2 - lng1) * Math.PI / 180
  const a = Math.sin(dLat / 2) ** 2 +
    Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) *
    Math.sin(dLng / 2) ** 2
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

function getNearbyFiles(files: MediaFile[], lat: number, lng: number, radiusMeters = 500): MediaFile[] {
  return files
    .filter(f => f.tags.coordinates)
    .map(f => ({ file: f, dist: distanceMeters(lat, lng, f.tags.coordinates!.lat, f.tags.coordinates!.lng) }))
    .filter(x => x.dist <= radiusMeters)
    .sort((a, b) => a.dist - b.dist)
    .map(x => x.file)
}

function createThumbnailIcon(thumbnailUrl: string) {
  return L.divIcon({
    html: `<div style="width:48px;height:48px;border-radius:50%;border:2px solid #1976d2;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.5);background:#111;">
      <img src="${thumbnailUrl}" style="width:100%;height:100%;object-fit:cover;" onerror="this.style.display='none'"/>
    </div>`,
    className: '', iconSize: [48, 48], iconAnchor: [24, 24], popupAnchor: [0, -28]
  })
}

function createClusterIcon(cluster: L.MarkerCluster) {
  const markers = cluster.getAllChildMarkers() as any[]
  const count = cluster.getChildCount()
  const thumbs = markers.slice(0, 4).map(m => m.options.thumbnailUrl).filter(Boolean)
  const size = count > 99 ? 72 : count > 9 ? 64 : 56
  const thumbHtml = thumbs.length >= 2 ? thumbs.map((url, i) => {
    const half = Math.floor(size / 2) - 1
    const pos = ['top:1px;left:1px', 'top:1px;right:1px', 'bottom:1px;left:1px', 'bottom:1px;right:1px']
    return `<img src="${url}" style="position:absolute;${pos[i]};width:${half}px;height:${half}px;object-fit:cover;border-radius:2px;" onerror="this.style.display='none'"/>`
  }).join('') : ''
  return L.divIcon({
    html: `<div style="width:${size}px;height:${size}px;border-radius:50%;border:3px solid #1976d2;background:#1a1a1a;position:relative;overflow:hidden;box-shadow:0 3px 10px rgba(0,0,0,0.5);">
      ${thumbHtml}
      <div style="position:absolute;inset:0;background:${thumbs.length >= 2 ? 'rgba(0,0,0,0.45)' : 'transparent'};display:flex;align-items:center;justify-content:center;z-index:2;">
        <span style="color:white;font-weight:700;font-size:${count > 99 ? 13 : 15}px;font-family:sans-serif;">${count}</span>
      </div>
    </div>`,
    className: '', iconSize: [size, size], iconAnchor: [size / 2, size / 2]
  })
}

// Stores the Leaflet map instance into an external ref
function MapRefCapture({ mapRef }: { mapRef: React.MutableRefObject<L.Map | null> }) {
  const map = useMap()
  useEffect(() => { mapRef.current = map }, [map, mapRef])
  return null
}

interface ClusterLayerProps {
  files: MediaFile[]
  onMarkerClick: (file: MediaFile) => void
}

function ClusterLayer({ files, onMarkerClick }: ClusterLayerProps) {
  const map = useMap()
  const clusterRef = useRef<L.MarkerClusterGroup | null>(null)

  useEffect(() => {
    if (clusterRef.current) map.removeLayer(clusterRef.current)
    const cluster = (L as any).markerClusterGroup({
      iconCreateFunction: createClusterIcon,
      maxClusterRadius: 80,
      spiderfyOnMaxZoom: true,
      showCoverageOnHover: false,
      zoomToBoundsOnClick: true,
      animate: true,
      animateAddingMarkers: false,
      disableClusteringAtZoom: 16,
      // Lets a few thousand pins go in across several frames instead of one long one.
      chunkedLoading: true
    })
    const markers = files.map(file => {
      const coords = file.tags.coordinates!
      const thumbUrl = mediaApi.getThumbnailUrl(file.filePath, THUMB_SIZE_SMALL)
      const marker = L.marker([coords.lat, coords.lng], {
        icon: createThumbnailIcon(thumbUrl), thumbnailUrl: thumbUrl
      } as any)
      marker.on('click', () => onMarkerClick(file))
      return marker
    })
    // One bulk insert: adding them singly re-clusters after every marker.
    cluster.addLayers(markers)
    map.addLayer(cluster)
    clusterRef.current = cluster
    return () => { if (clusterRef.current) { map.removeLayer(clusterRef.current); clusterRef.current = null } }
  }, [files, map, onMarkerClick])

  return null
}

interface Props {
  roots: string[]
  /** Bumped when tags change; coordinates are tags, so the pins go stale too. */
  refreshKey: number
  /** Only the visible tab refetches. */
  active: boolean
  onShowInApp?: (file: MediaFile) => void
}

export function MapView({ roots, refreshKey, active, onShowInApp }: Props) {
  const [files, setFiles] = useState<MediaFile[]>([])
  const [loading, setLoading] = useState(false)
  const [lightboxFile, setLightboxFile] = useState<MediaFile | null>(null)
  const [lightboxFiles, setLightboxFiles] = useState<MediaFile[]>([])
  const [lightboxLabel, setLightboxLabel] = useState('')

  // mapRef holds the Leaflet instance — used to save/restore view imperatively
  const mapRef = useRef<L.Map | null>(null)
  // savedView holds center+zoom captured just before lightbox opens
  const savedView = useRef<{ center: L.LatLng; zoom: number } | null>(null)
  // Which folder the current view was framed for, so panning is never undone by a
  // later resize or by the pins refreshing after a tag edit — only a different
  // folder re-frames the map.
  const fittedFor = useRef<string | null>(null)
  // The folder the pins on screen belong to.
  const [filesFolder, setFilesFolder] = useState('')

  const fitToFiles = useCallback((map: L.Map) => {
    if (files.length === 0 || fittedFor.current === filesFolder) return
    const { width, height } = map.getContainer().getBoundingClientRect()
    if (!width || !height) return // hidden tab: MapAutoResize will call back once shown
    map.fitBounds(
      L.latLngBounds(files.map(f => [f.tags.coordinates!.lat, f.tags.coordinates!.lng] as [number, number])),
      { padding: [40, 40], maxZoom: 14, animate: false }
    )
    fittedFor.current = filesFolder
  }, [files, filesFolder])

  // A new folder means a new view; frame it now if the map is already on screen.
  useEffect(() => {
    if (mapRef.current) fitToFiles(mapRef.current)
  }, [fitToFiles])

  const folderPath = roots.join('\n')

  // What the pins currently on the map were built from.
  const loadedFor = useRef<string | null>(null)

  useEffect(() => {
    if (!folderPath || !active) return

    const signature = `${folderPath}|${refreshKey}`
    if (loadedFor.current === signature) return
    loadedFor.current = signature

    setLoading(true)
    mediaApi.getMapFiles(roots)
      .then(r => { setFiles(r.data); setFilesFolder(folderPath) })
      // Let a failed load be retried rather than remembered as done.
      .catch(() => { loadedFor.current = null })
      .finally(() => setLoading(false))
  }, [folderPath, roots, refreshKey, active])

  const handleMarkerClick = useCallback((file: MediaFile) => {
    // Snapshot current view right before opening lightbox
    if (mapRef.current) {
      savedView.current = {
        center: mapRef.current.getCenter(),
        zoom: mapRef.current.getZoom()
      }
    }
    const coords = file.tags.coordinates!
    const nearby = getNearbyFiles(files, coords.lat, coords.lng, 500)
    setLightboxFiles(nearby.length > 0 ? nearby : [file])
    setLightboxFile(file)
    setLightboxLabel(nearby.length > 1 ? `${nearby.length} фото поруч` : '')
  }, [files])

  const handleLightboxClose = useCallback(() => {
    setLightboxFile(null)
    // Directly call setView on the Leaflet instance — no React state involved
    // This runs synchronously before any re-render, so Leaflet never sees a resize
    if (mapRef.current && savedView.current) {
      mapRef.current.invalidateSize()
      mapRef.current.setView(savedView.current.center, savedView.current.zoom, { animate: false })
    }
  }, [])

  if (!folderPath) return (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
      <Typography color="text.secondary">Оберіть папку для відображення на мапі</Typography>
    </Box>
  )

  // Only the very first load replaces the map. A refresh keeps it mounted — tearing
  // it down would throw away the tiles already fetched and where the user had panned to.
  if (loading && files.length === 0) return (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
      <CircularProgress />
    </Box>
  )

  if (files.length === 0) return (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
      <Typography color="text.secondary">Немає файлів з GPS координатами</Typography>
    </Box>
  )

  return (
    <Box sx={{ height: '100%', position: 'relative' }}>
      <Paper elevation={2} sx={{
        position: 'absolute', top: 10, left: 10, zIndex: 1000,
        px: 1.5, py: 0.5, borderRadius: 2,
        bgcolor: 'background.paper', border: '1px solid', borderColor: 'divider'
      }}>
        <Typography variant="caption" color="text.secondary">
          📍 {files.length} фото у «{folderPath.split(/[\\/]/).pop()}» · клік на маркер — перегляд
        </Typography>
        {loading && <CircularProgress size={12} sx={{ ml: 1, verticalAlign: 'middle' }} />}
      </Paper>

      <style>{`
        .marker-cluster-small, .marker-cluster-medium, .marker-cluster-large { background: transparent !important; }
        .marker-cluster-small div, .marker-cluster-medium div, .marker-cluster-large div { background: transparent !important; }
      `}</style>

      <MapContainer
        center={[files[0].tags.coordinates!.lat, files[0].tags.coordinates!.lng]}
        zoom={6}
        style={{ height: '100%', width: '100%' }}
      >
        <TileLayer
          attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
          url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
        />
        <MapRefCapture mapRef={mapRef} />
        <MapAutoResize onResize={fitToFiles} />
        <ClusterLayer files={files} onMarkerClick={handleMarkerClick} />
      </MapContainer>

      <LightboxViewer
        file={lightboxFile}
        files={lightboxFiles}
        onClose={handleLightboxClose}
        onNavigate={setLightboxFile}
        onOpenTags={() => setLightboxFile(null)}
        onShowInApp={onShowInApp}
        contextLabel={lightboxLabel}
      />
    </Box>
  )
}
