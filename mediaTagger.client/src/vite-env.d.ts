/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_ORIGIN?: string
  readonly MEDIATAGGER_API_URL?: string
}

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
