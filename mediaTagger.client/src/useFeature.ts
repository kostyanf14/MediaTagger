import { useEffect, useState } from 'react'
import { FeatureId, featureSupported, onFeatures } from './api/client'

export function useFeature(id: FeatureId): boolean | null {
  const [value, setValue] = useState<boolean | null>(() => featureSupported(id))
  useEffect(() => onFeatures(() => setValue(featureSupported(id))), [id])
  return value
}
