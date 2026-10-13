import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage, type Role } from '../api'
import { useAuth } from '../auth'
import { Icon } from '../components/Icon'
import { Modal } from '../components/Modal'
import { dayLabel, roleLabel } from '../format'

interface ClinicSettings {
  name: string
  slug: string
  doctorName: string
  specialty: string
  about?: string
  phone?: string
  address?: string
  consultationFee: number
  followUpFee: number
  avgMinutesPerPatient: number
  workStart: string
  workEnd: string
  workDays: string
  onlineBookingEnabled: boolean
  /** PLAN_ADDENDUM.md 9.3: days a logically deleted patient file is kept before it may be removed for good. */
  dataRetentionDays: number
  /** PLAN.md 10.7: the name shown in the app, the public page and printouts; equals name when unchanged. */
  displayName?: string
  /** PLAN.md 10.7: true when the clinic uploaded its logo (served from /api/public/clinics/{slug}/logo). */
  hasLogo?: boolean
  /** PLAN.md 10.12: doctor's qualifications for the prescription header. */
  qualifications?: string
  /** PLAN.md 10.12: working hours text for the prescription header. */
  workingHoursText?: string
}

interface UserRow {
  id: number
  fullName: string
  username: string
  role: Role
  isActive: boolean
  /** PLAN.md 1.4: true when the account carries an explicit permission set instead of its role's defaults. */
  isExplicit?: boolean
  permissions?: string[]
}

/** PLAN.md 1.4: Arabic display names for the permission keys (the keys themselves are authoritative on the API). */
const PERMISSION_LABELS: Record<string, string> = {
  dashboard: 'لوحة التحكم',
  queue: 'مرضى اليوم',
  appointments: 'المواعيد',
  patients: 'ملفات المرضى',
  invoices: 'الحسابات',
  assistant: 'المساعد الطبي',
  admin: 'إدارة العيادة',
}

const allDays = ['Sat', 'Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri']

export function Settings() {
  const { hasPermission } = useAuth()
  // PLAN.md 1.4: user management and clinic settings are gated by the admin permission (the Admin role carries it
  // by default), not by the role name itself — a customised account may hold one without the other.
  const admin = hasPermission('admin')
  return (
    <>
      <div className="page-head"><h1>الإعدادات</h1></div>
      <div className="stack" style={{ gap: 16 }}>
        <ClinicForm editable={admin} />
        {admin && <UsersCard />}
        {admin && <AuditLogCard />}
        {admin && <AiLogCard />}
      </div>
    </>
  )
}

