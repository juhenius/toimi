import { useEffect, useState } from 'react'

export interface AdminSummaryDto {
  id: string
  kind: 'memory' | 'reminder' | 'schedule' | 'skill'
  title: string
  subtitle: string | null
  createdAt: string
  updatedAt: string
}

export interface AggregatedSummary {
  items: AdminSummaryDto[]
  errors: { tool: string; message: string }[]
}

export function useAdminSummary(query: string) {
  const [data, setData] = useState<AggregatedSummary | null>(null)
  const [loading, setLoading] = useState(true)
  useEffect(() => {
    let cancelled = false
    setLoading(true)
    const url = `/api/admin/summary?q=${encodeURIComponent(query)}&limit=50`
    void fetch(url).then(async r => {
      if (cancelled) return
      if (r.ok) setData(await r.json() as AggregatedSummary)
      setLoading(false)
    })
    return () => { cancelled = true }
  }, [query])
  return { data, loading }
}
