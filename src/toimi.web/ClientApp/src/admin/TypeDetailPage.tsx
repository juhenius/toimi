import { Link, useParams } from 'react-router-dom'
import { useAdminList } from './useAdmin'
import { FetchErrorBanner } from './FetchErrorBanner'

interface TypeDetail {
  name: string
  jsonSchema: string
  behaviors: string | null
  defaultTriggers: string | null
  createdAt: string
  updatedAt: string
}

function prettyJson(raw: string | null): string {
  if (!raw) return 'none'
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}

export function TypeDetailPage() {
  const { name } = useParams()
  const { data: item, error, loading } = useAdminList<TypeDetail>(
    'tietue',
    'types/' + encodeURIComponent(name ?? '')
  )

  if (!name) return <div className="text-zinc-500 text-sm p-6">Invalid URL.</div>

  return (
    <div>
      <Link to="/admin/types" className="text-blue-400 hover:underline text-sm mb-4 inline-block">
        ← Back
      </Link>
      <FetchErrorBanner error={error?.status !== 404 ? error : null} />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {error?.status === 404 && (
        <div className="text-zinc-400 text-sm">Type not found.</div>
      )}
      {item && (
        <>
          <h1 className="text-2xl mb-4 text-zinc-100 font-mono">{item.name}</h1>
          <div className="text-sm text-zinc-400 mb-4 flex gap-4">
            <span>Created: {new Date(item.createdAt).toLocaleString()}</span>
            <span>Updated: {new Date(item.updatedAt).toLocaleString()}</span>
          </div>

          <h2 className="text-lg mt-6 mb-2 text-zinc-200">Schema</h2>
          <pre className="overflow-auto bg-zinc-800 text-zinc-300 font-mono text-xs p-3 rounded">
            {prettyJson(item.jsonSchema)}
          </pre>

          <h2 className="text-lg mt-6 mb-2 text-zinc-200">Behaviors</h2>
          <pre className="overflow-auto bg-zinc-800 text-zinc-300 font-mono text-xs p-3 rounded">
            {prettyJson(item.behaviors)}
          </pre>

          <h2 className="text-lg mt-6 mb-2 text-zinc-200">Default triggers</h2>
          <pre className="overflow-auto bg-zinc-800 text-zinc-300 font-mono text-xs p-3 rounded">
            {prettyJson(item.defaultTriggers)}
          </pre>
        </>
      )}
    </div>
  )
}