function ClinicForm({ editable }: { editable: boolean }) {
  const [c, setC] = useState<ClinicSettings | null>(null)
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    api<ClinicSettings>('/api/clinic').then(setC).catch((e) => setMsg({ ok: false, text: errorMessage(e) }))
  }, [])
  if (!c) return <div className="loading"><span className="spinner dark lg" /></div>

  const set = (k: keyof ClinicSettings) => (e: { target: { value: string } }) => setC({ ...c, [k]: e.target.value })
  const days = c.workDays.split(',').filter(Boolean)
  const toggleDay = (d: string) =>
    setC({ ...c, workDays: allDays.filter((x) => (x === d ? !days.includes(d) : days.includes(x))).join(',') })

  async function save(e: FormEvent) {
    e.preventDefault()
    setSaving(true)
    try {
      await api('/api/clinic', {
        method: 'PUT',
        body: { ...c!, consultationFee: Number(c!.consultationFee), followUpFee: Number(c!.followUpFee), avgMinutesPerPatient: Number(c!.avgMinutesPerPatient), dataRetentionDays: Number(c!.dataRetentionDays) },
      })
      setMsg({ ok: true, text: 'تم حفظ الإعدادات' })
    } catch (err) {
      setMsg({ ok: false, text: errorMessage(err) })
    } finally {
      setSaving(false)
    }
  }

  return (
    <form className="card stack" style={{ gap: 18 }} onSubmit={save}>
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="card-title"><Icon name="stethoscope" /><h2>بيانات العيادة</h2></div>
      </div>
      {msg && <div className={`alert ${msg.ok ? 'success' : 'error'}`}><Icon name={msg.ok ? 'check' : 'alert'} /> {msg.text}</div>}
      <fieldset disabled={!editable} style={{ border: 0, padding: 0, margin: 0 }} className="stack">
        <div className="section-title">البيانات العامة</div>
        <div className="grid cols-3">
          <div className="field"><label>اسم العيادة</label><input className="input" value={c.name} onChange={set('name')} /></div>
          <div className="field"><label>اسم الطبيب</label><input className="input" value={c.doctorName} onChange={set('doctorName')} /></div>
          <div className="field"><label>التخصص</label><input className="input" value={c.specialty} onChange={set('specialty')} /></div>
          <div className="field"><label>الهاتف</label><input className="input" dir="ltr" value={c.phone ?? ''} onChange={set('phone')} /></div>
          <div className="field"><label>العنوان</label><input className="input" value={c.address ?? ''} onChange={set('address')} /></div>
          <div className="field"><label>رابط الموقع</label><input className="input" dir="ltr" value={c.slug} onChange={set('slug')} /></div>
          {/* PLAN.md 10.7: the name that appears in the app, the public page and the printouts. */}
                    <div className="field"><label>الاسم المعروض (في التطبيق والطباعة)</label><input className="input" value={c.displayName ?? ''} onChange={set('displayName')} placeholder={c.name} maxLength={80} /></div>
                    {/* PLAN.md 10.12: prescription header fields. */}
                    <div className="field"><label>مؤهلات الطبيب (للروشتة)</label><input className="input" value={c.qualifications ?? ''} onChange={set('qualifications')} placeholder="مثال: ماجستير الباطنة، زمالة القلب" maxLength={120} /></div>
                    <div className="field"><label>مواعيد العمل (للروشتة)</label><input className="input" value={c.workingHoursText ?? ''} onChange={set('workingHoursText')} placeholder="مثال: السبت–الأربعاء ٥–١٠ م، الجمعة راحة" maxLength={120} /></div>
                  </div>
        <div className="field"><label>نبذة عن الطبيب</label><textarea className="input" value={c.about ?? ''} onChange={set('about')} /></div>

        {/* PLAN.md 10.7: the clinic logo — shown in the sidebar, the public page and the printouts. */}
        <div className="field">
          <label>لوجو العيادة (PNG أو JPG، حد أقصى 2 ميجابايت)</label>
          <LogoUpload
            editable={editable}
            slug={c.slug}
            hasLogo={!!c.hasLogo}
            onUploaded={() => { setC({ ...c, hasLogo: true }); setMsg({ ok: true, text: 'تم رفع اللوجو' }) }}
            onError={(t) => setMsg({ ok: false, text: t })}
          />
        </div>

        <div className="section-title mt">الأسعار والمواعيد</div>
        <div className="grid cols-4">
          <div className="field"><label>سعر الكشف</label><input className="input" value={c.consultationFee} onChange={set('consultationFee')} inputMode="decimal" /></div>
          <div className="field"><label>سعر الاستشارة</label><input className="input" value={c.followUpFee} onChange={set('followUpFee')} inputMode="decimal" /></div>
          <div className="field"><label>مدة الكشف (دقيقة)</label><input className="input" value={c.avgMinutesPerPatient} onChange={set('avgMinutesPerPatient')} inputMode="numeric" /></div>
          <div className="field">
            <label>الحجز الإلكتروني</label>
            <select className="input" value={c.onlineBookingEnabled ? '1' : '0'} onChange={(e) => setC({ ...c, onlineBookingEnabled: e.target.value === '1' })}>
              <option value="1">مفعّل</option>
              <option value="0">معطّل</option>
            </select>
          </div>
          <div className="field"><label>بداية العمل</label><input className="input" type="time" value={c.workStart} onChange={set('workStart')} /></div>
          <div className="field"><label>نهاية العمل</label><input className="input" type="time" value={c.workEnd} onChange={set('workEnd')} /></div>
          {/* PLAN_ADDENDUM.md 9.3: how long a logically deleted patient file survives before final removal. */}
          <div className="field">
            <label>مدة احتفاظ الملفات المحذوفة (يوم)</label>
            <input className="input" value={c.dataRetentionDays} onChange={set('dataRetentionDays')} inputMode="numeric" min={1} max={3650} />
          </div>
        </div>
        <div className="field">
          <label>أيام العمل</label>
          <div className="chips">
            {allDays.map((d) => (
              <button type="button" key={d} className={`chip ${days.includes(d) ? 'active' : ''}`} onClick={() => toggleDay(d)}>{dayLabel[d]}</button>
            ))}
          </div>
        </div>
        {editable && <div className="row end"><button className="btn" disabled={saving}>{saving ? <span className="spinner" /> : <Icon name="check" />} حفظ التغييرات</button></div>}
      </fieldset>
    </form>
  )
}

