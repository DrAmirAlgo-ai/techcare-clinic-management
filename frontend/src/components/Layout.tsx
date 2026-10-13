import { useState, type FormEvent } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { api, errorMessage, type SessionClinic } from '../api'
import { useAuth } from '../auth'
import { ErrorBoundary } from './ErrorBoundary'
import { fmtDate, roleLabel } from '../format'
import { Icon, LogoMark, type IconName } from './Icon'

/**
 * PLAN.md 1.4: each page shows when the account holds its permission key. The API enforces the same keys on
 * the endpoints, so the sidebar is only the visible half of the rule — a role still shapes the defaults that
 * an uncustomised account starts from.
 */
interface NavItem { to: string; label: string; icon: IconName; perm?: string }

const sections: { label: string; items: NavItem[] }[] = [
  {
    label: 'العمليات اليومية',
    items: [
      { to: '/app', label: 'لوحة التحكم', icon: 'home', perm: 'dashboard' },
      { to: '/app/queue', label: 'مرضى اليوم', icon: 'queue', perm: 'queue' },
      { to: '/app/appointments', label: 'حجز المواعيد', icon: 'calendar', perm: 'appointments' },
      { to: '/app/patients', label: 'ملفات المرضى', icon: 'users', perm: 'patients' },
      { to: '/app/invoices', label: 'الحسابات', icon: 'receipt', perm: 'invoices' },
    ],
  },
  {
    label: 'الذكاء الاصطناعي',
    items: [
      { to: '/app/assistant', label: 'المساعد الطبي', icon: 'sparkles', perm: 'assistant' },
    ],
  },
  {
    label: 'الإدارة',
    // The settings page reads the clinic profile (the dashboard permission), which every default role carries.
    items: [{ to: '/app/settings', label: 'الإعدادات', icon: 'settings', perm: 'dashboard' }],
  },
]

export function Layout() {
  const { session, logout, hasPermission } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  // Null for the SuperAdmin owner (PLAN.md 1.1); such a session is redirected to /owner before this renders.
  const slug = session?.clinic?.slug

  return (
    <div className="shell">
      <aside className="sidebar no-print">
        <div className="brand">
          {/* PLAN.md 10.7: the clinic's own logo/name replace the platform default once uploaded. */}
          {session?.clinic?.hasLogo
            ? <img className="brand-logo" src={`/api/public/clinics/${session.clinic.slug}/logo`} alt={session.clinic.displayName ?? session.clinic.name} />
            : <LogoMark />}
          <div>
            <div className="brand-name">{session?.clinic ? (session.clinic.displayName ?? session.clinic.name) : 'TechCare Clinic'}</div>
            <div className="brand-sub">{session?.clinic ? 'TechCare Clinic' : 'TechCare'}</div>
          </div>
        </div>
        {sections.map((s) => {
          const items = s.items.filter((l) => !l.perm || hasPermission(l.perm))
          if (items.length === 0) return null
          return (
            <div key={s.label}>
              <div className="nav-label">{s.label}</div>
              {items.map((l) => (
                <NavLink key={l.to} to={l.to} end={l.to === '/app'}>
                  <Icon name={l.icon} /> {l.label}
                </NavLink>
              ))}
            </div>
          )
        })}
        <div className="spacer" />
        <div className="user-box">
          <span className="avatar">{session?.user.fullName.trim().charAt(0)}</span>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div className="name">{session?.user.fullName}</div>
            <div className="role">{session && roleLabel[session.user.role]}</div>
          </div>
          <button className="icon-btn" title="تسجيل الخروج" onClick={() => { logout(); navigate('/login') }}>
            <Icon name="logout" />
          </button>
        </div>
      </aside>
      <div className="main">
        <header className="topbar no-print">
          <div className="date"><Icon name="calendar" size={16} /> {fmtDate(new Date().toISOString())}</div>
          {slug && (
            <div className="row tight">
              <a className="btn ghost sm" href={`/c/${slug}/display`} target="_blank" rel="noreferrer"><Icon name="monitor" size={16} /> شاشة الانتظار</a>
              <a className="btn ghost sm" href={`/c/${slug}`} target="_blank" rel="noreferrer"><Icon name="globe" size={16} /> موقع العيادة</a>
            </div>
          )}
        </header>
        {/* PLAN.md 10.3: the subscription state bar; the owner's session holds no clinic, so it never shows there. */}
        {session?.clinic && <SubscriptionBar clinic={session.clinic} />}
        <main className="content">
          {/* PLAN_ADDENDUM.md 7.10: a crashing page shows the Arabic boundary with a retry button, not a blank area. */}
          <ErrorBoundary key={location.pathname}>
            <Outlet />
          </ErrorBoundary>
        </main>
      </div>
      {session?.mustChangePassword && <ForcePasswordChange username={session.user.username} />}
    </div>
  )
}

