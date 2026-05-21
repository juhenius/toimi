import { useEffect, useState } from 'react'

interface ScheduleRun {
  id: string
  scheduleName: string
  startedAt: string
  completedAt: string | null
  durationMs: number | null
  response: string | null
  toolCallsJson: string | null
  success: boolean
  error: string | null
}

export function ActivityList() {
  const [open, setOpen] = useState(false)
  const [runs, setRuns] = useState<ScheduleRun[]>([])
  const [loading, setLoading] = useState(false)
  const [expandedId, setExpandedId] = useState<string | null>(null)

  const fetchRuns = async () => {
    setLoading(true)
    try {
      const res = await fetch('/api/activity?limit=20')
      if (res.ok) {
        setRuns(await res.json())
      }
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (open) fetchRuns()
  }, [open])

  return (
    <div className="relative">
      <button
        onClick={() => setOpen(!open)}
        className="text-sm text-zinc-400 hover:text-zinc-200 flex items-center gap-1"
      >
        Activity
        <span className="text-xs">{open ? '▾' : '▸'}</span>
      </button>
      {open && (
        <div className="absolute right-0 top-8 w-96 bg-zinc-800 border border-zinc-700 rounded-lg shadow-lg z-50 overflow-hidden">
          <div className="px-3 py-2 border-b border-zinc-700 flex items-center justify-between">
            <span className="text-sm text-zinc-300 font-medium">Schedule Runs</span>
            <button
              onClick={fetchRuns}
              className="text-xs text-zinc-500 hover:text-zinc-300"
            >
              {loading ? '...' : 'Refresh'}
            </button>
          </div>
          <div className="max-h-96 overflow-y-auto">
            {runs.length === 0 ? (
              <div className="px-3 py-4 text-sm text-zinc-500 text-center">
                {loading ? 'Loading...' : 'No runs yet.'}
              </div>
            ) : (
              runs.map(run => (
                <div key={run.id} className="border-b border-zinc-700 last:border-0">
                  <button
                    onClick={() => setExpandedId(expandedId === run.id ? null : run.id)}
                    className="w-full px-3 py-2 text-left hover:bg-zinc-700"
                  >
                    <div className="flex items-center gap-2 text-sm">
                      <span className={`w-2 h-2 rounded-full ${run.success ? 'bg-green-400' : 'bg-red-400'}`} />
                      <span className="text-zinc-200 font-mono truncate">{run.scheduleName}</span>
                      {run.durationMs != null && (
                        <span className="text-zinc-500 ml-auto text-xs">{formatDuration(run.durationMs)}</span>
                      )}
                      <span className="text-zinc-500 text-xs">{expandedId === run.id ? '▾' : '▸'}</span>
                    </div>
                    <div className="text-xs text-zinc-500 mt-0.5">{formatRelativeTime(run.startedAt)}</div>
                  </button>
                  {expandedId === run.id && (
                    <div className="px-3 pb-2 space-y-2 text-xs">
                      {run.error && (
                        <div>
                          <span className="text-red-400">Error:</span>
                          <pre className="mt-0.5 p-1.5 rounded bg-zinc-950 text-red-300 whitespace-pre-wrap break-all">{run.error}</pre>
                        </div>
                      )}
                      {run.response && (
                        <div>
                          <span className="text-zinc-500">Response:</span>
                          <pre className="mt-0.5 p-1.5 rounded bg-zinc-950 text-zinc-400 whitespace-pre-wrap break-all max-h-48 overflow-y-auto">{run.response}</pre>
                        </div>
                      )}
                      {run.toolCallsJson && (
                        <div>
                          <span className="text-zinc-500">Tool calls:</span>
                          <pre className="mt-0.5 p-1.5 rounded bg-zinc-950 text-zinc-400 whitespace-pre-wrap break-all max-h-32 overflow-y-auto">{formatJson(run.toolCallsJson)}</pre>
                        </div>
                      )}
                    </div>
                  )}
                </div>
              ))
            )}
          </div>
        </div>
      )}
    </div>
  )
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

function formatRelativeTime(iso: string): string {
  const date = new Date(iso)
  const now = new Date()
  const diffMs = now.getTime() - date.getTime()
  const diffMins = Math.floor(diffMs / 60000)

  if (diffMins < 1) return 'just now'
  if (diffMins < 60) return `${diffMins}m ago`
  const diffHours = Math.floor(diffMins / 60)
  if (diffHours < 24) return `${diffHours}h ago`
  const diffDays = Math.floor(diffHours / 24)
  return `${diffDays}d ago`
}

function formatJson(str: string): string {
  try {
    return JSON.stringify(JSON.parse(str), null, 2)
  } catch {
    return str
  }
}
