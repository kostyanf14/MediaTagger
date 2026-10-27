import React, { useEffect, useState } from 'react'
import {
  Box, Typography, IconButton, Tooltip, Popover, Autocomplete, TextField, CircularProgress
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import CloseIcon from '@mui/icons-material/Close'
import { facesApi, FileFace, MediaFile } from '../api/client'
import { FaceAvatar } from './FaceAvatar'

const AVATAR_SIZE = 48

interface Props {
  roots: string[]
  file: MediaFile
  /** The people currently in the editor, which may be ahead of what was saved. */
  people: string[]
  /** Everyone tagged anywhere, for naming an unknown face. */
  allPeople: string[]
  /** Adds a person to the file's tags; the editor saves it like any other chip. */
  onAddPerson: (person: string) => void
}

/**
 * The faces found in one photo: who each one is when known, who it might be when
 * the model has a guess, and a way to name the rest — which also teaches the model.
 */
export function PhotoFaces({ roots, file, people, allPeople, onAddPerson }: Props) {
  const [faces, setFaces] = useState<FileFace[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [naming, setNaming] = useState<{ face: FileFace; anchor: HTMLElement } | null>(null)
  // Tags changing can settle or reopen faces, so they are part of what is asked for.
  const peopleKey = people.join('\n')

  useEffect(() => { setFaces(null) }, [file.filePath])

  useEffect(() => {
    if (!roots.length) return
    let cancelled = false
    setLoading(true)
    facesApi.file(roots, file.filePath)
      .then(r => { if (!cancelled) setFaces(r.data.faces) })
      .catch(() => { if (!cancelled) setFaces([]) })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [roots, file.filePath, peopleKey])

  const update = (faceId: number, patch: Partial<FileFace>) =>
    setFaces(prev => prev?.map(f => (f.faceId === faceId ? { ...f, ...patch } : f)) ?? prev)

  const assign = async (face: FileFace, raw: string) => {
    const person = raw.trim()
    if (!person) return
    setNaming(null)
    update(face.faceId, { person, suggestion: undefined })
    try {
      // The label goes to the face store only; the tag itself is written by the editor.
      await facesApi.accept([{ faceId: face.faceId, filePath: file.filePath, person }], false)
    } catch { /* the tag below still lands; the model just learns it from the tags instead */ }
    if (!people.some(p => p.toLowerCase() === person.toLowerCase())) onAddPerson(person)
  }

  const reject = (face: FileFace) => {
    if (!face.suggestion) return
    update(face.faceId, { suggestion: undefined })
    facesApi.reject([{ faceId: face.faceId, filePath: file.filePath, person: face.suggestion }]).catch(() => {})
  }

  if (faces === null) {
    return loading ? (
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <CircularProgress size={12} />
        <Typography variant="caption" color="text.secondary">Пошук облич…</Typography>
      </Box>
    ) : null
  }
  if (faces.length === 0) return null

  return (
    <Box>
      <Typography variant="caption" color="text.secondary">
        {file.type === 'video' ? 'Обличчя у відео' : 'Обличчя на фото'}
      </Typography>
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mt: 0.5 }}>
        {faces.map(face => (
          <Box key={face.faceId} sx={{ width: 68, textAlign: 'center' }}>
            <Tooltip title={face.person ? 'Змінити' : 'Вказати, хто це'}>
              <Box
                onClick={e => setNaming({ face, anchor: e.currentTarget })}
                sx={{ display: 'inline-block', cursor: 'pointer' }}
              >
                <FaceAvatar filePath={file.filePath} box={face} size={AVATAR_SIZE} sx={{
                  border: '2px solid',
                  borderStyle: face.suggestion ? 'dashed' : 'solid',
                  borderColor: face.person ? 'primary.main' : face.suggestion ? 'warning.main' : 'divider'
                }} />
              </Box>
            </Tooltip>

            {face.person ? (
              <Typography variant="caption" noWrap display="block" title={face.person}>
                {face.person}
              </Typography>
            ) : face.suggestion ? (
              <>
                <Typography variant="caption" noWrap display="block" color="warning.main"
                  title={`${face.suggestion}? Схожість ${Math.round(face.score * 100)}%`}>
                  {face.suggestion}?
                </Typography>
                <Box sx={{ display: 'flex', justifyContent: 'center', gap: 0.25 }}>
                  <Tooltip title={`Додати ${face.suggestion}`}>
                    <IconButton size="small" sx={{ p: 0.25 }} onClick={() => assign(face, face.suggestion!)}>
                      <AddIcon sx={{ fontSize: 16, color: 'success.main' }} />
                    </IconButton>
                  </Tooltip>
                  <Tooltip title="Ні, це не вона/він">
                    <IconButton size="small" sx={{ p: 0.25 }} onClick={() => reject(face)}>
                      <CloseIcon sx={{ fontSize: 16 }} />
                    </IconButton>
                  </Tooltip>
                </Box>
              </>
            ) : (
              <Typography variant="caption" color="text.disabled" display="block">Хто це?</Typography>
            )}
          </Box>
        ))}
      </Box>

      <Popover
        open={!!naming}
        anchorEl={naming?.anchor}
        onClose={() => setNaming(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        transformOrigin={{ vertical: 'top', horizontal: 'center' }}
      >
        {naming && (
          <Box sx={{ p: 1.5, width: 260 }}>
            <Autocomplete
              freeSolo
              openOnFocus
              options={allPeople}
              defaultValue={naming.face.person ?? naming.face.suggestion ?? null}
              onChange={(_, value) => { if (typeof value === 'string') assign(naming.face, value) }}
              renderInput={params => (
                <TextField {...params} autoFocus size="small" label="Хто це?" placeholder="Ім'я" />
              )}
            />
          </Box>
        )}
      </Popover>
    </Box>
  )
}
