import { Component, type ReactNode } from 'react'

interface Props { children: ReactNode }
interface State { error: Error | null }

/**
 * Class component because error boundaries are the only React mechanism for
 * catching render errors — there is no hook equivalent. One malformed message
 * or tool-call payload should degrade to this fallback, not blank the app.
 */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error) {
    console.error('Unhandled render error:', error)
  }

  render() {
    if (this.state.error) {
      return (
        <div className="flex h-screen flex-col items-center justify-center gap-4 bg-zinc-900 text-zinc-100">
          <p className="text-lg font-semibold">Something went wrong rendering this view.</p>
          <p className="max-w-xl truncate text-sm text-zinc-400">{this.state.error.message}</p>
          <button
            className="rounded bg-zinc-800 px-4 py-2 text-sm hover:bg-zinc-700"
            onClick={() => window.location.reload()}
          >
            Reload
          </button>
        </div>
      )
    }
    return this.props.children
  }
}
