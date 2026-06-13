import { useMemo, useState } from 'react'
import { useAdminList, adminDelete, adminPost } from './useAdmin'
import { useDebounced } from './useDebounced'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { FetchErrorBanner } from './FetchErrorBanner'
import { EmptyState } from './EmptyState'

interface ReminderItem {
  id: string
  title: string
  description: string | null
  dateTimeUtc: string
  timeZone: string
  recurrenceRule: string | null
  isCompleted: boolean
  updatedAt: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  size: number
  total: number
}

export function RemindersPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const dq = useDebounced(q)
  const { data, error, loading, reload } = useAdminList<PagedResult<ReminderItem>>(
    'muistutin',
    `items?page=${page}&size=20&q=${encodeURIComponent(dq)}`
  )
  const [pendingDelete, setPendingDelete] = useState<ReminderItem | null>(null)

  const columns: Column<ReminderItem>[] = useMemo(
    () => [
      { key: 'title', header: 'Title', render: (r) => r.title },
      {
        key: 'when',
        header: 'When',
        render: (r) => new Date(r.dateTimeUtc).toLocaleString(),
      },
      {
        key: 'recurring',
        header: 'Recurring',
        render: (r) => (r.recurrenceRule ? 'Yes' : 'No'),
      },
      {
        key: 'status',
        header: 'Status',
        render: (r) => (r.isCompleted ? 'Completed' : 'Pending'),
      },
      {
        key: 'actions',
        header: '',
        render: (r) => (
          <div className="flex gap-2">
            {!r.isCompleted && (
              <button
                className="text-green-400"
                onClick={async (e) => {
                  e.stopPropagation()
                  await adminPost('muistutin', `items/${r.id}/complete`)
                  await reload()
                }}
              >
                Complete
              </button>
            )}
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
    [reload]
  )

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Reminders</h1>
      <FetchErrorBanner error={error} />
      <input
        className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-4"
        placeholder="Search title…"
        value={q}
        onChange={(e) => {
          setPage(1)
          setQ(e.target.value)
        }}
      />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && (
        <EmptyState message="No reminders." />
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
        label="reminder"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('muistutin', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
    </div>
  )
}
