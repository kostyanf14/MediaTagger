import React, { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useVirtualizer } from '@tanstack/react-virtual'
import {
  Box, Typography, Button, IconButton, Tooltip, LinearProgress, Chip, Alert, Skeleton
} from '@mui/material'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import DoneAllIcon from '@mui/icons-material/DoneAll'
import RefreshIcon from '@mui/icons-material/Refresh'
import StopIcon from '@mui/icons-material/Stop'
import FaceRetouchingNaturalIcon from '@mui/icons-material/FaceRetouchingNatural'
import {
  facesApi, mediaApi, FaceScanStatus, FaceSuggestionsResponse, FaceSuggestion, BulkTagsRequest, THUMB_SIZE
} from '../api/client'
import { FaceAvatar } from './FaceAvatar'

// Often enough that the counter visibly moves, rare enough to stay out of the scan's way.
const STATUS_POLL_MS = 1500
const CARD_SIZE = 104
const CARD_GAP = 8
const HEADER_HEIGHT = 48
// Card plus the filename under it, and a little air before the next row.
const CARD_ROW_HEIGHT = CARD_SIZE + 22
const OVERSCAN_ROWS = 3
// "Add to all" only takes matches this close; weaker ones are left to be checked one by one.
const CONFIDENT_SCORE = 0.55

type SuggestionGroup = { person: string; total: number; items: FaceSuggestion[] }
type FaceRow =
  | { kind: 'header'; key: string; group: SuggestionGroup; remaining: number }
  | { kind: 'cards'; key: string; person: string; items: FaceSuggestion[] }

function buildFaceRows(groups: SuggestionGroup[], columns: number, sent: Map<string, number>): FaceRow[] {
  const rows: FaceRow[] = []
  const perRow = Math.max(1, columns)
  for (const group of groups) {
    const answered = (sent.get(group.person) ?? group.items.length) - group.items.length
    rows.push({ kind: 'header', key: `h:${group.person}`, group, remaining: group.total - answered })
    for (let i = 0; i < group.items.length; i += perRow) {
      const items = group.items.slice(i, i + perRow)
      rows.push({ kind: 'cards', key: `c:${items[0].faceId}`, person: group.person, items })
    }
  }
  return rows
}

interface Props {
  roots: string[]
  /** Bumped whenever tags change anywhere — a new tag is new training data. */
  refreshKey: number
  /** Only the visible tab polls and refetches. */
  active: boolean
  /** Fired after suggestions were accepted, with the tags the server added. */
  onTagsAdded: (applied: BulkTagsRequest) => void
}

/** Similarity → how the card is outlined. ArcFace scores above ~0.6 are near-certain. */
function scoreColor(score: number): string {
  if (score >= CONFIDENT_SCORE) return 'success.main'
  if (score >= 0.5) return 'warning.main'
  return 'divider'
}

/** The whole photo with the face marked, so a crop can be judged in context. */
function FaceContext({ item }: { item: FaceSuggestion }) {
  return (
    <Box sx={{ position: 'relative', width: 260 }}>
      <Box component="img" src={mediaApi.getThumbnailUrl(item.filePath, THUMB_SIZE)} alt={item.fileName}
        crossOrigin="anonymous" sx={{ width: '100%', display: 'block', borderRadius: 0.5 }} />
      <Box sx={{
        position: 'absolute',
        left: `${item.x * 100}%`, top: `${item.y * 100}%`,
        width: `${item.w * 100}%`, height: `${item.h * 100}%`,
        border: '2px solid', borderColor: 'warning.main', borderRadius: 0.5
      }} />
      <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
        {item.fileName} · схожість {Math.round(item.score * 100)}%
      </Typography>
    </Box>
  )
}

