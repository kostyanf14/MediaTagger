import React, { useState } from 'react'
import { Box, Typography, IconButton, Tooltip } from '@mui/material'
import FolderIcon from '@mui/icons-material/Folder'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import DriveFileRenameOutlineIcon from '@mui/icons-material/DriveFileRenameOutline'
import DriveFileMoveIcon from '@mui/icons-material/DriveFileMove'
import { FolderNode } from '../api/client'

interface Props {
  node: FolderNode
  selectedPath: string
  onSelect: (path: string) => void
  onRename: (node: FolderNode) => void
  onMove: (node: FolderNode) => void
  depth?: number
}

export function FolderTreeNode({ node, selectedPath, onSelect, onRename, onMove, depth = 0 }: Props) {
  const [expanded, setExpanded] = useState(depth === 0)
  const isSelected = selectedPath === node.path
  const hasChildren = node.children.length > 0

  return (
    <Box>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          pl: depth * 1.5 + 0.5,
          pr: 1,
          py: 0.3,
          cursor: 'pointer',
          borderRadius: 1,
          bgcolor: isSelected ? 'primary.main' : 'transparent',
          color: isSelected ? 'primary.contrastText' : 'text.primary',
          '&:hover': { bgcolor: isSelected ? 'primary.dark' : 'action.hover' },
          '&:hover .row-btn': { opacity: 1 },
          transition: 'background-color 0.15s'
        }}
        onClick={() => onSelect(node.path)}
      >
        {hasChildren ? (
          <Box
            component="span"
            onClick={(e) => { e.stopPropagation(); setExpanded(!expanded) }}
            sx={{ display: 'flex', alignItems: 'center', mr: 0.3 }}
          >
            {expanded ? <ExpandMoreIcon fontSize="small" /> : <ChevronRightIcon fontSize="small" />}
          </Box>
        ) : (
          <Box sx={{ width: 20 }} />
        )}
        {expanded && hasChildren
          ? <FolderOpenIcon fontSize="small" sx={{ mr: 0.8, color: isSelected ? 'inherit' : 'warning.main' }} />
          : <FolderIcon fontSize="small" sx={{ mr: 0.8, color: isSelected ? 'inherit' : 'warning.main' }} />
        }
        <Typography
          variant="body2"
          noWrap
          sx={{ fontSize: '0.82rem', fontWeight: isSelected ? 600 : 400 }}
        >
          {node.name}
        </Typography>
        <Tooltip title="Перемістити папку">
          <IconButton
            className="row-btn"
            size="small"
            onClick={e => { e.stopPropagation(); onMove(node) }}
            sx={{
              ml: 'auto', p: 0.25, opacity: isSelected ? 1 : 0,
              color: isSelected ? 'inherit' : 'text.secondary'
            }}
          >
            <DriveFileMoveIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
        <Tooltip title="Перейменувати папку">
          <IconButton
            className="row-btn"
            size="small"
            onClick={e => { e.stopPropagation(); onRename(node) }}
            sx={{ p: 0.25, opacity: isSelected ? 1 : 0, color: isSelected ? 'inherit' : 'text.secondary' }}
          >
            <DriveFileRenameOutlineIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
      </Box>

      {expanded && hasChildren && (
        <Box>
          {node.children.map(child => (
            <FolderTreeNode
              key={child.path}
              node={child}
              selectedPath={selectedPath}
              onSelect={onSelect}
              onRename={onRename}
              onMove={onMove}
              depth={depth + 1}
            />
          ))}
        </Box>
      )}
    </Box>
  )
}
