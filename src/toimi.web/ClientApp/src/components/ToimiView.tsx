import { useToimi } from '../hooks/useToimi.ts'
import { MessageList } from './MessageList.tsx'
import { ToimiInput } from './ToimiInput.tsx'
import { ConversationList } from './ConversationList.tsx'
import { ActivityList } from './ActivityList.tsx'

export function ToimiView() {
  const { messages, isStreaming, connectionStatus, toolCount, sendMessage,
    conversations, currentConversationId, loadConversation, newConversation } = useToimi()

  return (
    <div className="flex flex-col h-screen bg-zinc-900 text-zinc-100">
      <header className="flex items-center justify-between px-4 py-3 border-b border-zinc-700">
        <h1 className="text-lg font-semibold">Toimi</h1>
        <div className="flex items-center gap-3 text-sm">
          <ActivityList />
          <ConversationList
            conversations={conversations}
            currentConversationId={currentConversationId}
            onLoad={loadConversation}
            onNew={newConversation}
          />
          {toolCount > 0 && (
            <span className="text-zinc-400">{toolCount} tools</span>
          )}
          <span className={`flex items-center gap-1.5 ${
            connectionStatus === 'connected' ? 'text-green-400' :
            connectionStatus === 'reconnecting' ? 'text-yellow-400' :
            connectionStatus === 'connecting' ? 'text-zinc-400' :
            'text-red-400'
          }`}>
            <span className="w-2 h-2 rounded-full bg-current" />
            {connectionStatus}
          </span>
        </div>
      </header>

      <MessageList messages={messages} />
      <ToimiInput onSend={sendMessage} disabled={isStreaming || connectionStatus !== 'connected'} />
    </div>
  )
}
