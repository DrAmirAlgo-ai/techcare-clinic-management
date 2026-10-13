import { useState, type FormEvent } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { errorMessage } from '../../api'
import { useAuth } from '../../auth'
import { Icon, LogoMark } from '../../components/Icon'

/**
 * Sign-in page of the owner panel (PLAN.md 1.1), on its own route (`/owner/login`) so it is clearly separate from
 * the clinic staff login. The API keeps a single login endpoint; this page only lets the SuperAdmin through and
 * signs a clinic account straight out again instead of handing it the clinic app.
 */
export function OwnerLogin() {
  const { session, login, logout } = useAuth()
  const navigate = useNavigate()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  if (session?.user.role === 'SuperAdmin') return <Navigate to="/owner" replace />

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const s = await login(username, password)
      if (s.user.role !== 'SuperAdmin') {
        logout()
        setError('هذا الحساب حساب عيادة، استخدم صفحة دخول العيادة')
        return
      }
      navigate('/owner', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
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
              <div className="strong" style={{ fontSize: 18 }}>TechCare · لوحة المالك</div>
              <div className="muted small">إدارة كل العيادات في المنصة</div>
            </div>
          </div>
          <div>
            <h1>دخول مالك النظام</h1>
          </div>
          {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
          <div className="field">
            <label>اسم المستخدم</label>
            <input className="input" value={username} onChange={(e) => setUsername(e.target.value)} autoFocus required dir="ltr" autoComplete="username" />
          </div>
          <div className="field">
            <label>كلمة المرور</label>
            <input className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required dir="ltr" autoComplete="current-password" />
          </div>
          <button className="btn lg block" disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="enter" />} دخول</button>
          <a className="small muted" href="/login">دخول موظفي العيادة</a>
        </form>
      </div>
      <div className="login-art">
        <div className="row" style={{ gap: 12 }}>
          <LogoMark size={40} />
          <span className="strong" style={{ fontSize: 17 }}>TechCare Clinic</span>
        </div>
        <div>
          <h2>نظرة واحدة على كل عيادات المنصة</h2>
          <div className="features">
            <div className="feature"><span className="icon-wrap"><Icon name="users" /></span> كل العيادات وفرق العمل</div>
            <div className="feature"><span className="icon-wrap"><Icon name="file" /></span> عدد المرضى والزيارات والفواتير</div>
            <div className="feature"><span className="icon-wrap"><Icon name="cash" /></span> إيرادات كل عيادة وآخر نشاط</div>
          </div>
        </div>
        <div className="small" style={{ color: '#8fa9bb' }}>© {new Date().getFullYear()} TechCare</div>
      </div>
    </div>
  )
}
