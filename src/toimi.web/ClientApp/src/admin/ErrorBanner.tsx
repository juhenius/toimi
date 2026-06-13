export function ErrorBanner({ errors }: { errors: { tool: string; message: string }[] }) {
  if (!errors.length) return null
  return (
    <div className="bg-yellow-900/40 border border-yellow-700 text-yellow-200 p-3 rounded mb-4 text-sm">
      Some stores are unavailable:&nbsp;
      {errors.map((e, i) => (
        <span key={e.tool}>
          {e.tool}{i < errors.length - 1 ? ', ' : ''}
        </span>
      ))}
    </div>
  )
}
