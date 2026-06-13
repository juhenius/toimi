import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAdminList, adminDelete, adminPost } from './useAdmin'
import { useDebounced } from './useDebounced'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { FetchErrorBanner } from './FetchErrorBanner'
import { EmptyState } from './EmptyState'

interface ScheduleItem {
  id: string
  name: string
  cronExpression: string | null
  runAt: string | null
  prompt: string
  enabled: boolean
  lastRunAt: string | null
  updatedAt: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  size: number
  total: number
}

export function SchedulesPage() {
  const nav = useNavigate()
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const dq = useDebounced(q)
  const { data, error, loading, reload } = useAdminList<PagedResult<ScheduleItem>>(
    'ajastin',
    `items?page=${page}&size=20&q=${encodeURIComponent(dq)}`,
  )
  const [pendingDelete, setPendingDelete] = useState<ScheduleItem | null>(null)

  const columns: Column<ScheduleItem>[] = useMemo(
    () => [
      { key: 'name', header: 'Name', render: (r) => r.name },
      {
        key: 'trigger',
        header: 'Trigger',
        render: (r) =>
          r.cronExpression ?? (r.runAt ? `at ${new Date(r.runAt).toLocaleString()}` : '—'),
      },
      { key: 'enabled', header: 'Enabled', render: (r) => (r.enabled ? 'Yes' : 'No') },
      {
        key: 'last',
        header: 'Last run',
        render: (r) => (r.lastRunAt ? new Date(r.lastRunAt).toLocaleString() : 'Never'),
      },
      {
        key: 'actions',
        header: '',
        render: (r) => (
          <div className="flex gap-2">
            <button
              className="text-blue-400"
              onClick={async (e) => {
                e.stopPropagation()
                await adminPost('ajastin', `items/${r.id}/run-now`)
                await reload()
              }}
            >
              Run now
            </button>
            <button
              className="text-yellow-400"
              onClick={async (e) => {
                e.stopPropagation()
                await adminPost('ajastin', `items/${r.id}/${r.enabled ? 'disable' : 'enable'}`)
                await reload()
              }}
            >
              {r.enabled ? 'Disable' : 'Enable'}
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
    [reload],
  )

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Schedules</h1>
      <FetchErrorBanner error={error} />
      <input
        className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-4"
        placeholder="Search name…"
        value={q}
        onChange={(e) => {
          setPage(1)
          setQ(e.target.value)
        }}
      />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && <EmptyState message="No schedules." />}
      {data && data.items.length > 0 && (
        <>
          <DataTable rows={data.items} columns={columns} onRowClick={(r) => nav(`/admin/ajastin/${r.id}`)} />
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
        label="schedule"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('ajastin', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
    </div>
  )
}
