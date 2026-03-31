import { useState } from 'react'
import type { ConversationSummary } from '../hooks/useToimi.ts'

interface ConversationListProps {
  conversations: ConversationSummary[]
  currentConversationId: string | null
  onLoad: (id: string) => void
  onNew: () => void
}

export function ConversationList({ conversations, currentConversationId, onLoad, onNew }: ConversationListProps) {
  const [open, setOpen] = useState(false)

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(!open)}
        className="text-sm text-zinc-400 hover:text-zinc-200 flex items-center gap-1"
      >
        Conversations
        <span className="text-xs">{open ? '▾' : '▸'}</span>
      </button>
      {open && (
        <div className="absolute right-0 top-8 w-72 bg-zinc-800 border border-zinc-700 rounded-lg shadow-lg z-50 overflow-hidden">
          <button
            onClick={() => { onNew(); setOpen(false) }}
            className="w-full px-3 py-2 text-left text-sm text-blue-400 hover:bg-zinc-700 border-b border-zinc-700"
          >
            + New conversation
          </button>
          <div className="max-h-64 overflow-y-auto">
            {conversations.length === 0 ? (
              <div className="px-3 py-2 text-sm text-zinc-500">No conversations yet.</div>
            ) : (
              conversations.map(c => (
                <button
                  key={c.id}
                  onClick={() => { onLoad(c.id); setOpen(false) }}
                  className={`w-full px-3 py-2 text-left text-sm hover:bg-zinc-700 ${
                    c.id === currentConversationId ? 'bg-zinc-700 text-zinc-100' : 'text-zinc-300'
                  }`}
                >
                  <div className="truncate">{c.title || 'Untitled'}</div>
                  <div className="text-xs text-zinc-500">{formatRelativeTime(c.lastMessageAt)}</div>
                </button>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  )
}

function formatRelativeTime(iso: string): string {
  const date = new Date(iso)
  const now = new Date()
  const diffMs = now.getTime() - date.getTime()
  const diffMins = Math.floor(diffMs / 60000)

  if (diffMins < 1) return 'just now'
  if (diffMins < 60) return `${diffMins}m ago`
  const diffHours = Math.floor(diffMins / 60)
  if (diffHours < 24) return `${diffHours}h ago`
  const diffDays = Math.floor(diffHours / 24)
  return `${diffDays}d ago`
}
