import { useCallback, useEffect, useState } from 'react'

// counterpart: src/toimi.core/Admin/AdminRoutes.cs — the C# owner of the
// upstream /admin path contract behind the /api/admin/... prefix used here.
// TS cannot consume a C# constant: if toimi.web's /api/admin routes move,
// update the fetch paths here and in useAdminSummary.ts / UsagePage.tsx.

export interface AdminFetchError { status: number; body?: unknown }

export function useAdminList<T>(tool: string, path: string) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<AdminFetchError | null>(null)
  const [loading, setLoading] = useState(true)
  const reload = useCallback(async () => {
    setLoading(true); setError(null)
    try {
      const resp = await fetch(`/api/admin/${tool}/${path}`)
      if (!resp.ok) { setError({ status: resp.status, body: await safeJson(resp) }); return }
      setData(await resp.json() as T)
    } catch { setError({ status: 0 }) } finally { setLoading(false) }
  }, [tool, path])
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { void reload() }, [reload])
  return { data, error, loading, reload }
}

export async function adminPut<TBody, TResult>(
  tool: string, path: string, body: TBody, ifUnmodifiedSince: string,
): Promise<{ ok: true; data: TResult } | { ok: false; status: number; body?: unknown }> {
  const resp = await fetch(`/api/admin/${tool}/${path}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json', 'if-unmodified-since': ifUnmodifiedSince },
    body: JSON.stringify(body),
  })
  if (!resp.ok) return { ok: false, status: resp.status, body: await safeJson(resp) }
  return { ok: true, data: await resp.json() as TResult }
}

export async function adminDelete(tool: string, path: string): Promise<boolean> {
  const resp = await fetch(`/api/admin/${tool}/${path}`, { method: 'DELETE' })
  return resp.ok
}

export async function adminPost(tool: string, path: string): Promise<boolean> {
  const resp = await fetch(`/api/admin/${tool}/${path}`, { method: 'POST' })
  return resp.ok
}

async function safeJson(resp: Response) {
  try { return await resp.json() } catch { return undefined }
}
