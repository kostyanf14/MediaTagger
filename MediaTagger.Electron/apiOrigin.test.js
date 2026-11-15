const test = require('node:test')
const assert = require('node:assert/strict')
const { normalizeApiOrigin, apiHostIsLocal } = require('./apiOrigin')

test('an empty address is this PC', () => {
  assert.equal(normalizeApiOrigin('  '), 'http://localhost:5001')
  assert.equal(apiHostIsLocal(normalizeApiOrigin('')), true)
})

test('a NAS address keeps its port and drops a trailing slash', () => {
  assert.equal(normalizeApiOrigin('https://192.168.1.10:5001/'), 'https://192.168.1.10:5001')
  assert.equal(normalizeApiOrigin('192.168.1.10:5001'), 'https://192.168.1.10:5001')
  assert.equal(normalizeApiOrigin('https://nas.local:5001/api'), 'https://nas.local:5001')
  assert.equal(apiHostIsLocal('https://192.168.1.10:5001'), false)
})

test('a bare loopback host stays http', () => {
  assert.equal(normalizeApiOrigin('localhost:5001'), 'http://localhost:5001')
})

test('a password or a broken address is refused', () => {
  assert.equal(normalizeApiOrigin('not a url'), null)
  assert.equal(normalizeApiOrigin('https://user:pass@nas:5001'), null)
  assert.equal(normalizeApiOrigin('ftp://nas:5001'), null)
})
