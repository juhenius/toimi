import type { AdminFetchError } from './useAdmin'

export function FetchErrorBanner({ error }: { error: AdminFetchError | null }) {
  if (!error) return null
  return (
    <div className="bg-red-900/40 border border-red-700 text-red-200 p-3 rounded mb-4 text-sm">
      Failed to load (HTTP {error.status})
    </div>
  )
}
