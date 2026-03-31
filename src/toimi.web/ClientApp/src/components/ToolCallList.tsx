import { useState } from 'react'
import type { ToolCall } from '../hooks/useToimi.ts'

interface ToolCallListProps {
  toolCalls: ToolCall[]
}

export function ToolCallList({ toolCalls }: ToolCallListProps) {
  if (toolCalls.length === 0) return null

  return (
    <div className="space-y-1 mb-2">
      {toolCalls.map(tc => (
        <ToolCallItem key={tc.id} toolCall={tc} />
      ))}
    </div>
  )
}

function ToolCallItem({ toolCall }: { toolCall: ToolCall }) {
  const [expanded, setExpanded] = useState(false)
  const isRunning = toolCall.status === 'running'

  return (
    <div className="rounded border border-zinc-700 bg-zinc-900 text-xs">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-2 w-full px-2 py-1.5 text-left hover:bg-zinc-800 rounded"
      >
        {isRunning ? (
          <span className="w-2 h-2 rounded-full bg-blue-400 animate-pulse" />
        ) : (
          <span className="w-2 h-2 rounded-full bg-green-400" />
        )}
        <span className="font-mono text-zinc-300">{toolCall.name}</span>
        {toolCall.durationMs != null && (
          <span className="text-zinc-500 ml-auto">{toolCall.durationMs}ms</span>
        )}
        <span className="text-zinc-500">{expanded ? '▾' : '▸'}</span>
      </button>
      {expanded && (
        <div className="px-2 pb-2 space-y-1">
          <div>
            <span className="text-zinc-500">Arguments:</span>
            <pre className="mt-0.5 p-1.5 rounded bg-zinc-950 text-zinc-400 overflow-x-auto whitespace-pre-wrap break-all">
              {formatJson(toolCall.arguments)}
            </pre>
          </div>
          {toolCall.result != null && (
            <div>
              <span className="text-zinc-500">Result:</span>
              <pre className="mt-0.5 p-1.5 rounded bg-zinc-950 text-zinc-400 overflow-x-auto whitespace-pre-wrap break-all max-h-40 overflow-y-auto">
                {formatJson(toolCall.result)}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function formatJson(str: string): string {
  try {
    return JSON.stringify(JSON.parse(str), null, 2)
  } catch {
    return str
  }
}