function FaceCard({ item, person, onAccept, onReject }: {
  item: FaceSuggestion
  person: string
  onAccept: (person: string, items: FaceSuggestion[]) => void
  onReject: (person: string, items: FaceSuggestion[]) => void
}) {
  return (
    <Box sx={{
      position: 'relative', width: CARD_SIZE, flexShrink: 0,
      '&:hover .face-actions': { opacity: 1 }
    }}>
      <Tooltip title={<FaceContext item={item} />} placement="top" enterDelay={500}
        componentsProps={{ tooltip: { sx: { maxWidth: 'none', p: 1 } } }}>
        <Box>
          <FaceAvatar filePath={item.filePath} box={item} size={CARD_SIZE} variant="rounded"
            sx={{ border: '2px solid', borderColor: scoreColor(item.score) }} />
        </Box>
      </Tooltip>
      <Box className="face-actions" sx={{
        position: 'absolute', top: 4, right: 4, display: 'flex', gap: 0.5,
        opacity: 0, transition: 'opacity 0.15s'
      }}>
        <Tooltip title={`Так, це ${person}`}>
          <IconButton size="small" onClick={() => onAccept(person, [item])}
            sx={{ bgcolor: 'success.dark', p: 0.25, '&:hover': { bgcolor: 'success.main' } }}>
            <CheckIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
        <Tooltip title="Ні">
          <IconButton size="small" onClick={() => onReject(person, [item])}
            sx={{ bgcolor: 'error.dark', p: 0.25, '&:hover': { bgcolor: 'error.main' } }}>
            <CloseIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
      </Box>
      <Typography variant="caption" color="text.secondary" noWrap
        sx={{ display: 'block', fontSize: '0.65rem' }} title={item.fileName}>
        {item.fileName}
      </Typography>
    </Box>
  )
}

