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
