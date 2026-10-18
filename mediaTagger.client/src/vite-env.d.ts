/// <reference types="vite/client" />

declare module '*.png' {
  const src: string
  export default src
}
declare module '*.jpg' {
  const src: string
  export default src
}
declare module '*.svg' {
  const src: string
  export default src
}

declare module 'leaflet.markercluster' {
  import * as L from 'leaflet'
  export {}
}
