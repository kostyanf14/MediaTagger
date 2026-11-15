import test from 'node:test'
import assert from 'node:assert/strict'
import { FEATURES, FEATURE_ROUTES, featureForUrl, isFeatureUnsupported, offeredFeatures, supports } from './features.ts'

test('a health body without features is an older API', () => {
  assert.equal(offeredFeatures(null), null)
  assert.equal(offeredFeatures({ ok: true }).size, 0)
  const offered = offeredFeatures({ features: ['recaps', 3, ''] })
  assert.deepEqual([...(offered ?? [])], ['recaps'])
  assert.equal(supports(offered, FEATURES.recaps), true)
  assert.equal(supports(offered, FEATURES.unnamedFaces), false)
  assert.equal(supports(null, FEATURES.recaps), null)
})

test('each negotiated feature has the route a 404 would hide', () => {
  const ids = new Set(FEATURE_ROUTES.map(route => route.feature))
  for (const id of Object.values(FEATURES)) assert.equal(ids.has(id), true)
  assert.equal(featureForUrl('/api/media/recaps?rootPath=a'), FEATURES.recaps)
  assert.equal(featureForUrl('/api/faces/unnamed/skip'), FEATURES.unnamedFaces)
  assert.equal(featureForUrl('/api/media/files'), null)
})

test('a timeout is not an unsupported feature', () => {
  assert.equal(isFeatureUnsupported({ featureUnsupported: FEATURES.recaps }), true)
  assert.equal(isFeatureUnsupported(new Error('timeout')), false)
  assert.equal(isFeatureUnsupported(null), false)
})
