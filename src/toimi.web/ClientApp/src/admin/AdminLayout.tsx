import { Link, NavLink, Outlet } from 'react-router-dom'

const links = [
  { to: '/admin', label: 'Dashboard', end: true },
  { to: '/admin/data', label: 'Data' },
  { to: '/admin/types', label: 'Types' },
  { to: '/admin/usage', label: 'Usage' },
]

export function AdminLayout() {
  return (
    <div className="flex h-screen bg-zinc-900 text-zinc-300">
      <aside className="w-48 border-r border-zinc-700 p-4 flex flex-col gap-2">
        <Link to="/" className="text-sm text-zinc-400 hover:text-zinc-100 mb-4">← Chat</Link>
        {links.map(l => (
          <NavLink
            key={l.to}
            to={l.to}
            end={l.end}
            className={({ isActive }) =>
              `px-3 py-2 rounded ${isActive ? 'bg-zinc-700' : 'hover:bg-zinc-800'}`
            }
          >
            {l.label}
          </NavLink>
        ))}
      </aside>
      <main className="flex-1 overflow-y-auto p-6">
        <Outlet />
      </main>
    </div>
  )
}
