import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAdminList, adminDelete } from './useAdmin'
import { useDebounced } from './useDebounced'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { FetchErrorBanner } from './FetchErrorBanner'
import { EmptyState } from './EmptyState'

interface EntityRow {
  id: string
  type: string
  data: string
  tags: string[]
  createdAt: string
  updatedAt: string
}

interface Paged {
  items: EntityRow[]
  page: number
  size: number
  total: number
}

const PAGE_SIZE = 20

export function DataPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const dq = useDebounced(q)
  const { data, error, loading, reload } = useAdminList<Paged>(
    'tietue',
    `items?q=${encodeURIComponent(dq)}&page=${page}&size=${PAGE_SIZE}`
  )
  const [pendingDelete, setPendingDelete] = useState<EntityRow | null>(null)

  const columns: Column<EntityRow>[] = useMemo(
    () => [
      {
        key: 'type',
        header: 'Type',
        render: (r) => (
          <Link to={`/admin/data/${r.id}`} className="text-blue-400 hover:underline" onClick={(e) => e.stopPropagation()}>
            {r.type}
          </Link>
        ),
      },
      {
        key: 'data',
        header: 'Data',
        render: (r) => (
          <span className="text-zinc-400 font-mono text-xs">
            {r.data.length > 80 ? r.data.slice(0, 80) + '…' : r.data}
          </span>
        ),
      },
      {
        key: 'tags',
        header: 'Tags',
        render: (r) => r.tags.join(', '),
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
          <button
            className="text-red-400"
            onClick={(e) => {
              e.stopPropagation()
              setPendingDelete(r)
            }}
          >
            Delete
          </button>
        ),
      },
    ],
    []
  )

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Data</h1>
      <FetchErrorBanner error={error} />
      <input
        className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-4"
        placeholder="Filter by type…"
        value={q}
        onChange={(e) => {
          setPage(1)
          setQ(e.target.value)
        }}
      />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {!loading && data && data.items.length === 0 && (
        <EmptyState message="No entities found." />
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
        label={pendingDelete ? `${pendingDelete.type} (${pendingDelete.id})` : ''}
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('tietue', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
    </div>
  )
}