export function FacesPanel({ roots, refreshKey, active, onTagsAdded }: Props) {
  const [status, setStatus] = useState<FaceScanStatus | null>(null)
  const [data, setData] = useState<FaceSuggestionsResponse | null>(null)
  const [loading, setLoading] = useState(false)
  // Faces already answered. A refetch that started before the answer would otherwise
  // bring the card back for a moment.
  const [decided, setDecided] = useState<Set<number>>(new Set())
  const [applyingAll, setApplyingAll] = useState(false)
  const applyingAllRef = useRef(false)
  const loadedFor = useRef<string | null>(null)
  const scanWasActive = useRef(false)

  const load = useCallback((quiet = false) => {
    if (!roots.length) return
    if (!quiet) setLoading(true)
    facesApi.suggestions(roots)
      .then(r => setData(r.data))
      .catch(() => { loadedFor.current = null })
      .finally(() => setLoading(false))
  }, [roots])

  useEffect(() => {
    if (!roots.length || !active) return
    const signature = `${roots.join('\n')}|${refreshKey}`
    if (loadedFor.current === signature) return
    // A refetch after an accept keeps the cards on screen instead of flashing skeletons.
    const quiet = loadedFor.current?.startsWith(`${roots.join('\n')}|`) ?? false
    loadedFor.current = signature
    load(quiet)
  }, [roots, refreshKey, active, load])

  // Different library, different faces.
  useEffect(() => { setData(null); setDecided(new Set()) }, [roots])

  useEffect(() => {
    if (!active) return
    let cancelled = false
    const tick = () => facesApi.status()
      .then(r => {
        if (cancelled) return
        setStatus(r.data)
        // The scan just finished: everything it found can now be suggested.
        if (scanWasActive.current && !r.data.active) load(true)
        scanWasActive.current = r.data.active
      })
      .catch(() => {})
    tick()
    const timer = setInterval(tick, STATUS_POLL_MS)
    return () => { cancelled = true; clearInterval(timer) }
  }, [active, load])

  const markDecided = (items: FaceSuggestion[]) =>
    setDecided(prev => { const next = new Set(prev); items.forEach(i => next.add(i.faceId)); return next })

  const accept = async (person: string, items: FaceSuggestion[]) => {
    if (!items.length) return
    markDecided(items)
    try {
      await facesApi.accept(items.map(i => ({ faceId: i.faceId, filePath: i.filePath, person })))
      onTagsAdded({ filePaths: items.map(i => i.filePath), people: [person], groups: [], albums: [] })
    } catch {
      // Put the cards back so the answer can be given again.
      setDecided(prev => { const next = new Set(prev); items.forEach(i => next.delete(i.faceId)); return next })
    }
  }

  const reject = async (person: string, items: FaceSuggestion[]) => {
    if (!items.length) return
    markDecided(items)
    try {
      await facesApi.reject(items.map(i => ({ faceId: i.faceId, filePath: i.filePath, person })))
    } catch {
      setDecided(prev => { const next = new Set(prev); items.forEach(i => next.delete(i.faceId)); return next })
    }
  }

  const startScan = () => {
    facesApi.scan(roots).then(r => { setStatus(r.data); scanWasActive.current = r.data.active }).catch(() => {})
  }

  const [scroller, setScroller] = useState<HTMLDivElement | null>(null)
  const [width, setWidth] = useState(0)

  const people = useMemo(() => (data?.people ?? [])
    .map(p => ({ ...p, items: p.items.filter(i => !decided.has(i.faceId)) }))
    .filter(p => p.items.length > 0), [data, decided])

  // The percent is what the tooltip shows, so 80.4% (shown as 80) stays for a manual check.
  const above80 = people.reduce((n, g) => n + g.items.filter(i => Math.round(i.score * 100) > 80).length, 0)

  const columns = Math.max(1, Math.floor((width + CARD_GAP) / (CARD_SIZE + CARD_GAP)))
  const rows = useMemo(() => {
    const sent = new Map((data?.people ?? []).map(p => [p.person, p.items.length]))
    return buildFaceRows(people, columns, sent)
  }, [people, columns, data])

  const virtualizer = useVirtualizer({
    count: active ? rows.length : 0,
    getScrollElement: () => scroller,
    estimateSize: index => rows[index]?.kind === 'header' ? HEADER_HEIGHT : CARD_ROW_HEIGHT,
    overscan: OVERSCAN_ROWS
  })

  useLayoutEffect(() => {
    if (!scroller) return
    const measure = () => { if (scroller.clientWidth > 0) setWidth(scroller.clientWidth) }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(scroller)
    return () => observer.disconnect()
  }, [scroller])

  useLayoutEffect(() => { virtualizer.measure() }, [virtualizer, rows, columns])

  const acceptAbove80 = async () => {
    if (applyingAllRef.current) return
    applyingAllRef.current = true
    setApplyingAll(true)
    try {
      for (const group of people) {
        const high = group.items.filter(i => Math.round(i.score * 100) > 80)
        if (high.length) await accept(group.person, high)
      }
    } finally {
      applyingAllRef.current = false
      setApplyingAll(false)
    }
  }

  if (!roots.length) return (
    <Box sx={{ p: 3, textAlign: 'center' }}>
      <Typography color="text.secondary">Оберіть папку</Typography>
    </Box>
  )

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Scan state */}
      <Box sx={{ px: 2, py: 1.5, borderBottom: '1px solid', borderColor: 'divider' }}>
        {status && !status.available ? (
          <Alert severity="warning" variant="outlined">
            Розпізнавання облич недоступне: моделі не вдалося завантажити. Деталі — у журналі API.
          </Alert>
        ) : status?.active ? (
          <>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
              <Typography variant="caption" color="text.secondary">
                Пошук облич у бібліотеці…
              </Typography>
              <Typography variant="caption" color="text.secondary" sx={{ ml: 'auto' }}>
                {status.processed.toLocaleString('uk-UA')} / {status.total.toLocaleString('uk-UA')}
              </Typography>
              <Tooltip title="Зупинити (знайдене збережеться)">
                <IconButton size="small" onClick={() => facesApi.stop().then(r => setStatus(r.data)).catch(() => {})}>
                  <StopIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Box>
            <LinearProgress
              variant={status.total > 0 ? 'determinate' : 'indeterminate'}
              value={status.total > 0 ? (status.processed / status.total) * 100 : 0}
              sx={{ height: 4, borderRadius: 2 }}
            />
          </>
        ) : (
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <FaceRetouchingNaturalIcon fontSize="small" sx={{ color: 'text.secondary' }} />
            <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
              {data ? `Облич у бібліотеці: ${data.facesTotal.toLocaleString('uk-UA')}` : 'Обличчя'}
            </Typography>
            <Button size="small" onClick={startScan}>Шукати нові фото</Button>
            <Tooltip title="Оновити підказки">
              <IconButton size="small" onClick={() => load()}>
                <RefreshIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          </Box>
        )}
      </Box>

      {/* Who the model has learned so far */}
      {data && data.learned.length > 0 && (
        <Box sx={{ px: 2, py: 1.5, borderBottom: '1px solid', borderColor: 'divider' }}>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
            Впізнає за вашими тегами: {data.learned.length}
          </Typography>
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.75 }}>
            {data.learned.map(p => (
              <Tooltip key={p.name} title={`Відомих облич: ${p.faces}`}>
                <Chip
                  size="small"
                  variant="outlined"
                  label={p.name}
                  avatar={p.cover ? <FaceAvatar filePath={p.cover.filePath} box={p.cover} size={24} /> : undefined}
                />
              </Tooltip>
            ))}
          </Box>
        </Box>
      )}

      {above80 > 0 && (
        <Box sx={{ px: 2, pt: 1.5, flexShrink: 0 }}>
          <Button size="small" variant="contained" startIcon={<DoneAllIcon />}
            disabled={applyingAll} onClick={acceptAbove80}>
            Застосувати всі понад 80% ({above80})
          </Button>
        </Box>
      )}

      {/* Only the rows on screen are mounted — the same idea as the photo gallery. */}
      <Box ref={setScroller} sx={{ flex: 1, minHeight: 0, overflowY: 'auto', px: 2, pt: 1 }}>
        {!active ? null : loading && !data ? (
          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
            {Array.from({ length: 12 }).map((_, i) => (
              <Skeleton key={i} variant="rounded" width={CARD_SIZE} height={CARD_SIZE} />
            ))}
          </Box>
        ) : data && data.learned.length === 0 ? (
          <Typography color="text.secondary" variant="body2" sx={{ textAlign: 'center', mt: 4, px: 4 }}>
            Позначте людей у полі «Люди» хоча б на кількох фото — бажано там, де людина одна в кадрі.
            Програма навчиться впізнавати їх і підкаже, на яких ще фото вони є.
          </Typography>
        ) : data && people.length === 0 ? (
          <Typography color="text.secondary" variant="body2" sx={{ textAlign: 'center', mt: 4 }}>
            Нових підказок немає
          </Typography>
        ) : (
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
            {virtualizer.getVirtualItems().map(virtualRow => {
              const row = rows[virtualRow.index]
              if (!row) return null
              return (
                <div key={row.key} style={{
                  position: 'absolute', top: 0, left: 0, width: '100%',
                  height: virtualRow.size, transform: `translateY(${virtualRow.start}px)`
                }}>
                  {row.kind === 'header' ? (() => {
                    const cover = data?.learned.find(l => l.name.toLowerCase() === row.group.person.toLowerCase())?.cover
                    const confident = row.group.items.filter(i => i.score >= CONFIDENT_SCORE)
                    return (
                      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, height: HEADER_HEIGHT }}>
                        {cover && <FaceAvatar filePath={cover.filePath} box={cover} size={32} />}
                        <Typography variant="subtitle2" fontWeight={600}>{row.group.person}</Typography>
                        <Typography variant="caption" color="text.secondary">
                          можливо ще на {row.remaining} фото
                        </Typography>
                        <Box sx={{ flex: 1 }} />
                        <Button size="small" color="inherit" onClick={() => reject(row.group.person, row.group.items)}
                          sx={{ color: 'text.secondary' }}>
                          Відхилити всі
                        </Button>
                        <Tooltip title="Лише збіги з високою схожістю (зелена рамка); решту перевірте поштучно">
                          <span>
                            <Button size="small" variant="outlined" startIcon={<DoneAllIcon />}
                              disabled={confident.length === 0}
                              onClick={() => accept(row.group.person, confident)}>
                              Додати на впевнені ({confident.length})
                            </Button>
                          </span>
                        </Tooltip>
                      </Box>
                    )
                  })() : (
                    <Box sx={{ display: 'flex', gap: `${CARD_GAP}px` }}>
                      {row.items.map(item => (
                        <FaceCard key={item.faceId} item={item} person={row.person}
                          onAccept={accept} onReject={reject} />
                      ))}
                    </Box>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </Box>
    </Box>
  )
}
