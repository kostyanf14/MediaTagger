import React, { useState } from 'react'
import {
  Box, TextField, Autocomplete, Chip, Button,
  Popover, Stack, Typography, IconButton
} from '@mui/material'
import SearchIcon from '@mui/icons-material/Search'
import TuneIcon from '@mui/icons-material/Tune'
import ClearIcon from '@mui/icons-material/Clear'
import { SearchRequest } from '../api/client'

interface Props {
  allTags: { people: string[]; groups: string[]; albums: string[] }
  roots: string[]
  onSearch: (req: SearchRequest | null) => void
}

export function SearchBar({ allTags, roots, onSearch }: Props) {
  const [query, setQuery] = useState('')
  const [filterAnchor, setFilterAnchor] = useState<null | HTMLElement>(null)
  const [filterPeople, setFilterPeople] = useState<string[]>([])
  const [filterAlbums, setFilterAlbums] = useState<string[]>([])
  const [filterGroups, setFilterGroups] = useState<string[]>([])

  const hasFilters = filterPeople.length > 0 || filterAlbums.length > 0 || filterGroups.length > 0

  const handleSearch = () => {
    if (!query && !hasFilters) {
      onSearch(null)
      return
    }
    onSearch({
      query: query || undefined,
      people: filterPeople.length ? filterPeople : undefined,
      albums: filterAlbums.length ? filterAlbums : undefined,
      groups: filterGroups.length ? filterGroups : undefined,
      rootPath: roots[0] ?? '',
      rootPaths: roots
    })
  }

  const handleClear = () => {
    setQuery('')
    setFilterPeople([])
    setFilterAlbums([])
    setFilterGroups([])
    onSearch(null)
  }

  return (
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', p: 1.5, borderBottom: '1px solid', borderColor: 'divider' }}>
      <TextField
        size="small"
        placeholder="Пошук по тегах, іменах, місцях..."
        value={query}
        onChange={e => setQuery(e.target.value)}
        onKeyDown={e => e.key === 'Enter' && handleSearch()}
        sx={{ flex: 1 }}
        InputProps={{
          startAdornment: <SearchIcon fontSize="small" sx={{ mr: 0.5, color: 'text.secondary' }} />
        }}
      />

      <IconButton
        size="small"
        onClick={e => setFilterAnchor(e.currentTarget)}
        color={hasFilters ? 'primary' : 'default'}
      >
        <TuneIcon fontSize="small" />
        {hasFilters && (
          <Box sx={{
            position: 'absolute', top: 2, right: 2,
            width: 8, height: 8, borderRadius: '50%', bgcolor: 'primary.main'
          }} />
        )}
      </IconButton>

      <Button variant="contained" size="small" onClick={handleSearch}>Знайти</Button>

      {(query || hasFilters) && (
        <IconButton size="small" onClick={handleClear}>
          <ClearIcon fontSize="small" />
        </IconButton>
      )}

      {/* Filter popover */}
      <Popover
        open={!!filterAnchor}
        anchorEl={filterAnchor}
        onClose={() => setFilterAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
      >
        <Stack spacing={2} sx={{ p: 2, width: 300 }}>
          <Typography variant="subtitle2" fontWeight={600}>Фільтри</Typography>

          <Autocomplete
            multiple options={allTags.people} value={filterPeople}
            onChange={(_, v) => setFilterPeople(v)}
            renderTags={(val, getProps) =>
              val.map((opt, i) => <Chip label={opt} size="small" {...getProps({ index: i })} key={i} />)
            }
            renderInput={(params) => <TextField {...params} label="Люди" size="small" />}
          />

          <Autocomplete
            multiple options={allTags.albums} value={filterAlbums}
            onChange={(_, v) => setFilterAlbums(v)}
            renderTags={(val, getProps) =>
              val.map((opt, i) => <Chip label={opt} size="small" color="info" {...getProps({ index: i })} key={i} />)
            }
            renderInput={(params) => <TextField {...params} label="Альбоми" size="small" />}
          />

          <Autocomplete
            multiple options={allTags.groups} value={filterGroups}
            onChange={(_, v) => setFilterGroups(v)}
            renderTags={(val, getProps) =>
              val.map((opt, i) => <Chip label={opt} size="small" color="secondary" {...getProps({ index: i })} key={i} />)
            }
            renderInput={(params) => <TextField {...params} label="Групи" size="small" />}
          />

          <Button variant="contained" onClick={() => { handleSearch(); setFilterAnchor(null) }}>
            Застосувати
          </Button>
        </Stack>
      </Popover>
    </Box>
  )
}
