import { useParams, Link } from 'react-router-dom'
import { useAdminList } from './useAdmin'
import { DataTable, type Column } from './DataTable'
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

interface RunItem {
  id: string
  startedAt: string
  completedAt: string | null
  success: boolean
  response: string | null
  error: string | null
}

export function ScheduleDetailPage() {
  const { id } = useParams<{ id: string }>()
  const { data: schedule, loading } = useAdminList<ScheduleItem>('ajastin', `items/${id}`)
  const { data: runs } = useAdminList<RunItem[]>('ajastin', `items/${id}/runs?limit=20`)

  if (loading) return <div className="text-zinc-500 text-sm">Loading…</div>
  if (!schedule) return <EmptyState message="Schedule not found." />

  const cols: Column<RunItem>[] = [
    { key: 'when', header: 'Started', render: (r) => new Date(r.startedAt).toLocaleString() },
    { key: 'ok', header: 'Status', render: (r) => (r.success ? 'Success' : 'Failed') },
    {
      key: 'preview',
      header: 'Response',
      render: (r) => (r.response ?? r.error ?? '').slice(0, 100),
    },
  ]

  return (
    <div>
      <Link to="/admin/ajastin" className="text-sm text-zinc-400">
        ← Schedules
      </Link>
      <h1 className="text-2xl mt-2 mb-4 text-zinc-100">{schedule.name}</h1>
      <dl className="grid grid-cols-2 gap-2 text-sm mb-6">
        <dt className="text-zinc-400">Trigger</dt>
        <dd>
          {schedule.cronExpression ??
            (schedule.runAt ? `at ${new Date(schedule.runAt).toLocaleString()}` : '—')}
        </dd>
        <dt className="text-zinc-400">Enabled</dt>
        <dd>{schedule.enabled ? 'Yes' : 'No'}</dd>
        <dt className="text-zinc-400">Last run</dt>
        <dd>{schedule.lastRunAt ? new Date(schedule.lastRunAt).toLocaleString() : 'Never'}</dd>
        <dt className="text-zinc-400">Prompt</dt>
        <dd className="whitespace-pre-wrap">{schedule.prompt}</dd>
      </dl>
      <h2 className="text-xl mb-2 text-zinc-100">Recent runs</h2>
      {!runs?.length ? <EmptyState message="No runs yet." /> : <DataTable rows={runs} columns={cols} />}
    </div>
  )
}
