import { useState, useEffect, useRef, useCallback } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import type { HubConnection } from '@microsoft/signalr'

export interface ToolCall {
  id: string
  name: string
  arguments: string
  result?: string
  durationMs?: number
  status: 'running' | 'complete'
}

export interface ToimiMessage {
  role: 'user' | 'assistant'
  content: string
  error?: boolean
  toolCalls?: ToolCall[]
}

export interface ConversationSummary {
  id: string
  title: string | null
  lastMessageAt: string
}

export type ConnectionStatus = 'connecting' | 'connected' | 'disconnected' | 'reconnecting'

export function useToimi() {
  const [messages, setMessages] = useState<ToimiMessage[]>([])
  const [isStreaming, setIsStreaming] = useState(false)
  const [connectionStatus, setConnectionStatus] = useState<ConnectionStatus>('disconnected')
  const [toolCount, setToolCount] = useState(0)
  const [conversations, setConversations] = useState<ConversationSummary[]>([])
  const [currentConversationId, setCurrentConversationId] = useState<string | null>(null)
  const [reconnectCounter, setReconnectCounter] = useState(0)
  const connectionRef = useRef<HubConnection | null>(null)
  const streamBufferRef = useRef('')
  const conversationIdRef = useRef<string | undefined>(undefined)
  // Mirrors currentConversationId for use in connection callbacks, which would
  // otherwise close over the state value from the render that built them.
  const currentConversationIdRef = useRef<string | null>(null)

  useEffect(() => {
    const url = conversationIdRef.current
      ? `/toimihub?conversationId=${conversationIdRef.current}`
      : '/toimihub'

    const connection = new HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('Connected', (count: number) => {
      setToolCount(count)
      connection.invoke('ListConversations')
    })

    connection.on('ConversationLoaded', (conversationId: string, messagesJson: string) => {
      const raw = JSON.parse(messagesJson) as { role: string; content: string; toolCallsJson?: string | null }[]
      const loaded: ToimiMessage[] = raw.map(m => {
        const msg: ToimiMessage = {
          role: m.role as 'user' | 'assistant',
          content: m.content,
        }
        if (m.toolCallsJson) {
          const rawCalls = JSON.parse(m.toolCallsJson) as { type: string; CallId?: string; callId?: string; Name?: string; name?: string; Arguments?: string; arguments?: string; Result?: string; result?: string; DurationMs?: number; durationMs?: number }[]
          const toolCalls: ToolCall[] = []
          for (const rc of rawCalls) {
            if (rc.type === 'call') {
              toolCalls.push({
                id: rc.CallId ?? rc.callId ?? '',
                name: rc.Name ?? rc.name ?? '',
                arguments: rc.Arguments ?? rc.arguments ?? '',
                status: 'complete',
              })
            } else if (rc.type === 'result') {
              const existing = toolCalls.find(tc => tc.id === (rc.CallId ?? rc.callId))
              if (existing) {
                existing.result = rc.Result ?? rc.result
                existing.durationMs = rc.DurationMs ?? rc.durationMs
              }
            }
          }
          if (toolCalls.length > 0) {
            msg.toolCalls = toolCalls
          }
        }
        return msg
      })
      setMessages(loaded)
      setCurrentConversationId(conversationId)
      currentConversationIdRef.current = conversationId
    })

    // Lazy conversations: the row is created on the first message, and the server
    // tells us its id here. Mirror ConversationLoaded's id-capture so a later
    // reconnect rebuilds the connection with ?conversationId=<id> and resyncs.
    connection.on('ConversationCreated', (id: string) => {
      setCurrentConversationId(id)
      currentConversationIdRef.current = id
    })

    // NewConversation started a fresh, row-less conversation server-side. Reset the
    // view and forget the current id so reconnect-resync stays fresh until the first
    // message creates the row (and ConversationCreated hands us the new id).
    connection.on('ConversationReset', () => {
      setMessages([])
      setIsStreaming(false)
      setCurrentConversationId(null)
      currentConversationIdRef.current = null
      conversationIdRef.current = undefined
    })

    connection.on('ConversationList', (json: string) => {
      const list = JSON.parse(json) as ConversationSummary[]
      setConversations(list)
    })

    connection.on('ReceiveToken', (token: string) => {
      streamBufferRef.current += token
      setMessages(prev => {
        const updated = [...prev]
        const last = updated[updated.length - 1]
        if (last?.role === 'assistant' && !last.error) {
          updated[updated.length - 1] = { ...last, content: streamBufferRef.current }
        }
        return updated
      })
    })

    connection.on('ToolCallStart', (callId: string, name: string, args: string) => {
      setMessages(prev => {
        const updated = [...prev]
        const last = updated[updated.length - 1]
        if (last?.role === 'assistant' && !last.error) {
          const toolCalls = [...(last.toolCalls ?? []), { id: callId, name, arguments: args, status: 'running' as const }]
          updated[updated.length - 1] = { ...last, toolCalls }
        }
        return updated
      })
    })

    connection.on('ToolCallEnd', (callId: string, result: string, durationMs: number) => {
      setMessages(prev => {
        const updated = [...prev]
        const last = updated[updated.length - 1]
        if (last?.role === 'assistant' && !last.error && last.toolCalls) {
          const toolCalls = last.toolCalls.map(tc =>
            tc.id === callId ? { ...tc, result, durationMs, status: 'complete' as const } : tc
          )
          updated[updated.length - 1] = { ...last, toolCalls }
        }
        return updated
      })
    })

    connection.on('MessageComplete', () => {
      setIsStreaming(false)
      connection.invoke('ListConversations')
    })

    connection.on('Error', (error: string) => {
      setIsStreaming(false)
      setMessages(prev => {
        const updated = [...prev]
        const last = updated[updated.length - 1]
        if (last?.role === 'assistant') {
          updated[updated.length - 1] = { role: 'assistant', content: error, error: true }
        } else {
          updated.push({ role: 'assistant', content: error, error: true })
        }
        return updated
      })
    })

    connection.onreconnecting(() => setConnectionStatus('reconnecting'))
    connection.onreconnected(() => {
      setConnectionStatus('connected')
      // The reconnect rebuilt the server session: the hub re-ran OnConnectedAsync
      // with the ORIGINAL connection URL, so any response that was streaming is
      // gone and the session now holds whatever that URL implies — a fresh
      // conversation (no query param) or a replay of the URL's conversation id,
      // which may not be the one the user is viewing. The hub exposes no
      // invokable reload method, so converge to the DB source of truth here.
      setIsStreaming(false)
      const activeId = currentConversationIdRef.current ?? undefined
      if (!activeId) {
        // No conversation was ever loaded on this client; the rebuilt session
        // points at a brand-new conversation, so reset the view to match. Any
        // prior exchange is persisted and reachable from the conversation list
        // (refreshed by the 'Connected' handler on this same reconnect).
        setMessages([])
      } else if (conversationIdRef.current !== activeId) {
        // The connection URL carries a stale id (the user switched conversations
        // via NewConversation since connecting). Rebuild the connection through
        // the normal query-param load flow so the server replays the ACTIVE
        // conversation and ConversationLoaded replaces client message state.
        conversationIdRef.current = activeId
        setReconnectCounter(c => c + 1)
      }
      // else: the URL already names the active conversation — OnConnectedAsync
      // replayed it from the DB and re-sent ConversationLoaded, which replaces
      // client message state; nothing more to do.
    })
    connection.onclose(() => setConnectionStatus('disconnected'))

    // eslint-disable-next-line react-hooks/set-state-in-effect
    setConnectionStatus('connecting')
    void connection.start().then(() => {
      setConnectionStatus('connected')
    })

    connectionRef.current = connection

    return () => {
      connection.stop()
    }
  }, [reconnectCounter])

  const sendMessage = useCallback(async (text: string) => {
    const connection = connectionRef.current
    if (!connection || connection.state !== HubConnectionState.Connected) return

    setMessages(prev => [...prev, { role: 'user', content: text }])
    streamBufferRef.current = ''
    setMessages(prev => [...prev, { role: 'assistant', content: '' }])
    setIsStreaming(true)

    await connection.invoke('SendMessage', text)
  }, [])

  const loadConversation = useCallback((id: string) => {
    connectionRef.current?.stop()
    // The old connection is gone mid-stream: MessageComplete will never arrive,
    // so clear the flag here or the composer stays disabled forever.
    setIsStreaming(false)
    conversationIdRef.current = id
    setReconnectCounter(c => c + 1)
  }, [])

  const newConversation = useCallback(async () => {
    const connection = connectionRef.current
    if (!connection || connection.state !== HubConnectionState.Connected) return
    await connection.invoke('NewConversation')
    connection.invoke('ListConversations')
  }, [])

  return {
    messages, isStreaming, connectionStatus, toolCount, sendMessage,
    conversations, currentConversationId, loadConversation, newConversation,
  }
}
