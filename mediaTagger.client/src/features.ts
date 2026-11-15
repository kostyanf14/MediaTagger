/** Names the API may list on `/health`. Same strings as ApiFeatures.All. */
export const FEATURES = {
  recaps: 'recaps',
  unnamedFaces: 'unnamed-faces'
} as const

export type FeatureId = typeof FEATURES[keyof typeof FEATURES]

export const FEATURE_UNSUPPORTED = 'Ця версія API не підтримує цю можливість. Оновіть сервер.'

/** Routes added with a feature. A 404 here means the server never offered it. */
export const FEATURE_ROUTES: { feature: FeatureId; path: string }[] = [
  { feature: FEATURES.recaps, path: '/media/recaps' },
  { feature: FEATURES.unnamedFaces, path: '/faces/unnamed' }
]

/**
 * null when /health was not reached. A body with no `features` array is an older API
 * and offers nothing from this list.
 */
export function offeredFeatures(body: unknown): Set<string> | null {
  if (body == null || typeof body !== 'object') return null
  const features = (body as { features?: unknown }).features
  if (!Array.isArray(features)) return new Set()
  return new Set(features.filter((item): item is string => typeof item === 'string' && item.length > 0))
}

/** null while the offer is unknown, false when this server did not list the feature. */
export function supports(offered: Set<string> | null, id: string): boolean | null {
  if (offered == null) return null
  return offered.has(id)
}

export function featureForUrl(url: string): FeatureId | null {
  const hit = FEATURE_ROUTES.find(route => url.includes(route.path))
  return hit ? hit.feature : null
}

/** True only for the rejection the client tags when a route was not offered. */
export function isFeatureUnsupported(error: unknown): boolean {
  return !!error && typeof error === 'object'
    && typeof (error as { featureUnsupported?: unknown }).featureUnsupported === 'string'
}
