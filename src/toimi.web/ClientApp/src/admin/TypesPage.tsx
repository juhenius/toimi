import { Link } from 'react-router-dom'
import { useAdminList } from './useAdmin'
import { FetchErrorBanner } from './FetchErrorBanner'
import { EmptyState } from './EmptyState'

interface BehaviorEntry {
  behavior: string
  config?: unknown
}

interface TypeRow {
  name: string
  jsonSchema: string
  behaviors: string | null
  defaultTriggers: string | null
  createdAt: string
  updatedAt: string
}

function parseBehaviorNames(behaviors: string | null): string {
  if (!behaviors) return '—'
  try {
    const arr = JSON.parse(behaviors) as BehaviorEntry[]
    if (!Array.isArray(arr) || arr.length === 0) return '—'
    return arr.map((b) => b.behavior).join(', ')
  } catch {
    return '—'
  }
}

function hasDefaultTriggers(defaultTriggers: string | null): string {
  if (!defaultTriggers) return '—'
  try {
    const arr = JSON.parse(defaultTriggers) as unknown[]
    return Array.isArray(arr) && arr.length > 0 ? 'yes' : '—'
  } catch {
    return '—'
  }
}

export function TypesPage() {
  const { data, error, loading } = useAdminList<TypeRow[]>('tietue', 'types')

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Types</h1>
      <FetchErrorBanner error={error} />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {!loading && data && data.length === 0 && (
        <EmptyState message="No types defined." />
      )}
      {data && data.length > 0 && (
        <table className="w-full text-sm border-collapse">
          <thead>
            <tr>
              <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1 pr-4">Name</th>
              <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1 pr-4">Behaviors</th>
              <th className="text-left text-zinc-400 border-b border-zinc-700 pb-1">Default triggers</th>
            </tr>
          </thead>
          <tbody>
            {data.map((row) => (
              <tr key={row.name} className="border-b border-zinc-800">
                <td className="py-2 pr-4">
                  <Link
                    to={`/admin/types/${encodeURIComponent(row.name)}`}
                    className="font-mono text-blue-400 hover:underline"
                  >
                    {row.name}
                  </Link>
                </td>
                <td className="py-2 pr-4 text-zinc-400">{parseBehaviorNames(row.behaviors)}</td>
                <td className="py-2 text-zinc-400">{hasDefaultTriggers(row.defaultTriggers)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
