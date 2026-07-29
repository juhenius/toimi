import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { useAdminList } from './useAdmin'
import { useAdminSummary } from './useAdminSummary'

describe('useAdminList', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('surfaces a network failure as an error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('network down')))

    const { result } = renderHook(() => useAdminList('tietue', 'entities'))

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.error).toEqual({ status: 0 })
  })

  it('surfaces an HTTP error response as status + body', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: false,
      status: 409,
      json: () => Promise.resolve({ message: 'stale' }),
    }))

    const { result } = renderHook(() => useAdminList('tietue', 'entities'))

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.error).toEqual({ status: 409, body: { message: 'stale' } })
    expect(result.current.data).toBeNull()
  })
})

describe('useAdminSummary', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('clears loading when the fetch rejects', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('network down')))

    const { result } = renderHook(() => useAdminSummary(''))

    await waitFor(() => expect(result.current.loading).toBe(false))
  })
})
