import { useEffect, useRef } from 'react'
import { useMap } from 'react-leaflet'
import type L from 'leaflet'

/**
 * Leaflet measures its container once, when the map is created. Ours is created
 * inside a tab that is still `display: none` (and, for the picker, inside a dialog
 * that animates open), so that first measurement is 0x0 — which is why the map came
 * up at the wrong size with its tiles offset outside the viewport.
 *
 * Watching the box and re-measuring whenever it actually has one fixes both cases,
 * and keeps working for ordinary window resizes.
 */
export function MapAutoResize({ onResize }: { onResize?: (map: L.Map) => void }) {
  const map = useMap()
  const callback = useRef(onResize)
  callback.current = onResize

  useEffect(() => {
    const container = map.getContainer()
    let lastSize = ''

    const sync = () => {
      const { width, height } = container.getBoundingClientRect()
      if (!width || !height) return // still hidden — nothing worth measuring yet

      const size = `${Math.round(width)}x${Math.round(height)}`
      if (size === lastSize) return // guards against reacting to our own invalidateSize
      lastSize = size

      map.invalidateSize({ animate: false })
      callback.current?.(map)
    }

    const observer = new ResizeObserver(sync)
    observer.observe(container)
    sync()
    return () => observer.disconnect()
  }, [map])

  return null
}
