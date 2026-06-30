import { useState } from 'react'
import { useAdminSummary } from './useAdminSummary'
import { useDebounced } from './useDebounced'
import { ErrorBanner } from './ErrorBanner'
import { EmptyState } from './EmptyState'

export function DashboardPage() {
  const [q, setQ] = useState('')
  const dq = useDebounced(q)
  const { data, loading } = useAdminSummary(dq)

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Dashboard</h1>
      <input
        className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-4"
        placeholder="Search across records…"
        value={q} onChange={e => setQ(e.target.value)}
      />
      <ErrorBanner errors={data?.errors ?? []} />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {!loading && data && data.items.length === 0 && <EmptyState message="No matches." />}
      <ul className="divide-y divide-zinc-800">
        {data?.items.map(item => (
          <li key={`${item.kind}:${item.id}`} className="py-3">
            <div className="block hover:bg-zinc-800 -mx-3 px-3 py-1 rounded">
              <div className="flex justify-between text-sm">
                <span className="font-medium">{item.title}</span>
                <span className="text-zinc-500">{item.kind}</span>
              </div>
              {item.subtitle && <div className="text-xs text-zinc-500">{item.subtitle}</div>}
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}
