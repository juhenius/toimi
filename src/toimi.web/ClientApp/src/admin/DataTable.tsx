import React from 'react'

export interface Column<T> { key: string; header: string; render: (row: T) => React.ReactNode }

export function DataTable<T extends { id: string | number }>({ rows, columns, onRowClick }: {
  rows: T[]; columns: Column<T>[]; onRowClick?: (row: T) => void;
}) {
  return (
    <table className="w-full text-left text-sm">
      <thead className="text-zinc-400">
        <tr>{columns.map(c => <th key={c.key} className="px-3 py-2">{c.header}</th>)}</tr>
      </thead>
      <tbody>
        {rows.map(r => (
          <tr
            key={r.id}
            onClick={() => onRowClick?.(r)}
            className={onRowClick ? 'hover:bg-zinc-800 cursor-pointer' : ''}
          >
            {columns.map(c => <td key={c.key} className="px-3 py-2 border-t border-zinc-800">{c.render(r)}</td>)}
          </tr>
        ))}
      </tbody>
    </table>
  )
}
