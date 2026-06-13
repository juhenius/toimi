export function StaleConflictModal({ open, onReload, onDismiss }: {
  open: boolean; onReload: () => void; onDismiss: () => void;
}) {
  if (!open) return null
  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
      <div className="bg-zinc-900 border border-zinc-700 rounded p-6 w-96">
        <h3 className="text-lg mb-3 text-zinc-100">Item changed elsewhere</h3>
        <p className="text-sm text-zinc-400 mb-4">Reload to see the latest version.</p>
        <div className="flex justify-end gap-2">
          <button className="px-3 py-1 rounded bg-zinc-700" onClick={onDismiss}>Cancel</button>
          <button className="px-3 py-1 rounded bg-blue-600" onClick={onReload}>Reload</button>
        </div>
      </div>
    </div>
  )
}
