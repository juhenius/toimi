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
