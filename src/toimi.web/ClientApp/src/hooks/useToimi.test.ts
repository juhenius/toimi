import { renderHook, act, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const { fakes, FakeConnection } = vi.hoisted(() => {
  type Handler = (...args: unknown[]) => void

  class FakeConnection {
    handlers = new Map<string, Handler>()
    state = 'Connected'
    url: string

    constructor(url: string) {
      this.url = url
    }

    on(name: string, cb: Handler) {
      this.handlers.set(name, cb)
    }

    onreconnecting() {}
    onreconnected() {}
    onclose() {}

    start() {
      return Promise.resolve()
    }

    stop() {
      return Promise.resolve()
    }

    invoke() {
      return Promise.resolve()
    }

    fire(name: string, ...args: unknown[]) {
      this.handlers.get(name)?.(...args)
    }
  }

  return { fakes: [] as InstanceType<typeof FakeConnection>[], FakeConnection }
})

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    private url = ''

    withUrl(url: string) {
      this.url = url
      return this
    }

    withAutomaticReconnect() {
      return this
    }

    configureLogging() {
      return this
    }

    build() {
      const connection = new FakeConnection(this.url)
      fakes.push(connection)
      return connection
    }
  }

  return {
    HubConnectionBuilder,
    HubConnectionState: { Connected: 'Connected' },
    LogLevel: { Warning: 'Warning' },
  }
})

import { useToimi } from './useToimi'

describe('useToimi streaming state', () => {
  beforeEach(() => {
    fakes.length = 0
  })

  it('clears isStreaming when switching conversations mid-stream', async () => {
    const { result } = renderHook(() => useToimi())
    await waitFor(() => expect(result.current.connectionStatus).toBe('connected'))
    const first = fakes[fakes.length - 1]

    await act(async () => {
      await result.current.sendMessage('hello')
    })
    act(() => {
      first.fire('ReceiveToken', 'partial ')
    })
    expect(result.current.isStreaming).toBe(true)

    // Switching conversations tears down the connection: MessageComplete will never
    // arrive, so the flag must be cleared here or the composer stays disabled forever.
    act(() => {
      result.current.loadConversation('11111111-1111-1111-1111-111111111111')
    })
    await waitFor(() => expect(fakes.length).toBe(2))
    const second = fakes[fakes.length - 1]
    act(() => {
      second.fire(
        'ConversationLoaded',
        '11111111-1111-1111-1111-111111111111',
        JSON.stringify([{ role: 'user', content: 'old message' }]),
      )
    })

    expect(result.current.isStreaming).toBe(false)
  })

  it('clears isStreaming on ConversationReset', async () => {
    const { result } = renderHook(() => useToimi())
    await waitFor(() => expect(result.current.connectionStatus).toBe('connected'))
    const connection = fakes[fakes.length - 1]

    await act(async () => {
      await result.current.sendMessage('hello')
    })
    expect(result.current.isStreaming).toBe(true)

    act(() => {
      connection.fire('ConversationReset')
    })

    expect(result.current.isStreaming).toBe(false)
    expect(result.current.messages).toEqual([])
  })
})

describe('useToimi tool-call indicators', () => {
  beforeEach(() => {
    fakes.length = 0
  })

  it('unmatched ToolCallEnd is dropped silently', async () => {
    const { result } = renderHook(() => useToimi())
    await waitFor(() => expect(result.current.connectionStatus).toBe('connected'))
    const connection = fakes[fakes.length - 1]

    await act(async () => {
      await result.current.sendMessage('hello')
    })
    act(() => {
      connection.fire('ToolCallStart', 'a', 'search', '{}')
    })

    expect(() => {
      act(() => {
        connection.fire('ToolCallEnd', 'never-started', 'result', 42)
      })
    }).not.toThrow()

    const last = result.current.messages[result.current.messages.length - 1]
    expect(last.toolCalls).toEqual([{ id: 'a', name: 'search', arguments: '{}', status: 'running' }])
  })

  it('interleaved tool calls complete independently', async () => {
    const { result } = renderHook(() => useToimi())
    await waitFor(() => expect(result.current.connectionStatus).toBe('connected'))
    const connection = fakes[fakes.length - 1]

    await act(async () => {
      await result.current.sendMessage('hello')
    })
    act(() => {
      connection.fire('ToolCallStart', 'a', 'search', '{}')
    })
    act(() => {
      connection.fire('ToolCallStart', 'b', 'fetch', '{}')
    })
    act(() => {
      connection.fire('ToolCallEnd', 'b', 'b-result', 10)
    })

    const midpoint = result.current.messages[result.current.messages.length - 1]
    const midA = midpoint.toolCalls?.find(tc => tc.id === 'a')
    const midB = midpoint.toolCalls?.find(tc => tc.id === 'b')
    expect(midA).toMatchObject({ status: 'running' })
    expect(midB).toMatchObject({ status: 'complete', result: 'b-result', durationMs: 10 })

    act(() => {
      connection.fire('ToolCallEnd', 'a', 'a-result', 20)
    })

    const final = result.current.messages[result.current.messages.length - 1]
    const finalA = final.toolCalls?.find(tc => tc.id === 'a')
    const finalB = final.toolCalls?.find(tc => tc.id === 'b')
    expect(finalA).toMatchObject({ status: 'complete', result: 'a-result', durationMs: 20 })
    expect(finalB).toMatchObject({ status: 'complete', result: 'b-result', durationMs: 10 })
  })
})
