import { useState, type FormEvent } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router-dom'
import { ApiError, errorMessage } from '../api'
import { useAuth } from '../auth'
import { Icon, LogoMark, type IconName } from '../components/Icon'

const features: { icon: IconName; text: string }[] = [
  { icon: 'queue', text: 'طابور ذكي وشاشة انتظار مباشرة' },
  { icon: 'file', text: 'ملفات المرضى والروشتات الإلكترونية' },
  { icon: 'globe', text: 'حجز إلكتروني ومتابعة الدور' },
  { icon: 'sparkles', text: 'مساعد طبي بالذكاء الاصطناعي' },
]

export function Login() {
  const { session, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [clinicCode, setClinicCode] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  // PLAN_ADDENDUM.md 7.2: shown only after a 409 — the username exists at more than one clinic.
  const [needClinicCode, setNeedClinicCode] = useState(false)
  // PLAN_ADDENDUM.md 7.10: the unified 401 (api.ts) lands here with a note that the session expired, not a mystery.
  const [sessionExpired] = useState(() => {
    const flagged = sessionStorage.getItem('tc_session_expired') === '1'
    sessionStorage.removeItem('tc_session_expired')
    return flagged
  })

  if (session) return <Navigate to={session.user.role === 'SuperAdmin' ? '/owner' : '/app'} replace />

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const s = await login(username, password, clinicCode.trim() || undefined)
      // The owner account signs in here by accident at most: its panel lives at /owner (PLAN.md 1.1).
      if (s.user.role === 'SuperAdmin') {
        navigate('/owner', { replace: true })
        return
      }
      const from = (location.state as { from?: string } | null)?.from
      navigate(from && from.startsWith('/app') ? from : '/app', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
      if (err instanceof ApiError && err.status === 409) setNeedClinicCode(true)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-page">
      <div className="login-form-wrap">
        <form className="login-card stack" onSubmit={submit} style={{ gap: 18 }}>
          <div className="row" style={{ gap: 12 }}>
            <LogoMark size={44} />
            <div>
              <div className="strong" style={{ fontSize: 18 }}>TechCare Clinic</div>
              <div className="muted small">نظام إدارة العيادات</div>
            </div>
          </div>
          <div>
            <h1>تسجيل الدخول</h1>
          </div>
          {sessionExpired && !error && <div className="alert success"><Icon name="alert" /> انتهت الجلسة أو انتهت صلاحيتها، يرجى تسجيل الدخول مرة أخرى.</div>}
          {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
          <div className="field">
            <label>اسم المستخدم</label>
            <input className="input" value={username} onChange={(e) => setUsername(e.target.value)} autoFocus required dir="ltr" autoComplete="username" />
          </div>
          <div className="field">
            <label>كلمة المرور</label>
            <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required dir="ltr" autoComplete="current-password" />
          </div>
          {needClinicCode && (
            <div className="field">
              <label>كود العيادة</label>
              <input className="input" dir="ltr" value={clinicCode} onChange={(e) => setClinicCode(e.target.value)} required autoFocus placeholder="dr-amir" />
            </div>
          )}
          <button className="btn lg block" disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="enter" />} دخول</button>
        </form>
      </div>
      <div className="login-art">
        <div className="row" style={{ gap: 12 }}>
          <LogoMark size={40} />
          <span className="strong" style={{ fontSize: 17 }}>TechCare Clinic</span>
        </div>
        <div>
          <h2>إدارة متكاملة لعيادتك في منصة واحدة</h2>
          <div className="features">
            {features.map((f) => (
              <div key={f.text} className="feature">
                <span className="icon-wrap"><Icon name={f.icon} /></span> {f.text}
              </div>
            ))}
          </div>
        </div>
        <div className="small" style={{ color: '#8fa9bb' }}>© {new Date().getFullYear()} TechCare</div>
      </div>
    </div>
  )
}
