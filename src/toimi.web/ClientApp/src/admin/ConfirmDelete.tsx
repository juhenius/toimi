export function ConfirmDelete({ open, label, onConfirm, onCancel }: {
  open: boolean; label: string; onConfirm: () => void; onCancel: () => void;
}) {
  if (!open) return null
  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
      <div className="bg-zinc-900 border border-zinc-700 rounded p-6 w-96">
        <h3 className="text-lg mb-3 text-zinc-100">Delete {label}?</h3>
        <p className="text-sm text-zinc-400 mb-4">This cannot be undone.</p>
        <div className="flex justify-end gap-2">
          <button className="px-3 py-1 rounded bg-zinc-700" onClick={onCancel}>Cancel</button>
          <button className="px-3 py-1 rounded bg-red-700" onClick={onConfirm}>Delete</button>
        </div>
      </div>
    </div>
  )
}
