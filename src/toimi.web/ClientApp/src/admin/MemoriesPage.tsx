import { useEffect, useMemo, useState } from 'react'
import { useAdminList, adminDelete, adminPut } from './useAdmin'
import { useDebounced } from './useDebounced'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { FetchErrorBanner } from './FetchErrorBanner'
import { StaleConflictModal } from './StaleConflictModal'
import { EmptyState } from './EmptyState'

interface MemoryItem {
  id: string
  content: string
  category: string | null
  tags: string[]
  source: string
  confirmed: boolean
  expiresAt: string | null
  createdAt: string
  updatedAt: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  size: number
  total: number
}

export function MemoriesPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const dq = useDebounced(q)
  const { data, error, loading, reload } = useAdminList<PagedResult<MemoryItem>>(
    'muistio',
    `items?page=${page}&size=20&q=${encodeURIComponent(dq)}`
  )
  const [pendingDelete, setPendingDelete] = useState<MemoryItem | null>(null)
  const [editing, setEditing] = useState<MemoryItem | null>(null)

  const columns: Column<MemoryItem>[] = useMemo(
    () => [
      { key: 'content', header: 'Content', render: (r) => r.content },
      { key: 'source', header: 'Source', render: (r) => r.source },
      {
        key: 'confirmed',
        header: 'Confirmed',
        render: (r) => (r.confirmed ? 'Yes' : 'No'),
      },
      {
        key: 'updated',
        header: 'Updated',
        render: (r) => new Date(r.updatedAt).toLocaleString(),
      },
      {
        key: 'actions',
        header: '',
        render: (r) => (
          <div className="flex gap-2">
            <button
              className="text-blue-400"
              onClick={(e) => {
                e.stopPropagation()
                setEditing(r)
              }}
            >
              Edit
            </button>
            <button
              className="text-red-400"
              onClick={(e) => {
                e.stopPropagation()
                setPendingDelete(r)
              }}
            >
              Delete
            </button>
          </div>
        ),
      },
    ],
    []
  )

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Memories</h1>
      <FetchErrorBanner error={error} />
      <input
        className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-4"
        placeholder="Search content…"
        value={q}
        onChange={(e) => {
          setPage(1)
          setQ(e.target.value)
        }}
      />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && (
        <EmptyState message="No memories yet." />
      )}
      {data && data.items.length > 0 && (
        <>
          <DataTable rows={data.items} columns={columns} />
          <div className="mt-3 text-sm text-zinc-400 flex gap-3 items-center">
            <button
              disabled={page === 1}
              onClick={() => setPage((p) => p - 1)}
              className="disabled:opacity-30"
            >
              ← Prev
            </button>
            <span>
              Page {data.page} ({data.total} total)
            </span>
            <button
              disabled={data.page * data.size >= data.total}
              onClick={() => setPage((p) => p + 1)}
              className="disabled:opacity-30"
            >
              Next →
            </button>
          </div>
        </>
      )}
      <ConfirmDelete
        open={!!pendingDelete}
        label="memory"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('muistio', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
      <EditMemoryDialog
        item={editing}
        onClose={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null)
          await reload()
        }}
      />
    </div>
  )
}

function EditMemoryDialog({
  item,
  onClose,
  onSaved,
}: {
  item: MemoryItem | null
  onClose: () => void
  onSaved: () => void
}) {
  const [content, setContent] = useState('')
  const [stale, setStale] = useState(false)
  useEffect(() => {
    if (item) setContent(item.content)
  }, [item])
  if (!item) return null
  return (
    <>
      <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
        <div className="bg-zinc-900 border border-zinc-700 rounded p-6 w-[32rem]">
          <h3 className="text-lg mb-3 text-zinc-100">Edit memory</h3>
          <textarea
            className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 h-32"
            defaultValue={item.content}
            onChange={(e) => setContent(e.target.value)}
          />
          <div className="flex justify-end gap-2 mt-3">
            <button className="px-3 py-1 rounded bg-zinc-700" onClick={onClose}>
              Cancel
            </button>
            <button
              className="px-3 py-1 rounded bg-blue-600"
              onClick={async () => {
                const result = await adminPut<{ content: string }, MemoryItem>(
                  'muistio',
                  `items/${item.id}`,
                  { content: content || item.content },
                  item.updatedAt
                )
                if (result.ok) onSaved()
                else if (result.status === 409) setStale(true)
                else alert(`Update failed (HTTP ${result.status})`)
              }}
            >
              Save
            </button>
          </div>
        </div>
      </div>
      <StaleConflictModal
        open={stale}
        onDismiss={() => setStale(false)}
        onReload={() => {
          setStale(false)
          onSaved()
        }}
      />
    </>
  )
}
