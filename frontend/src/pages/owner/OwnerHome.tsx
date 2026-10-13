import { useCallback, useEffect, useState, type ChangeEvent, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, errorMessage, type Role } from '../../api'
import { useAuth } from '../../auth'
import { Icon, LogoMark, type IconName } from '../../components/Icon'
import { Modal } from '../../components/Modal'
import { fmtShortDate, money, roleLabel } from '../../format'
import { Library } from '../Library'

/** One row of GET api/owner/clinics (PLAN.md 1.1). */
interface ClinicNumbers {
  id: number
  name: string
  slug: string
  doctorName: string
  specialty: string
  phone?: string | null
  address?: string | null
  createdAtUtc: string
  onlineBookingEnabled: boolean
  staff: { total: number; active: number }
  patients: number
  visits: number
  lastVisitAt?: string | null
  appointments: number
  invoices: number
  revenue: number
  /** PLAN.md 10.5: the subscription the owner sees per row (plan, state, effective end, days left). */
  plan: string
  subscriptionStatus: string
  endsAtUtc?: string | null
  daysRemaining?: number | null
}

interface StaffRow { id: number; fullName: string; username: string; role: Role; isActive: boolean; createdAtUtc: string }

interface ClinicDetail {
  clinic: ClinicNumbers & { about?: string | null; timeZoneId?: string }
  numbers: ClinicNumbers
  staff: StaffRow[]
}

/** The shape of GET api/owner/ai-settings (PLAN.md 1.2): appsettings defaults, a global row, one override row per clinic. */
interface AiSettingsRow { baseUrl: string; apiKey: string; model: string; logQuestionText: boolean }
interface AiScopeSettings extends AiSettingsRow { id: number; clinicId: number | null; updatedAtUtc: string }
interface AiSettingsView {
  defaults: AiSettingsRow
  global: AiScopeSettings | null
  clinics: { id: number; name: string; settings: AiScopeSettings | null }[]
}

/** PLAN.md 10.5: Arabic labels (and badge tones) for the clinic's plan and subscription state. */
const planLabel: Record<string, string> = { Basic: 'أساسية', Pro: 'احترافية' }
const subStatusLabel: Record<string, string> = { Trial: 'تجريبية', Active: 'نشطة', Suspended: 'موقوفة', Expired: 'منتهية' }
const subStatusTone: Record<string, string> = { Trial: 'amber', Active: 'green', Suspended: 'gray', Expired: 'red' }

function Stat({ icon, tone, label, value, sub }: { icon: IconName; tone: string; label: string; value: string | number; sub?: string }) {
  return (
    <div className="card stat-card">
      <span className={`stat-icon ${tone}`}><Icon name={icon} size={22} /></span>
      <div>
        <div className="stat-label">{label}</div>
        <div className="stat-value">{value}</div>
        {sub && <div className="stat-sub">{sub}</div>}
      </div>
    </div>
  )
}

/**
 * The owner panel (PLAN.md 1.1). It lists every clinic on the installation with the numbers behind it — the one
 * screen a clinic account can never open, because a clinic account's own shell lives under /app.
 */
