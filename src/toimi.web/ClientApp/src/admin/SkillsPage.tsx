import { useMemo, useState, useEffect } from 'react'
import { useAdminList, adminDelete, adminPut } from './useAdmin'
import { useDebounced } from './useDebounced'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { FetchErrorBanner } from './FetchErrorBanner'
import { StaleConflictModal } from './StaleConflictModal'
import { EmptyState } from './EmptyState'

interface SkillItem {
  id: string
  name: string
  description: string
  instructions: string
  tags: string[]
  createdAt: string
  updatedAt: string
}

interface PagedResult<T> {
  items: T[]
  page: number
  size: number
  total: number
}

export function SkillsPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const dq = useDebounced(q)
  const { data, error, loading, reload } = useAdminList<PagedResult<SkillItem>>(
    'taidot',
    `items?page=${page}&size=20&q=${encodeURIComponent(dq)}`
  )
  const [pendingDelete, setPendingDelete] = useState<SkillItem | null>(null)
  const [editing, setEditing] = useState<SkillItem | null>(null)

  const columns: Column<SkillItem>[] = useMemo(
    () => [
      { key: 'name', header: 'Name', render: (r) => r.name },
      { key: 'description', header: 'Description', render: (r) => r.description },
      { key: 'tags', header: 'Tags', render: (r) => r.tags.join(', ') },
      {
        key: 'updated',
        header: 'Updated',
        render: (r) => new Date(r.updatedAt).toLocaleString(),
      },
      {
        key: 'actions',
        header: '',
        render: (r) => (
          <div className="flex gap-2">
            <button
              className="text-blue-400"
              onClick={(e) => {
                e.stopPropagation()
                setEditing(r)
              }}
            >
              Edit
            </button>
            <button
              className="text-red-400"
              onClick={(e) => {
                e.stopPropagation()
                setPendingDelete(r)
              }}
            >
              Delete
            </button>
          </div>
        ),
      },
    ],
    []
  )

  return (
    <div>
      <h1 className="text-2xl mb-4 text-zinc-100">Skills</h1>
      <FetchErrorBanner error={error} />
      <input
        className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-4"
        placeholder="Search name or description…"
        value={q}
        onChange={(e) => {
          setPage(1)
          setQ(e.target.value)
        }}
      />
      {loading && <div className="text-zinc-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && (
        <EmptyState message="No skills." />
      )}
      {data && data.items.length > 0 && (
        <>
          <DataTable rows={data.items} columns={columns} />
          <div className="mt-3 text-sm text-zinc-400 flex gap-3 items-center">
            <button
              disabled={page === 1}
              onClick={() => setPage((p) => p - 1)}
              className="disabled:opacity-30"
            >
              ← Prev
            </button>
            <span>
              Page {data.page} ({data.total} total)
            </span>
            <button
              disabled={data.page * data.size >= data.total}
              onClick={() => setPage((p) => p + 1)}
              className="disabled:opacity-30"
            >
              Next →
            </button>
          </div>
        </>
      )}
      <ConfirmDelete
        open={!!pendingDelete}
        label="skill"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('taidot', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
      <EditSkillDialog
        item={editing}
        onClose={() => setEditing(null)}
        onSaved={async () => {
          setEditing(null)
          await reload()
        }}
      />
    </div>
  )
}

function EditSkillDialog({
  item,
  onClose,
  onSaved,
}: {
  item: SkillItem | null
  onClose: () => void
  onSaved: () => void
}) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [instructions, setInstructions] = useState('')
  const [tags, setTags] = useState('')
  const [stale, setStale] = useState(false)
  useEffect(() => {
    if (item) {
      setName(item.name)
      setDescription(item.description)
      setInstructions(item.instructions)
      setTags(item.tags.join(', '))
    }
  }, [item])
  if (!item) return null
  return (
    <>
      <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
        <div className="bg-zinc-900 border border-zinc-700 rounded p-6 w-[40rem] max-h-[80vh] overflow-y-auto">
          <h3 className="text-lg mb-3 text-zinc-100">Edit skill</h3>
          <label className="block text-sm text-zinc-400 mb-1">Name</label>
          <input
            className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-3"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <label className="block text-sm text-zinc-400 mb-1">Description</label>
          <input
            className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-3"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
          />
          <label className="block text-sm text-zinc-400 mb-1">Instructions</label>
          <textarea
            className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 h-40 mb-3"
            value={instructions}
            onChange={(e) => setInstructions(e.target.value)}
          />
          <label className="block text-sm text-zinc-400 mb-1">Tags (comma-separated)</label>
          <input
            className="w-full bg-zinc-800 border border-zinc-700 rounded p-2 mb-3"
            value={tags}
            onChange={(e) => setTags(e.target.value)}
          />
          <div className="flex justify-end gap-2 mt-3">
            <button className="px-3 py-1 rounded bg-zinc-700" onClick={onClose}>
              Cancel
            </button>
            <button
              className="px-3 py-1 rounded bg-blue-600"
              onClick={async () => {
                const result = await adminPut<
                  { name: string; description: string; instructions: string; tags: string[] },
                  SkillItem
                >(
                  'taidot',
                  `items/${item.id}`,
                  {
                    name,
                    description,
                    instructions,
                    tags: tags
                      .split(',')
                      .map((t) => t.trim())
                      .filter(Boolean),
                  },
                  item.updatedAt
                )
                if (result.ok) onSaved()
                else if (result.status === 409) setStale(true)
                else alert(`Update failed (HTTP ${result.status})`)
              }}
            >
              Save
            </button>
          </div>
        </div>
      </div>
      <StaleConflictModal
        open={stale}
        onDismiss={() => setStale(false)}
        onReload={() => {
          setStale(false)
          onSaved()
        }}
      />
    </>
  )
}
