import { useEffect, useRef } from 'react'
import Markdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeHighlight from 'rehype-highlight'
import type { ToimiMessage } from '../hooks/useToimi.ts'
import { ToolCallList } from './ToolCallList.tsx'

interface MessageListProps {
  messages: ToimiMessage[]
}

export function MessageList({ messages }: MessageListProps) {
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  if (messages.length === 0) {
    return (
      <div className="flex-1 flex items-center justify-center text-zinc-500">
        Send a message to start chatting.
      </div>
    )
  }

  return (
    <div className="flex-1 overflow-y-auto p-4 space-y-4">
      {messages.map((msg, i) => (
        <div
          key={i}
          className={`flex ${msg.role === 'user' ? 'justify-end' : 'justify-start'}`}
        >
          <div
            className={`max-w-[80%] rounded-lg px-4 py-2 ${
              msg.role === 'user'
                ? 'bg-blue-600 text-white'
                : msg.error
                  ? 'bg-red-900/50 text-red-200 border border-red-700'
                  : 'bg-zinc-800 text-zinc-100'
            }`}
          >
            {msg.role === 'assistant' && !msg.error ? (
              <>
                {msg.toolCalls && msg.toolCalls.length > 0 && (
                  <ToolCallList toolCalls={msg.toolCalls} />
                )}
                <div className="prose prose-invert prose-sm max-w-none">
                  <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeHighlight]}>
                    {msg.content || '...'}
                  </Markdown>
                </div>
              </>
            ) : (
              <p className="whitespace-pre-wrap">{msg.content}</p>
            )}
          </div>
        </div>
      ))}
      <div ref={bottomRef} />
    </div>
  )
}
