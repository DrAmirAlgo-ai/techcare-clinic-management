import { Component, type ReactNode } from 'react'
import { Icon } from './Icon'

/**
 * PLAN_ADDENDUM.md 7.10: a component that crashes must not turn the whole app into a white screen — the Arabic
 * message plus a "retry" button that remounts the failed part. The full error stays in the console for a human.
 */
interface Props { children: ReactNode }
interface State { error: Error | null }

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error) {
    console.error('ErrorBoundary:', error)
  }

  private retry = () => this.setState({ error: null })

  render() {
    if (this.state.error === null) return this.props.children
    return (
      <div className="card stack" style={{ gap: 12, textAlign: 'center', padding: '40px 24px', margin: '24px auto', maxWidth: 480 }}>
        <Icon name="alert" size={32} />
        <h2>حدث خطأ غير متوقع في هذه الشاشة</h2>
        <div className="muted small">سُجّل الخطأ وسيصلح، ويمكنك إعادة المحاولة الآن.</div>
        <button className="btn" onClick={this.retry}>
          <Icon name="refresh" /> إعادة المحاولة
        </button>
      </div>
    )
  }
}