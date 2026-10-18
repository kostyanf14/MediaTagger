import { MediaFile } from '../api/client'

export type SortField = 'name' | 'modified' | 'dateTaken' | 'size'
export type SortDir = 'asc' | 'desc'
export type GroupField = 'none' | 'date' | 'folder'

export interface FileGroup { label: string; files: MediaFile[] }

const NO_DATE_LABEL = 'Без дати'

// Built once: constructing a formatter costs far more than using one, and the
// group labels need it for every file in the library.
const dayFormat = new Intl.DateTimeFormat('uk-UA', { year: 'numeric', month: 'long', day: 'numeric' })
const dayLabels = new Map<number, string>()

// The capture-date tag is auto-filled from EXIF server-side but stays user-editable,
// so it's the field to trust; exif.dateTaken is only a fallback for older cached data.
export function captureDateMs(file: MediaFile): number {
  const iso = file.tags.dateTaken ?? file.exif?.dateTaken
  return iso ? new Date(iso).getTime() : 0
}

function sortKey(file: MediaFile, field: SortField): number | string {
  switch (field) {
    case 'name': return file.fileName.toLowerCase()
    case 'modified': return new Date(file.modified).getTime()
    case 'dateTaken': return captureDateMs(file)
    case 'size': return file.size
  }
}

export function sortFiles(files: MediaFile[], field: SortField, dir: SortDir): MediaFile[] {
  // Keys are worked out once per file rather than on every comparison — parsing two
  // dates per comparison is what made sorting a large folder slow.
  const keyed: { file: MediaFile; key: number | string }[] = []
  // A missing capture date means "unknown", not "oldest". Left in the main run it
  // would head an ascending timeline, so it is held back and appended either way.
  const undated: MediaFile[] = []
  for (const file of files) {
    const key = sortKey(file, field)
    if (field === 'dateTaken' && !key) undated.push(file)
    else keyed.push({ file, key })
  }

  keyed.sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0))
  if (dir === 'desc') keyed.reverse()

  const ordered = keyed.map(entry => entry.file)
  return undated.length ? ordered.concat(undated) : ordered
}

function dateGroupLabel(file: MediaFile): string {
  const ms = captureDateMs(file)
  if (!ms) return NO_DATE_LABEL
  const date = new Date(ms)
  const day = date.getFullYear() * 10000 + date.getMonth() * 100 + date.getDate()
  let label = dayLabels.get(day)
  if (!label) {
    label = dayFormat.format(date)
    dayLabels.set(day, label)
  }
  return label
}

function folderGroupLabel(file: MediaFile): string {
  const idx = Math.max(file.relativePath.lastIndexOf('/'), file.relativePath.lastIndexOf('\\'))
  return idx === -1 ? '.' : file.relativePath.slice(0, idx)
}

// Groups preserve the incoming (already-sorted) order — files are bucketed by label
// as encountered, so group order follows the current sort rather than being re-sorted.
export function groupFiles(files: MediaFile[], field: GroupField): FileGroup[] {
  if (field === 'none') return [{ label: '', files }]
  const labelFor = field === 'date' ? dateGroupLabel : folderGroupLabel
  const byLabel = new Map<string, MediaFile[]>()
  for (const file of files) {
    const label = labelFor(file)
    const bucket = byLabel.get(label)
    if (bucket) bucket.push(file)
    else byLabel.set(label, [file])
  }
  // A Map iterates in insertion order, which is the order the labels first appeared.
  return Array.from(byLabel, ([label, grouped]) => ({ label, files: grouped }))
}
