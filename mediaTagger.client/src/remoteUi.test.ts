import test from 'node:test'
import assert from 'node:assert/strict'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { REMOTE_HIDDEN_ACTIONS, remoteHides } from './remoteUi.ts'

const src = dirname(fileURLToPath(import.meta.url))

function sources(dir: string): string {
  let text = ''
  for (const name of readdirSync(dir)) {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) text += sources(path)
    else if ((name.endsWith('.ts') || name.endsWith('.tsx')) && !name.startsWith('remoteUi'))
      text += readFileSync(path, 'utf8')
  }
  return text
}

test('a remote API hides every listed control, a local API hides none', () => {
  for (const action of REMOTE_HIDDEN_ACTIONS) {
    assert.equal(remoteHides(action.id, false), true)
    assert.equal(remoteHides(action.id, true), false)
  }
})

test('each listed control is referenced by the UI', () => {
  const ui = sources(src)
  const ids = REMOTE_HIDDEN_ACTIONS.map(action => action.id)
  assert.equal(new Set(ids).size, ids.length)
  for (const id of ids) assert.ok(ui.includes(`'${id}'`), id)
})
