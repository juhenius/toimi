import { useEffect, useState } from 'react'
import { EmptyState } from './EmptyState'

interface WebUsageRow { date: string; promptTokens: number; completionTokens: number; costUsd: number }
interface AgentUsageRow { date: string; promptTokens: number; completionTokens: number; costUsd: number }

export function UsagePage() {
  const [web, setWeb] = useState<WebUsageRow[] | null>(null)
  const [agent, setAgent] = useState<AgentUsageRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [webResp, agentResp] = await Promise.all([
          fetch('/api/admin/usage'),
          fetch('/api/admin/tietue/usage'),
        ])
        if (cancelled) return
        if (webResp.ok) setWeb(await webResp.json() as WebUsageRow[])
        if (agentResp.ok) setAgent(await agentResp.json() as AgentUsageRow[])
        if (!webResp.ok && !agentResp.ok) setError('Failed to load usage data')
      } catch {
        if (!cancelled) setError('Failed to load usage data')
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [])

  const days = [...new Set([...(web ?? []).map(r => r.date), ...(agent ?? []).map(r => r.date)])].sort().reverse()
  const webBy = new Map((web ?? []).map(r => [r.date, r]))
  const agentBy = new Map((agent ?? []).map(r => [r.date, r]))

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Usage (last 30 days)</h1>
      {error && (
        <div className="bg-red-900/40 border border-red-700 text-red-200 p-3 rounded mb-4 text-sm">
          {error}
        </div>
      )}
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {!loading && !error && days.length === 0 && <EmptyState message="No usage recorded yet." />}
      {days.length > 0 && (
        <table className="w-full text-left text-sm">
          <thead className="text-zinc-400">
            <tr>
              <th className="px-3 py-2">Day</th>
              <th className="px-3 py-2">Web tokens (in / out)</th>
              <th className="px-3 py-2">Agent tokens (in / out)</th>
              <th className="px-3 py-2">Est. cost</th>
            </tr>
          </thead>
          <tbody>
            {days.map(d => {
              const w = webBy.get(d)
              const a = agentBy.get(d)
              return (
                <tr key={d}>
                  <td className="px-3 py-2 border-t border-zinc-800 text-zinc-100">{d}</td>
                  <td className="px-3 py-2 border-t border-zinc-800">
                    {w ? `${w.promptTokens.toLocaleString()} / ${w.completionTokens.toLocaleString()}` : '—'}
                  </td>
                  <td className="px-3 py-2 border-t border-zinc-800">
                    {a ? `${a.promptTokens.toLocaleString()} / ${a.completionTokens.toLocaleString()}` : '—'}
                  </td>
                  <td className="px-3 py-2 border-t border-zinc-800">
                    {w || a ? `$${((w?.costUsd ?? 0) + (a?.costUsd ?? 0)).toFixed(2)}` : '—'}
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </div>
  )
}