/**
 * PLAN.md 10.7: uploads the clinic logo to POST /api/clinic/logo (admin only). The preview re-reads the
 * public logo URL with a cache-buster, so a fresh upload shows immediately.
 */
function LogoUpload({ editable, slug, hasLogo, onUploaded, onError }: {
  editable: boolean
  slug: string
  hasLogo: boolean
  onUploaded: () => void
  onError: (text: string) => void
}) {
  const [busy, setBusy] = useState(false)
  const [stamp, setStamp] = useState(0)

  async function upload(e: { target: HTMLInputElement }) {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setBusy(true)
    try {
      const form = new FormData()
      form.append('file', file)
      await api('/api/clinic/logo', { method: 'POST', form })
      setStamp(Date.now())
      onUploaded()
    } catch (err) {
      onError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="row" style={{ gap: 12 }}>
      {hasLogo && (
        <img
          src={`/api/public/clinics/${slug}/logo?v=${stamp}`}
          alt="اللوجو"
          style={{ width: 56, height: 56, objectFit: 'contain', borderRadius: 10, background: '#fff', border: '1px solid var(--line)' }}
        />
      )}
      {editable && (
        <label className="btn ghost sm">
          {busy ? <span className="spinner" /> : <Icon name="upload" size={15} />} {hasLogo ? 'تغيير اللوجو' : 'رفع اللوجو'}
          <input type="file" accept=".png,.jpg,.jpeg" style={{ display: 'none' }} onChange={upload} disabled={busy} />
        </label>
      )}
      {!hasLogo && !editable && <span className="muted small">لم يتم رفع لوجو بعد</span>}
    </div>
  )
}

function UsersCard() {
  const { session } = useAuth()
  const [users, setUsers] = useState<UserRow[]>([])
  const [error, setError] = useState('')
  const [adding, setAdding] = useState(false)
  const [resetting, setResetting] = useState<UserRow | null>(null)
  const [permissioning, setPermissioning] = useState<UserRow | null>(null)
  const [deleting, setDeleting] = useState<UserRow | null>(null)

  const load = useCallback(() => {
    api<UserRow[]>('/api/users').then(setUsers).catch((e) => setError(errorMessage(e)))
  }, [])
  useEffect(load, [load])

  async function update(u: UserRow, patch: Partial<UserRow>) {
    try {
      const next = { ...u, ...patch }
      await api(`/api/users/${u.id}`, { method: 'PUT', body: { fullName: next.fullName, role: next.role, isActive: next.isActive } })
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <div className="card flush">
      <div className="card-head">
        <div className="card-title"><Icon name="users" /><h2>المستخدمون والصلاحيات</h2></div>
        <button className="btn sm" onClick={() => setAdding(true)}><Icon name="userPlus" size={15} /> مستخدم جديد</button>
      </div>
      {error && <div className="alert error" style={{ margin: 16 }}><Icon name="alert" /> {error}</div>}
      <div className="table-wrap">
        <table className="table">
          <thead><tr><th>الاسم</th><th>اسم المستخدم</th><th>الصلاحية</th><th>الحالة</th><th></th></tr></thead>
          <tbody>
            {users.map((u) => (
              <tr key={u.id}>
                <td>
                  <div className="row tight">
                    <span className="avatar" style={{ background: 'var(--primary-50)', color: 'var(--primary)', width: 32, height: 32, fontSize: 13 }}>{u.fullName.trim().charAt(0)}</span>
                    <span className="cell-main">{u.fullName}</span>
                  </div>
                </td>
                <td><span className="ltr">{u.username}</span></td>
                <td>
                  <select className="input" style={{ width: 150, height: 34 }} value={u.role} disabled={u.id === session?.user.id} onChange={(e) => update(u, { role: e.target.value as Role })}>
                    {(['Admin', 'Doctor', 'Reception'] as Role[]).map((r) => <option key={r} value={r}>{roleLabel[r]}</option>)}
                  </select>
                </td>
                <td><span className={`badge ${u.isActive ? 'green' : 'gray'}`}>{u.isActive ? 'نشط' : 'موقوف'}</span></td>
                <td>
                  <div className="actions">
                    <button className="btn sm ghost" onClick={() => setPermissioning(u)}><Icon name="shield" size={15} /> الصلاحيات{u.isExplicit ? ' · مخصصة' : ''}</button>
                    <button className="btn sm ghost" onClick={() => setResetting(u)}><Icon name="lock" size={15} /> كلمة المرور</button>
                    {u.id !== session?.user.id && (
                      <button className={`btn sm ${u.isActive ? 'danger' : 'soft'}`} onClick={() => update(u, { isActive: !u.isActive })}>{u.isActive ? 'إيقاف' : 'تفعيل'}</button>
                    )}
                    {u.id !== session?.user.id && (
                      <button className="btn sm danger" onClick={() => setDeleting(u)}><Icon name="userX" size={15} /> حذف</button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {adding && <AddUser onClose={() => setAdding(false)} onSaved={() => { setAdding(false); load() }} />}
      {resetting && <ResetPassword user={resetting} onClose={() => setResetting(null)} />}
      {permissioning && <PermissionsModal user={permissioning} onClose={() => setPermissioning(null)} onChanged={load} />}
      {deleting && <DeleteUser user={deleting} onClose={() => setDeleting(null)} onDone={() => { setDeleting(null); load() }} />}
    </div>
  )
}

/**
 * PLAN.md 10.6: logical delete of a staff account. The API renames the account so the username can be reused,
 * voids its tokens, and keeps its visits and audit rows; the account just disappears from every list.
 */
function DeleteUser({ user, onClose, onDone }: { user: UserRow; onClose: () => void; onDone: () => void }) {
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function remove(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    try {
      await api(`/api/users/${user.id}`, { method: 'DELETE' })
      onDone()
    } catch (err) {
      setError(errorMessage(err))
      setBusy(false)
    }
  }

  return (
    <Modal title={`حذف المستخدم · ${user.fullName}`} onClose={onClose}>
      <form className="stack" style={{ gap: 16 }} onSubmit={remove}>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        <p>
          سيُحذف حساب <strong>{user.fullName}</strong> <span className="ltr">({user.username})</span>: لن يستطيع
          الدخول ولن يظهر في القوائم، وستبقى زياراته وسجلاته محفوظة كما هي، ويمكن إعادة استخدام اسم المستخدم لحساب جديد.
        </p>
        <div className="row end">
          <button type="button" className="btn ghost" onClick={onClose}>إلغاء</button>
          <button className="btn danger" disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="userX" />} حذف نهائي</button>
        </div>
      </form>
    </Modal>
  )
}

function AddUser({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ fullName: '', username: '', password: '', role: 'Reception' as Role })
  const [error, setError] = useState('')

  async function save(e: FormEvent) {
    e.preventDefault()
    try {
      await api('/api/users', { body: f })
      onSaved()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <Modal title="مستخدم جديد" onClose={onClose}>
      <form className="stack" style={{ gap: 16 }} onSubmit={save}>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        <div className="field"><label>الاسم</label><input className="input" value={f.fullName} onChange={(e) => setF({ ...f, fullName: e.target.value })} required autoFocus /></div>
        <div className="grid cols-2">
          <div className="field"><label>اسم المستخدم</label><input className="input" dir="ltr" value={f.username} onChange={(e) => setF({ ...f, username: e.target.value })} required /></div>
          <div className="field"><label>كلمة مرور مؤقتة</label><input className="input" dir="ltr" type="password" value={f.password} onChange={(e) => setF({ ...f, password: e.target.value })} required minLength={10} /><div className="muted small">سيُطلب من المستخدم تغييرها عند أول تسجيل دخول</div></div>
        </div>
        <div className="field">
          <label>الصلاحية</label>
          <div className="segmented">
            {(['Reception', 'Doctor', 'Admin'] as Role[]).map((r) => (
              <button key={r} type="button" className={f.role === r ? 'active' : ''} onClick={() => setF({ ...f, role: r })}>{roleLabel[r]}</button>
            ))}
          </div>
        </div>
        <div className="row end"><button className="btn"><Icon name="check" /> إضافة</button></div>
      </form>
    </Modal>
  )
}

function ResetPassword({ user, onClose }: { user: UserRow; onClose: () => void }) {
  const [pwd, setPwd] = useState('')
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)

  async function save(e: FormEvent) {
    e.preventDefault()
    try {
      await api(`/api/users/${user.id}/reset-password`, { body: { newPassword: pwd } })
      setMsg({ ok: true, text: 'تم تغيير كلمة المرور' })
      setPwd('')
    } catch (err) {
      setMsg({ ok: false, text: errorMessage(err) })
    }
  }

  return (
    <Modal title={`تغيير كلمة المرور · ${user.fullName}`} onClose={onClose}>
      <form className="stack" style={{ gap: 16 }} onSubmit={save}>
        {msg && <div className={`alert ${msg.ok ? 'success' : 'error'}`}><Icon name={msg.ok ? 'check' : 'alert'} /> {msg.text}</div>}
        <div className="field"><label>كلمة المرور الجديدة</label><input className="input" dir="ltr" type="password" value={pwd} onChange={(e) => setPwd(e.target.value)} required minLength={10} autoFocus /></div>
        <div className="row end"><button className="btn"><Icon name="check" /> حفظ</button></div>
      </form>
    </Modal>
  )
}

/**
 * PLAN.md 1.4: the per-user permission editor. The checkboxes start from the set that is in force — the
 * account's explicit set when the admin made one, its role's defaults otherwise. Saving stores an explicit set;
 * "استعادة الافتراضي" sends null back to the role's defaults. The API applies the change within its 30 second
 * validation window, without asking the user to sign in again.
 */
function PermissionsModal({ user, onClose, onChanged }: { user: UserRow; onClose: () => void; onChanged: () => void }) {
  const [all, setAll] = useState<string[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [explicit, setExplicit] = useState(false)
  const [error, setError] = useState('')
  const [msg, setMsg] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api<{ role: string; isExplicit: boolean; effective: string[]; defaults: string[]; all: string[] }>(
      `/api/users/${user.id}/permissions`,
    )
      .then((d) => {
        setAll(d.all)
        setSelected(d.effective)
        setExplicit(d.isExplicit)
      })
      .catch((e) => setError(errorMessage(e)))
  }, [user.id])

  const toggle = (key: string) =>
    setSelected((s) => (s.includes(key) ? s.filter((k) => k !== key) : [...s, key]))

  async function save(permissions: string[] | null) {
    setBusy(true)
    setError('')
    setMsg('')
    try {
      const d = await api<{ isExplicit: boolean; effective: string[] }>(`/api/users/${user.id}/permissions`, {
        method: 'PUT',
        body: { permissions },
      })
      setSelected(d.effective)
      setExplicit(d.isExplicit)
      setMsg(permissions === null ? 'تمت استعادة صلاحيات الدور الافتراضية' : 'تم حفظ الصلاحيات')
      onChanged()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`الصلاحيات · ${user.fullName}`} onClose={onClose}>
      <div className="stack" style={{ gap: 16 }}>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        {msg && <div className="alert success"><Icon name="check" /> {msg}</div>}
        <div className="muted small">
          {explicit
            ? 'هذا المستخدم يملك صلاحيات مخصصة لا تتبع دورته — التغيير هنا يحدّث صلاحياته المخصصة.'
            : 'بستعمل حالياً الصلاحيات الافتراضية لدوره. الحفظ يثبّت اختيارك الخاص ويُخرج حسابته عن تلك الافتراضية.'}
        </div>
        <div className="stack" style={{ gap: 8 }}>
          {all.map((key) => (
            <label key={key} style={{ display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer' }}>
              <input type="checkbox" checked={selected.includes(key)} onChange={() => toggle(key)} />
              <span>{PERMISSION_LABELS[key] ?? key}</span>
            </label>
          ))}
        </div>
        <div className="row between" style={{ gap: 12 }}>
          <button type="button" className="btn ghost sm" disabled={busy} onClick={() => save(null)}>
            <Icon name="undo" size={15} /> استعادة الافتراضي
          </button>
          <button className="btn" disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="check" />} حفظ</button>
        </div>
      </div>
    </Modal>
  )
}

interface AuditRow {
  id: number
  atUtc: string
  userId?: number | null
  userName?: string | null
  action: string
  entity: string
  entityId?: string | null
  ipAddress?: string | null
}

/** PLAN_ADDENDUM.md 7.4: Arabic labels for what the trail records (action names come from the API verbatim). */
const AUDIT_ACTIONS: Record<string, string> = {
  Added: 'إنشاء',
  Modified: 'تعديل',
  Deleted: 'حذف',
  Viewed: 'فتح ملف',
  Login: 'تسجيل دخول',
  Uploaded: 'رفع ملف',
  Reindexed: 'إعادة فهرسة',
}

const AUDIT_ENTITIES: Record<string, string> = {
  Patient: 'مريض',
  Visit: 'زيارة',
  Invoice: 'فاتورة',
  Appointment: 'موعد',
  User: 'مستخدم',
  Clinic: 'عيادة',
  AiSettings: 'إعدادات الذكاء',
  Library: 'المكتبة',
}

/**
 * PLAN_ADDENDUM.md 7.4: the clinic's read-only event trail — who did what on which entity, when and from which
 * IP. It filters by date range and by user; it never holds medical content (the API is what guarantees that).
 */
function AuditLogCard() {
  const [users, setUsers] = useState<UserRow[]>([])
  const [rows, setRows] = useState<AuditRow[] | null>(null)
  const [error, setError] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [userId, setUserId] = useState('')

  useEffect(() => {
    api<UserRow[]>('/api/users').then(setUsers).catch(() => undefined)
  }, [])

  const load = useCallback(() => {
    const p = new URLSearchParams()
    if (from) p.set('fromUtc', new Date(from).toISOString())
    if (to) p.set('toUtc', new Date(to).toISOString())
    if (userId) p.set('userId', userId)
    api<{ items: AuditRow[] }>(`/api/audit-logs?${p.toString()}`)
      .then((d) => setRows(d.items))
      .catch((e) => setError(errorMessage(e)))
  }, [from, to, userId])

  useEffect(load, [load])

  return (
    <div className="card stack" style={{ gap: 14 }}>
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="card-title"><Icon name="shield" /><h2>سجل الأحداث</h2></div>
        <button className="btn ghost sm" onClick={load}><Icon name="refresh" size={16} /> تحديث</button>
      </div>
      <div className="muted small">من سجّل ماذا على أي كيان، ومتى ومن أي عنوان — قراءة فقط، ولا يُسجّل فيه أي محتوى طبي.</div>
      <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>من تاريخ</label>
          <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>إلى تاريخ</label>
          <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
        <div className="field" style={{ maxWidth: 220 }}>
          <label>المستخدم</label>
          <select className="input" value={userId} onChange={(e) => setUserId(e.target.value)}>
            <option value="">الكل</option>
            {users.map((u) => <option key={u.id} value={u.id}>{u.fullName}</option>)}
          </select>
        </div>
      </div>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      {rows == null && !error ? (
        <div className="loading"><span className="spinner dark" /></div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="table">
            <thead>
              <tr><th>الوقت</th><th>المستخدم</th><th>الإجراء</th><th>الكيان</th><th>العنوان</th></tr>
            </thead>
            <tbody>
              {(rows ?? []).map((r) => (
                <tr key={r.id}>
                  <td>{new Date(r.atUtc).toLocaleString('ar-EG')}</td>
                  <td>{r.userName ?? '—'}</td>
                  <td>
                    {AUDIT_ACTIONS[r.action] ?? r.action}
                    {r.entityId ? <span className="muted"> #{r.entityId}</span> : null}
                  </td>
                  <td>{AUDIT_ENTITIES[r.entity] ?? r.entity}</td>
                  <td dir="ltr">{r.ipAddress ?? '—'}</td>
                </tr>
              ))}
              {(rows ?? []).length === 0 && <tr><td colSpan={5} className="muted">لا أحداث مطابقة</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

/** One row of GET api/audit-logs/ai (PLAN_ADDENDUM.md 9.2). */
interface AiLogRow {
  id: number
  atUtc: string
  userId?: number | null
  patientId?: number | null
  kind: string
  sources: string
  durationMs: number
  questionText?: string | null
  answerText?: string | null
  userName?: string | null
}

const AI_KINDS: Record<string, string> = {
  ask: 'سؤال',
  summary: 'ملخص الحالة',
  differential: 'التشخيص التفريقي',
  'draft-report': 'تقرير',
  'draft-referral': 'تحويل',
}

/** The Sources column is a JSON array of source titles (never content); render it as one readable line. */
function aiSourceLine(text: string): string {
  try {
    const arr: unknown = JSON.parse(text)
    if (Array.isArray(arr) && arr.length > 0) return arr.join('، ')
  } catch { /* not JSON — show the raw value */ }
  return text || '—'
}

/**
 * PLAN_ADDENDUM.md 9.2: this clinic's AI interaction log — who asked about whom, the sources used and how long it
 * took. Read-only; the question/answer text appears only when text logging is enabled for this clinic.
 */
function AiLogCard() {
  const [users, setUsers] = useState<UserRow[]>([])
  const [rows, setRows] = useState<AiLogRow[] | null>(null)
  const [error, setError] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [userId, setUserId] = useState('')

  useEffect(() => {
    api<UserRow[]>('/api/users').then(setUsers).catch(() => undefined)
  }, [])

  const load = useCallback(() => {
    const p = new URLSearchParams()
    if (from) p.set('fromUtc', new Date(from).toISOString())
    if (to) p.set('toUtc', new Date(to).toISOString())
    if (userId) p.set('userId', userId)
    api<{ items: AiLogRow[] }>(`/api/audit-logs/ai?${p.toString()}`)
      .then((d) => setRows(d.items))
      .catch((e) => setError(errorMessage(e)))
  }, [from, to, userId])

  useEffect(load, [load])

  return (
    <div className="card stack" style={{ gap: 14 }}>
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="card-title"><Icon name="sparkles" /><h2>سجل استخدام الذكاء</h2></div>
        <button className="btn ghost sm" onClick={load}><Icon name="refresh" size={16} /> تحديث</button>
      </div>
      <div className="muted small">كل طلبات الذكاء في عيادتك: النوع، المصادر المستخدمة، والمدة — ويُعرض نص السؤال والإجابة فقط إن كان مفتاح التسجيل مفعّلًا.</div>
      <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>من تاريخ</label>
          <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>إلى تاريخ</label>
          <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
        <div className="field" style={{ maxWidth: 220 }}>
          <label>المستخدم</label>
          <select className="input" value={userId} onChange={(e) => setUserId(e.target.value)}>
            <option value="">الكل</option>
            {users.map((u) => <option key={u.id} value={u.id}>{u.fullName}</option>)}
          </select>
        </div>
      </div>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      {rows == null && !error ? (
        <div className="loading"><span className="spinner dark" /></div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="table">
            <thead>
              <tr><th>الوقت</th><th>المستخدم</th><th>النوع</th><th>المصادر</th><th>المدة</th></tr>
            </thead>
            <tbody>
              {(rows ?? []).map((r) => (
                <tr key={r.id}>
                  <td>{new Date(r.atUtc).toLocaleString('ar-EG')}</td>
                  <td>{r.userName ?? '—'}</td>
                  <td>
                    {AI_KINDS[r.kind] ?? r.kind}
                    {(r.questionText || r.answerText) && (
                      <details className="small">
                        <summary style={{ cursor: 'pointer' }}>عرض النص</summary>
                        {r.questionText && <p className="muted" dir="auto" style={{ whiteSpace: 'pre-wrap', margin: '6px 0 0' }}>السؤال: {r.questionText}</p>}
                        {r.answerText && <p className="muted" dir="auto" style={{ whiteSpace: 'pre-wrap', margin: '6px 0 0' }}>الإجابة: {r.answerText}</p>}
                      </details>
                    )}
                  </td>
                  <td className="muted small">{aiSourceLine(r.sources)}</td>
                  <td dir="ltr">{(r.durationMs / 1000).toFixed(1)} ث</td>
                </tr>
              ))}
              {(rows ?? []).length === 0 && <tr><td colSpan={5} className="muted">لا طلبات مطابقة</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