/**
 * PLAN.md 10.3: the subscription bar. A suspended or expired clinic — or one whose trial just ran out — is
 * read-only on the API side (every write answers 403 with the Arabic explanation), so the bar says so;
 * otherwise a short window before the end date reminds the account who to renew with. No bar when the
 * date is far away or the active subscription has no end date.
 */
function SubscriptionBar({ clinic }: { clinic: SessionClinic }) {
  const status = clinic.subscriptionStatus
  const days = clinic.daysRemaining
  if (status === 'Suspended' || status === 'Expired' || (days != null && days <= 0)) {
    return (
      <div className="sub-bar danger no-print">
        <Icon name="lock" size={16} />
        عيادتكم على وضع القراءة فقط — الاشتراك منتهي أو موقوف؛ يمكنك الاطلاع على السجلات، ولا يمكن إضافة أي بيانات جديدة حتى تجديد الاشتراك من مالك المنصة.
      </div>
    )
  }
  if (days != null && days <= 7) {
    const text = days === 1 ? 'اشتراكك ينتهي غدًا' : days === 2 ? 'اشتراكك ينتهي بعد يومين' : `اشتراكك ينتهي بعد ${days} يوم`
    return (
      <div className="sub-bar warn no-print">
        <Icon name="clock" size={16} />
        {text} — راجع مالك المنصة لتجديد الاشتراك.
      </div>
    )
  }
  return null
}

/**
 * PLAN_ADDENDUM.md 7.5: accounts created by the admin start with a temporary password. Until its owner
 * changes it, this overlay covers the whole app — there is no way around it except logging out. After the
 * change the app signs in again with the new password, which replaces the session (and its flag).
 */
function ForcePasswordChange({ username }: { username: string }) {
  const { login, logout } = useAuth()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (next !== confirm) {
      setError('تأكيد كلمة المرور غير مطابق')
      return
    }
    setBusy(true)
    setError('')
    try {
      await api('/api/auth/change-password', { method: 'POST', body: { currentPassword: current, newPassword: next } })
      await login(username, next) // fresh token; mustChangePassword is now false
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div style={{ position: 'fixed', inset: 0, zIndex: 50, background: 'rgba(15, 23, 32, 0.7)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
      <form className="login-card stack" style={{ gap: 16, maxWidth: 380, width: '100%' }} onSubmit={submit}>
        <h1>تغيير كلمة المرور</h1>
        <div className="muted small">هذه كلمة مؤقتة سُلمت لك عند إنشاء الحساب، ويجب تغييرها الآن لتكمل استخدام النظام.</div>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        <div className="field"><label>كلمة المرور الحالية</label><input className="input" dir="ltr" type="password" value={current} onChange={(e) => setCurrent(e.target.value)} required autoFocus autoComplete="current-password" /></div>
        <div className="field"><label>كلمة المرور الجديدة</label><input className="input" dir="ltr" type="password" value={next} onChange={(e) => setNext(e.target.value)} required minLength={10} autoComplete="new-password" /></div>
        <div className="field"><label>تأكيد كلمة المرور الجديدة</label><input className="input" dir="ltr" type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required minLength={10} autoComplete="new-password" /></div>
        <div className="row between" style={{ gap: 12 }}>
          <button type="button" className="btn ghost sm" onClick={logout}>تسجيل الخروج</button>
          <button className="btn" disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="check" />} حفظ</button>
        </div>
      </form>
    </div>
  )
}
