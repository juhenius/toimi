import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useAdminList, adminDelete } from './useAdmin'
import { ConfirmDelete } from './ConfirmDelete'
import { FetchErrorBanner } from './FetchErrorBanner'
import { EmptyState } from './EmptyState'

interface EntityDetail {
  id: string
  type: string
  data: string
  tags: string[]
  createdAt: string
  updatedAt: string
}

interface TriggerRow {
  id: string
  schedule: string
  handlerKind: string
  handlerConfig: string | null
  enabled: boolean
  nextFireAt: string | null
  lastFiredAt: string | null
}

interface EventRow {
  id: string
  occurrenceUtc: string
  kind: string
  status: string
  result: string | null
  createdAt: string
}

export function EntityDetailPage() {
  const { id } = useParams()
  const navigate = useNavigate()
  const { data: item, error, loading } = useAdminList<EntityDetail>('tietue', 'items/' + (id ?? ''))
  const { data: triggers } = useAdminList<TriggerRow[]>('tietue', 'items/' + (id ?? '') + '/triggers')
  const { data: events } = useAdminList<EventRow[]>('tietue', 'items/' + (id ?? '') + '/events')
  const [showDelete, setShowDelete] = useState(false)

  if (!id) return <div className="text-zinc-500 text-sm p-6">Invalid URL.</div>

  let prettyData = ''
  if (item) {
    try {
      prettyData = JSON.stringify(JSON.parse(item.data), null, 2)
    } catch {
      prettyData = item.data
    }
  }

  return (
    <div>
      <Link to="/admin/data" className="text-blue-400 hover:underline text-sm mb-4 inline-block">
        ← Back
      </Link>
      <FetchErrorBanner error={error?.status !== 404 ? error : null} />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {error?.status === 404 && (
        <div className="text-zinc-400 text-sm">Entity not found.</div>
      )}
      {item && (
        <>
          <h1 className="text-2xl mb-4 text-zinc-100">{item.type}</h1>
          <div className="text-sm text-zinc-400 mb-4 flex gap-4">
            <span>{item.tags.join(', ')}</span>
            <span>Created: {new Date(item.createdAt).toLocaleString()}</span>
            <span>Updated: {new Date(item.updatedAt).toLocaleString()}</span>
          </div>

          <h2 className="text-lg mt-6 mb-2 text-zinc-200">Data</h2>
          <pre className="overflow-auto bg-zinc-800 text-zinc-300 font-mono text-xs p-3 rounded">
            {prettyData}
          </pre>

          <h2 className="text-lg mt-6 mb-2 text-zinc-200">Triggers</h2>
          {!triggers || triggers.length === 0 ? (
            <EmptyState message="No triggers." />
          ) : (
            <table className="w-full text-sm border-collapse">
              <thead>
                <tr>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Handler kind</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Schedule</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Enabled</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Next fire</th>
                </tr>
              </thead>
              <tbody>
                {triggers.map((t) => (
                  <tr key={t.id} className="border-b border-zinc-800">
                    <td className="py-2 pr-4">{t.handlerKind}</td>
                    <td className="py-2 pr-4 font-mono text-xs">{t.schedule}</td>
                    <td className="py-2 pr-4">{t.enabled ? '✓' : '✗'}</td>
                    <td className="py-2 pr-4">
                      {t.nextFireAt ? new Date(t.nextFireAt).toLocaleString() : '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          <h2 className="text-lg mt-6 mb-2 text-zinc-200">Events</h2>
          {!events || events.length === 0 ? (
            <EmptyState message="No events." />
          ) : (
            <table className="w-full text-sm border-collapse">
              <thead>
                <tr>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Occurrence</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Kind</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Status</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Result</th>
                  <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Created</th>
                </tr>
              </thead>
              <tbody>
                {events.map((e) => (
                  <tr key={e.id} className="border-b border-zinc-800">
                    <td className="py-2 pr-4">{new Date(e.occurrenceUtc).toLocaleString()}</td>
                    <td className="py-2 pr-4">{e.kind}</td>
                    <td className="py-2 pr-4">{e.status}</td>
                    <td className="py-2 pr-4">
                      {e.result == null
                        ? '—'
                        : e.result.length > 60
                          ? e.result.slice(0, 60) + '…'
                          : e.result}
                    </td>
                    <td className="py-2 pr-4">{new Date(e.createdAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}

          <button
            className="mt-6 px-3 py-1 rounded bg-red-700 text-white text-sm"
            onClick={() => setShowDelete(true)}
          >
            Delete
          </button>
          <ConfirmDelete
            open={showDelete}
            label={`${item.type} (${item.id})`}
            onCancel={() => setShowDelete(false)}
            onConfirm={async () => {
              const ok = await adminDelete('tietue', 'items/' + id!)
              if (ok) navigate('/admin/data')
              else setShowDelete(false)
            }}
          />
        </>
      )}
    </div>
  )
}