export function OwnerHome() {
  const { session, logout } = useAuth()
  const navigate = useNavigate()
  const [clinics, setClinics] = useState<ClinicNumbers[] | null>(null)
  const [error, setError] = useState('')
  const [openId, setOpenId] = useState<number | null>(null)
  /** PLAN.md 1.3: the medical library moved into the owner's panel; it switches the panel's view, not the route. */
  const [view, setView] = useState<'clinics' | 'library' | 'audit' | 'ai'>('clinics')
  /** PLAN.md 10.1: the "new clinic" registration dialog. */
  const [showNew, setShowNew] = useState(false)
  /** PLAN.md 10.5: the month's recorded payments across every clinic, and the "expiring within 7 days" filter. */
  const [monthRevenue, setMonthRevenue] = useState(0)
  const [expiringOnly, setExpiringOnly] = useState(false)

  const load = useCallback(() => {
    setError('')
    // PLAN.md 10.5: the list arrives wrapped with the month's revenue total; expiringDays asks the backend
    // to keep only the rows whose subscription ends within the next seven days.
    api<{ items: ClinicNumbers[]; monthRevenue: number }>('/api/owner/clinics' + (expiringOnly ? '?expiringDays=7' : ''))
      .then((d) => { setClinics(d.items); setMonthRevenue(d.monthRevenue) })
      .catch((e) => setError(errorMessage(e)))
  }, [expiringOnly])
  useEffect(load, [load])

  const totals = (clinics ?? []).reduce(
    (sum, c) => ({
      staff: sum.staff + c.staff.total,
      patients: sum.patients + c.patients,
      visits: sum.visits + c.visits,
      revenue: sum.revenue + c.revenue,
    }),
    { staff: 0, patients: 0, visits: 0, revenue: 0 },
  )

  return (
    <div className="shell">
      <aside className="sidebar no-print">
        <div className="brand">
          <LogoMark />
          <div>
            <div className="brand-name">TechCare Clinic</div>
            <div className="brand-sub">لوحة المالك</div>
          </div>
        </div>
        <div className="nav-label">المنصة</div>
        <button type="button" className={`nav-item-btn${view === 'clinics' ? ' active' : ''}`} onClick={() => setView('clinics')}>
          <Icon name="activity" /> العيادات
        </button>
        <button type="button" className={`nav-item-btn${view === 'library' ? ' active' : ''}`} onClick={() => setView('library')}>
          <Icon name="book" /> المكتبة الطبية
        </button>
        <button type="button" className={`nav-item-btn${view === 'audit' ? ' active' : ''}`} onClick={() => setView('audit')}>
          <Icon name="shield" /> سجل الأحداث
        </button>
        <button type="button" className={`nav-item-btn${view === 'ai' ? ' active' : ''}`} onClick={() => setView('ai')}>
          <Icon name="sparkles" /> استخدام الذكاء
        </button>
        <div className="spacer" />
        <div className="user-box">
          <span className="avatar">{session?.user.fullName.trim().charAt(0)}</span>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div className="name">{session?.user.fullName}</div>
            <div className="role">{session && roleLabel[session.user.role]}</div>
          </div>
          <button className="icon-btn" title="تسجيل الخروج" onClick={() => { logout(); navigate('/owner/login') }}>
            <Icon name="logout" />
          </button>
        </div>
      </aside>
      <div className="main">
        <header className="topbar no-print">
          <div className="date"><Icon name="shield" size={16} /> مالك النظام · كل العيادات</div>
          {view === 'clinics' && (
            <>
              <button className="btn sm" onClick={() => setShowNew(true)}><Icon name="plus" size={16} /> عيادة جديدة</button>
              <button className="btn ghost sm" onClick={load}><Icon name="refresh" size={16} /> تحديث</button>
            </>
          )}
        </header>
        <main className="content">
          {view === 'library' ? <Library /> : view === 'audit' ? <OwnerAuditLog clinics={clinics ?? []} /> : view === 'ai' ? <OwnerAiUsage clinics={clinics ?? []} /> : (<><div className="page-head">
            <div>
              <h1>عيادات المنصة</h1>
              <div className="muted small">هذه الشاشة للمالك فقط، ولا يراها أي حساب داخل العيادات</div>
            </div>
          </div>
          {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}
          {!clinics ? <div className="loading"><span className="spinner dark lg" /></div> : (
            <>
              <div className="grid cols-5">
                <Stat icon="activity" tone="blue" label="عدد العيادات" value={clinics.length} />
                <Stat icon="user" tone="teal" label="حسابات الفريق" value={totals.staff} />
                <Stat icon="users" tone="amber" label="ملفات المرضى" value={totals.patients} />
                <Stat icon="cash" tone="green" label="إجمالي التحصيل" value={money(totals.revenue)} sub={`${totals.visits} زيارة`} />
                <Stat icon="cash" tone="violet" label="إيراد الشهر" value={money(monthRevenue)} sub="مدفوعات الاشتراك المسجلة" />
              </div>
              <div className="card mt">
                <div className="card-head">
                  <div className="card-title"><Icon name="activity" /><h3>العيادات</h3></div>
                  <label className="row tight small" style={{ cursor: 'pointer', gap: 6 }}>
                    <input type="checkbox" checked={expiringOnly} onChange={(e) => setExpiringOnly(e.target.checked)} />
                    تنتهي خلال 7 أيام
                  </label>
                </div>
                <div className="table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>العيادة</th><th>الرابط</th><th>الاشتراك</th><th>الفريق</th><th>المرضى</th><th>الزيارات</th>
                        <th>الفواتير</th><th>التحصيل</th><th>آخر زيارة</th><th></th>
                      </tr>
                    </thead>
                    <tbody>
                      {clinics.map((c) => (
                        <tr key={c.id}>
                          <td>
                            <div className="strong">{c.name}</div>
                            <div className="muted small">{c.doctorName} · {c.specialty}</div>
                          </td>
                          <td>
                            <a className="row tight" href={`/c/${c.slug}`} target="_blank" rel="noreferrer">
                              /{c.slug} <Icon name="external" size={14} />
                            </a>
                          </td>
                          <td>
                            <span className={`badge ${subStatusTone[c.subscriptionStatus] ?? 'gray'}`}>
                              {subStatusLabel[c.subscriptionStatus] ?? c.subscriptionStatus}
                            </span>
                            <div className="muted small" style={{ marginTop: 4 }}>
                              {planLabel[c.plan] ?? c.plan}
                              {c.endsAtUtc ? ` · ينتهي ${fmtShortDate(c.endsAtUtc)}` : ''}
                              {c.daysRemaining != null ? ` (باقي ${c.daysRemaining} يوم)` : ''}
                            </div>
                          </td>
                          <td>{c.staff.active} / {c.staff.total}</td>
                          <td>{c.patients}</td>
                          <td>{c.visits}</td>
                          <td>{c.invoices}</td>
                          <td>{money(c.revenue)}</td>
                          <td>{c.lastVisitAt ? fmtShortDate(c.lastVisitAt) : '—'}</td>
                          <td><button className="btn ghost sm" onClick={() => setOpenId(c.id)}>تفاصيل</button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
              <AiSettingsCard />
            </>
          )}
          </>)}
        </main>
      </div>
      {openId != null && <ClinicDetails id={openId} onClose={() => setOpenId(null)} />}
      {showNew && <NewClinicModal onClose={() => setShowNew(false)} onCreated={load} />}
    </div>
  )
}

/**
 * PLAN.md 10.1 — registers a clinic from the owner panel: the clinic row with its default schedule, plus the
 * admin account that must change its temporary password at the first login (PLAN_ADDENDUM.md 7.5). After the
 * answer the dialog shows the credentials so the owner can hand them over; a repeated slug answers 409.
 */
function NewClinicModal({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [form, setForm] = useState({
    name: '', slug: '', doctorName: '', specialty: '', phone: '', address: '',
    adminUsername: '', adminPassword: '', consultationFee: '', followUpFee: '',
  })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<{ clinic: { name: string; slug: string }; adminUsername: string; adminPassword: string } | null>(null)

  const set = (key: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) =>
    setForm((f) => ({ ...f, [key]: e.target.value }))

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const res = await api<{ clinic: { name: string; slug: string }; adminUsername: string; adminPassword: string }>('/api/owner/clinics', {
        method: 'POST',
        body: {
          name: form.name,
          slug: form.slug,
          doctorName: form.doctorName,
          specialty: form.specialty,
          phone: form.phone || null,
          address: form.address || null,
          adminUsername: form.adminUsername,
          adminPassword: form.adminPassword,
          consultationFee: form.consultationFee === '' ? null : Number(form.consultationFee),
          followUpFee: form.followUpFee === '' ? null : Number(form.followUpFee),
        },
      })
      setResult(res)
      onCreated()
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="عيادة جديدة" onClose={onClose}>
      {result ? (
        <div className="stack" style={{ gap: 12 }}>
          <div className="alert success">أُتمّت إنشاء العيادة. سجّل الأدمن ببياناته من موقع العيادة /{result.clinic.slug} — كلمة المرور المؤقتة يجب أن تُغيَّر عند أول دخول.</div>
          <div className="grid cols-2">
            <Info label="العيادة" value={result.clinic.name} />
            <Info label="الرابط" value={`/${result.clinic.slug}`} />
            <Info label="اسم مستخدم الأدمن" value={result.adminUsername} />
            <Info label="كلمة المرور المؤقتة" value={result.adminPassword} />
          </div>
          <div className="row between">
            <button className="btn ghost" onClick={onClose}>إغلاق</button>
          </div>
        </div>
      ) : (
        <form className="stack" onSubmit={submit} style={{ gap: 12 }}>
          <div className="grid cols-2">
            <div className="field"><label>اسم العيادة *</label><input className="input" value={form.name} onChange={set('name')} required /></div>
            <div className="field"><label>رابط العيادة *</label><input className="input ltr" dir="ltr" value={form.slug} onChange={set('slug')} placeholder="my-clinic-1" required /></div>
            <div className="field"><label>اسم الدكتور *</label><input className="input" value={form.doctorName} onChange={set('doctorName')} required /></div>
            <div className="field"><label>التخصص</label><input className="input" value={form.specialty} onChange={set('specialty')} /></div>
            <div className="field"><label>الهاتف</label><input className="input ltr" dir="ltr" value={form.phone} onChange={set('phone')} /></div>
            <div className="field"><label>العنوان</label><input className="input" value={form.address} onChange={set('address')} /></div>
            <div className="field"><label>اسم مستخدم الأدمن *</label><input className="input ltr" dir="ltr" value={form.adminUsername} onChange={set('adminUsername')} required /></div>
            <div className="field"><label>كلمة المرور المؤقتة * (10 أحرف على الأقل)</label><input className="input ltr" dir="ltr" value={form.adminPassword} onChange={set('adminPassword')} required /></div>
            <div className="field"><label>سعر الكشف (افتراضي 300)</label><input className="input ltr" dir="ltr" type="number" min="0" value={form.consultationFee} onChange={set('consultationFee')} /></div>
            <div className="field"><label>سعر المتابعة (افتراضي 200)</label><input className="input ltr" dir="ltr" type="number" min="0" value={form.followUpFee} onChange={set('followUpFee')} /></div>
          </div>
          <div className="muted small">المواعيد الافتراضية: 5 مساءً حتى 10 مساءً من السبت إلى الخميس، في إطار النسخة الجاهزة — يعيد الأدمن ضبطها من إعدادات العيادة بعد الدخول.</div>
          {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
          <div className="row between">
            <button type="button" className="btn ghost" onClick={onClose}>إلغاء</button>
            <button className="btn" disabled={busy}>{busy ? 'جارٍ الإنشاء…' : 'إنشاء العيادة'}</button>
          </div>
        </form>
      )}
    </Modal>
  )
}

/** One clinic in full: its settings, its numbers and the accounts that can sign in to it. */
function ClinicDetails({ id, onClose }: { id: number; onClose: () => void }) {
  const [data, setData] = useState<ClinicDetail | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    api<ClinicDetail>(`/api/owner/clinics/${id}`).then(setData).catch((e) => setError(errorMessage(e)))
  }, [id])

  return (
    <Modal title={data ? data.clinic.name : 'تفاصيل العيادة'} onClose={onClose} wide>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      {!data ? <div className="loading"><span className="spinner dark lg" /></div> : (
        <div className="stack" style={{ gap: 16 }}>
          <div className="grid cols-3">
            <Info label="الطبيب" value={data.clinic.doctorName} />
            <Info label="التخصص" value={data.clinic.specialty} />
            <Info label="الحجز الإلكتروني" value={data.clinic.onlineBookingEnabled ? 'مفتوح' : 'مغلق'} />
            <Info label="الهاتف" value={data.clinic.phone || '—'} />
            <Info label="العنوان" value={data.clinic.address || '—'} />
            <Info label="تاريخ الإنشاء" value={fmtShortDate(data.clinic.createdAtUtc)} />
          </div>
          <div className="row between">
            <a className="btn ghost sm" href={`/c/${data.clinic.slug}`} target="_blank" rel="noreferrer">
              <Icon name="globe" /> موقع العيادة
            </a>
            <a className="btn ghost sm" href={`/c/${data.clinic.slug}/display`} target="_blank" rel="noreferrer">
              <Icon name="monitor" /> شاشة الانتظار
            </a>
          </div>
          <div>
            <div className="kv"><span>ملفات المرضى</span><span>{data.numbers.patients}</span></div>
            <div className="kv"><span>الزيارات</span><span>{data.numbers.visits}</span></div>
            <div className="kv"><span>المواعيد</span><span>{data.numbers.appointments}</span></div>
            <div className="kv"><span>الفواتير</span><span>{data.numbers.invoices}</span></div>
            <div className="kv"><span>التحصيل</span><span>{money(data.numbers.revenue)}</span></div>
            <div className="kv"><span>آخر زيارة</span><span>{data.numbers.lastVisitAt ? fmtShortDate(data.numbers.lastVisitAt) : '—'}</span></div>
          </div>
          <div>
            <div className="section-title">فريق العمل</div>
            <div className="table-wrap">
              <table className="table">
                <thead><tr><th>الاسم</th><th>اسم المستخدم</th><th>الصلاحية</th><th>الحالة</th><th>أضيف في</th></tr></thead>
                <tbody>
                  {data.staff.map((s) => (
                    <tr key={s.id}>
                      <td>{s.fullName}</td>
                      <td className="ltr">{s.username}</td>
                      <td>{roleLabel[s.role]}</td>
                      <td>{s.isActive ? 'نشط' : 'موقوف'}</td>
                      <td>{fmtShortDate(s.createdAtUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}
    </Modal>
  )
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <div className="field">
      <label>{label}</label>
      <div className="strong">{value}</div>
    </div>
  )
}

/**
 * The AI settings card of the owner panel (PLAN.md 1.2): a global row plus one override row per clinic,
 * stored in the database. LmStudioClient resolves the effective settings for the calling clinic.
 */
function AiSettingsCard() {
  const [view, setView] = useState<AiSettingsView | null>(null)
  const [scope, setScope] = useState<'global' | number>('global')
  const [form, setForm] = useState({ baseUrl: '', apiKey: '', model: '', logQuestionText: false })
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<{ text: string; tone: 'success' | 'error' | 'info' } | null>(null)

  const load = useCallback(() => {
    api<AiSettingsView>('/api/owner/ai-settings').then(setView).catch(() => setView(null))
  }, [])
  useEffect(load, [load])

  // Pre-fill the form with the installation defaults once the first load lands.
  useEffect(() => {
    if (!view) return
    setForm((f) =>
      f.baseUrl && f.model
        ? f
        : { baseUrl: view.defaults.baseUrl, apiKey: view.defaults.apiKey, model: view.defaults.model, logQuestionText: view.defaults.logQuestionText },
    )
  }, [view])

  const rowFor = (s: 'global' | number): AiScopeSettings | null =>
    s === 'global' ? (view?.global ?? null) : view?.clinics.find((c) => c.id === s)?.settings ?? null

  const pickScope = (next: 'global' | number) => {
    setScope(next)
    setNote(null)
    const eff = rowFor(next) ?? view?.global ?? view?.defaults
    setForm({ baseUrl: eff?.baseUrl ?? '', apiKey: eff?.apiKey ?? '', model: eff?.model ?? '', logQuestionText: eff?.logQuestionText ?? false })
  }

  const save = async () => {
    setBusy(true)
    try {
      await api('/api/owner/ai-settings', { method: 'PUT', body: { clinicId: scope === 'global' ? null : scope, ...form } })
      setNote({ text: 'تم الحفظ', tone: 'success' })
      load()
    } catch (e) {
      setNote({ text: errorMessage(e), tone: 'error' })
    }
    setBusy(false)
  }

  const test = async () => {
    setBusy(true)
    setNote({ text: 'جارٍ تجربة الاتصال…', tone: 'info' })
    try {
      const r = await api<{ ok: boolean; model?: string; models?: string[]; error?: string }>('/api/owner/ai-settings/test', {
        method: 'POST',
        body: { clinicId: scope === 'global' ? null : scope },
      })
      setNote(r.ok
        ? { text: `متصل — النموذج الحالي: ${r.model ?? '؟'}`, tone: 'success' }
        : { text: `تعذّر الاتصال: ${r.error ?? ''}`, tone: 'error' })
    } catch (e) {
      setNote({ text: errorMessage(e), tone: 'error' })
    }
    setBusy(false)
  }

  const remove = async () => {
    setBusy(true)
    try {
      await api(`/api/owner/ai-settings/${scope}`, { method: 'DELETE' })
      setNote({ text: 'حُذف إعداد العيادة، فارتد للنطاق الأعلى', tone: 'success' })
      load()
    } catch (e) {
      setNote({ text: errorMessage(e), tone: 'error' })
    }
    setBusy(false)
  }

  const savedRow = view ? rowFor(scope) : null

  return (
    <div className="card mt">
      <div className="card-head">
        <div className="card-title"><Icon name="sparkles" /><h3>إعدادات الذكاء الاصطناعي</h3></div>
        <button className="btn ghost sm" onClick={load}><Icon name="refresh" size={16} /> تحديث</button>
      </div>
      {!view ? <div className="loading"><span className="spinner dark" /></div> : (
        <div className="stack" style={{ gap: 12 }}>
          <div className="muted small">
            الإعدادات محفوظة في قاعدة البيانات: الإعداد العام يعمل لكل العيادات، ولكل عيادة إعداد خاص يتفوق عليه،
            والمساعد يقرأ الإعداد الفعّال تلقائياً حسب عيادة المستخدم.
          </div>
          <div className="field" style={{ maxWidth: 320 }}>
            <label>النطاق</label>
            <select
              value={scope === 'global' ? 'global' : String(scope)}
              onChange={(e) => pickScope(e.target.value === 'global' ? 'global' : Number(e.target.value))}
            >
              <option value="global">الإعداد العام — كل العيادات</option>
              {view.clinics.map((c) => <option key={c.id} value={String(c.id)}>{c.name}</option>)}
            </select>
          </div>
          <div className="grid cols-3">
            <div className="field">
              <label>Base URL</label>
              <input dir="ltr" value={form.baseUrl} onChange={(e) => setForm({ ...form, baseUrl: e.target.value })} placeholder="http://127.0.0.1:1234/v1" />
            </div>
            <div className="field">
              <label>API Key</label>
              <input dir="ltr" value={form.apiKey} onChange={(e) => setForm({ ...form, apiKey: e.target.value })} placeholder="اتركه فارغاً لو لا يحتاج مفتاحاً" />
            </div>
            <div className="field">
              <label>النموذج</label>
              <input dir="ltr" value={form.model} onChange={(e) => setForm({ ...form, model: e.target.value })} placeholder="qwen3.5-9b" />
            </div>
          </div>
          {/* PLAN_ADDENDUM.md 9.2: text logging is a per-scope switch — off means the log keeps kind, sources and timing only. */}
          <label className="check">
            <input type="checkbox" checked={form.logQuestionText} onChange={(e) => setForm({ ...form, logQuestionText: e.target.checked })} />
            تسجيل نص السؤال والإجابة في سجل الاستخدام (عند تعطيله يُسجَّل نوع الطلب والمصادر والمدة فقط)
          </label>
          <div className="row" style={{ gap: 8 }}>
            <button className="btn sm" disabled={busy} onClick={save}><Icon name="check" /> حفظ</button>
            <button className="btn ghost sm" disabled={busy} onClick={test}><Icon name="zap" /> تجربة الاتصال</button>
            {scope !== 'global' && savedRow && (
              <button className="btn ghost sm" disabled={busy} onClick={remove}><Icon name="x" /> حذف إعداد هذه العيادة</button>
            )}
            {savedRow && <span className="muted small">آخر تحديث: {fmtShortDate(savedRow.updatedAtUtc)}</span>}
          </div>
          {note && <div className={`alert ${note.tone}`}>{note.text}</div>}
        </div>
      )}
    </div>
  )
}

interface OwnerAuditRow {
  id: number
  atUtc: string
  clinicId?: number | null
  userId?: number | null
  userName?: string | null
  action: string
  entity: string
  entityId?: string | null
  ipAddress?: string | null
}

const OWNER_AUDIT_ACTIONS: Record<string, string> = {
  Added: 'إنشاء',
  Modified: 'تعديل',
  Deleted: 'حذف',
  Viewed: 'فتح ملف',
  Login: 'تسجيل دخول',
  Uploaded: 'رفع ملف',
  Reindexed: 'إعادة فهرسة',
}

const OWNER_AUDIT_ENTITIES: Record<string, string> = {
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
 * PLAN_ADDENDUM.md 7.4: the installation-wide event trail — the one screen that sees every clinic's rows.
 * Filters by clinic and by date range; read-only, like its clinic-admin counterpart.
 */
function OwnerAuditLog({ clinics }: { clinics: ClinicNumbers[] }) {
  const [rows, setRows] = useState<OwnerAuditRow[] | null>(null)
  const [error, setError] = useState('')
  const [clinic, setClinic] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const load = useCallback(() => {
    const p = new URLSearchParams()
    if (clinic) p.set('clinicId', clinic)
    if (from) p.set('fromUtc', new Date(from).toISOString())
    if (to) p.set('toUtc', new Date(to).toISOString())
    api<{ items: OwnerAuditRow[] }>(`/api/owner/audit-logs?${p.toString()}`)
      .then((d) => setRows(d.items))
      .catch((e) => setError(errorMessage(e)))
  }, [clinic, from, to])

  useEffect(load, [load])

  const clinicName = (id?: number | null) => clinics.find((c) => c.id === id)?.name ?? '—'

  return (
    <div className="card stack" style={{ gap: 14 }}>
      <div className="card-head">
        <div className="card-title"><Icon name="shield" /><h3>سجل أحداث كل العيادات</h3></div>
        <button className="btn ghost sm" onClick={load}><Icon name="refresh" size={16} /> تحديث</button>
      </div>
      <div className="muted small">كل العمليات المسجّلة على مستوى التثبيت — من، ماذا، على أي كيان، متى ومن أي عنوان.</div>
      <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
        <div className="field" style={{ maxWidth: 200 }}>
          <label>العيادة</label>
          <select className="input" value={clinic} onChange={(e) => setClinic(e.target.value)}>
            <option value="">كل العيادات</option>
            {clinics.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </div>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>من تاريخ</label>
          <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>إلى تاريخ</label>
          <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      </div>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      {rows == null && !error ? (
        <div className="loading"><span className="spinner dark" /></div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="table">
            <thead>
              <tr><th>الوقت</th><th>العيادة</th><th>المستخدم</th><th>الإجراء</th><th>الكيان</th><th>العنوان</th></tr>
            </thead>
            <tbody>
              {(rows ?? []).map((r) => (
                <tr key={r.id}>
                  <td>{new Date(r.atUtc).toLocaleString('ar-EG')}</td>
                  <td>{clinicName(r.clinicId)}</td>
                  <td>{r.userName ?? '—'}</td>
                  <td>
                    {OWNER_AUDIT_ACTIONS[r.action] ?? r.action}
                    {r.entityId ? <span className="muted"> #{r.entityId}</span> : null}
                  </td>
                  <td>{OWNER_AUDIT_ENTITIES[r.entity] ?? r.entity}</td>
                  <td dir="ltr">{r.ipAddress ?? '—'}</td>
                </tr>
              ))}
              {(rows ?? []).length === 0 && <tr><td colSpan={6} className="muted">لا أحداث مطابقة</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

/** One row of GET api/owner/audit-logs/ai (PLAN_ADDENDUM.md 9.2). */
interface OwnerAiUsageRow {
  id: number
  atUtc: string
  clinicId?: number | null
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
 * PLAN_ADDENDUM.md 9.2: the installation-wide AI usage log — every clinic's requests, filterable by clinic and
 * date range. The question/answer text is shown only for rows logged with text logging enabled on their scope.
 */
function OwnerAiUsage({ clinics }: { clinics: ClinicNumbers[] }) {
  const [rows, setRows] = useState<OwnerAiUsageRow[] | null>(null)
  const [error, setError] = useState('')
  const [clinic, setClinic] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const load = useCallback(() => {
    const p = new URLSearchParams()
    if (clinic) p.set('clinicId', clinic)
    if (from) p.set('fromUtc', new Date(from).toISOString())
    if (to) p.set('toUtc', new Date(to).toISOString())
    api<{ items: OwnerAiUsageRow[] }>(`/api/owner/audit-logs/ai?${p.toString()}`)
      .then((d) => setRows(d.items))
      .catch((e) => setError(errorMessage(e)))
  }, [clinic, from, to])

  useEffect(load, [load])

  const clinicName = (id?: number | null) => clinics.find((c) => c.id === id)?.name ?? '—'

  return (
    <div className="card stack" style={{ gap: 14 }}>
      <div className="card-head">
        <div className="card-title"><Icon name="sparkles" /><h3>استخدام الذكاء — كل العيادات</h3></div>
        <button className="btn ghost sm" onClick={load}><Icon name="refresh" size={16} /> تحديث</button>
      </div>
      <div className="muted small">كل طلبات الذكاء في التثبيت: النوع، المصادر المستخدمة، والمدة — ويُعرض نص السؤال والإجابة فقط إن كان مفتاح التسجيل مفعّلًا للنطاق.</div>
      <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
        <div className="field" style={{ maxWidth: 200 }}>
          <label>العيادة</label>
          <select className="input" value={clinic} onChange={(e) => setClinic(e.target.value)}>
            <option value="">كل العيادات</option>
            {clinics.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </div>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>من تاريخ</label>
          <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </div>
        <div className="field" style={{ maxWidth: 180 }}>
          <label>إلى تاريخ</label>
          <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </div>
      </div>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      {rows == null && !error ? (
        <div className="loading"><span className="spinner dark" /></div>
      ) : (
        <div style={{ overflowX: 'auto' }}>
          <table className="table">
            <thead>
              <tr><th>الوقت</th><th>العيادة</th><th>المستخدم</th><th>النوع</th><th>المصادر</th><th>المدة</th></tr>
            </thead>
            <tbody>
              {(rows ?? []).map((r) => (
                <tr key={r.id}>
                  <td>{new Date(r.atUtc).toLocaleString('ar-EG')}</td>
                  <td>{clinicName(r.clinicId)}</td>
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
              {(rows ?? []).length === 0 && <tr><td colSpan={6} className="muted">لا طلبات مطابقة</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
