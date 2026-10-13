import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorMessage } from '../../api'
import { Icon, LogoMark } from '../../components/Icon'
import { dayLabel, fmtTime, isoDate, money, to12h, type AppointmentType } from '../../format'

interface Profile {
  name: string
  slug: string
  doctorName: string
  specialty: string
  about?: string
  phone?: string
  address?: string
  consultationFee: number
  followUpFee: number
  onlineBookingEnabled: boolean
  workStart: string
  workEnd: string
  workDays: string[]
  /** PLAN.md 10.7: the clinic's own display name; equals name when not customised. */
  displayName?: string
  /** PLAN.md 10.7: true when /api/public/clinics/{slug}/logo serves the clinic's logo. */
  hasLogo?: boolean
}

const dayKey = (d: Date) => ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'][d.getDay()]

export function DoctorSite() {
  const { slug } = useParams()
  const [p, setP] = useState<Profile | null>(null)
  const [notFound, setNotFound] = useState(false)

  useEffect(() => {
    api<Profile>(`/api/public/clinics/${slug}`).then((x) => { setP(x); document.title = `${x.doctorName} · ${x.specialty}` }).catch(() => setNotFound(true))
  }, [slug])

  if (notFound) return <div className="public"><div className="empty"><Icon name="alert" size={30} /> الصفحة غير موجودة</div></div>
  if (!p) return <div className="loading"><span className="spinner dark lg" /></div>

  return (
    <>
      <div className="public-bar">
        <div className="inner">
          <div className="row tight">
            {/* PLAN.md 10.7: the clinic's own logo/name once uploaded, else the platform default. */}
            {p.hasLogo ? <img src={`/api/public/clinics/${p.slug}/logo`} alt={p.displayName ?? p.name} style={{ width: 32, height: 32, objectFit: 'contain', borderRadius: 8, background: '#fff' }} /> : <LogoMark size={32} />}
            <b>{p.displayName ?? p.name}</b>
          </div>
          {p.phone && <a className="btn ghost sm" href={`tel:${p.phone}`}><Icon name="phone" size={15} /> <span className="ltr">{p.phone}</span></a>}
        </div>
      </div>
      <div className="public">
        <section className="hero">
          <div className="hero-avatar">
            {p.hasLogo
              ? <img src={`/api/public/clinics/${p.slug}/logo`} alt={p.displayName ?? p.name} style={{ width: 72, height: 72, objectFit: 'contain' }} />
              : <Icon name="stethoscope" size={52} />}
          </div>
          <div>
            <h1>{p.doctorName}</h1>
            <div className="spec">{p.specialty}</div>
            {p.about && <p>{p.about}</p>}
            <div className="row mt">
              <a className="btn lg white" href="#book"><Icon name="calendar" /> احجز موعدك</a>
            </div>
          </div>
        </section>

        <div className="grid cols-3">
          <div className="card info-card">
            <span className="stat-icon teal"><Icon name="mapPin" size={22} /></span>
            <div><h3>العنوان</h3><div className="muted">{p.address || '—'}</div></div>
          </div>
          <div className="card info-card">
            <span className="stat-icon blue"><Icon name="clock" size={22} /></span>
            <div>
              <h3>مواعيد العمل</h3>
              <div className="muted">{p.workDays.map((d) => dayLabel[d]).join('، ')}</div>
              <div className="muted">{to12h(p.workStart)} – {to12h(p.workEnd)}</div>
            </div>
          </div>
          <div className="card info-card">
            <span className="stat-icon green"><Icon name="cash" size={22} /></span>
            <div>
              <h3>الأسعار</h3>
              <div className="muted">الكشف: <b style={{ color: 'var(--ink)' }}>{money(p.consultationFee)}</b></div>
              <div className="muted">الاستشارة: <b style={{ color: 'var(--ink)' }}>{money(p.followUpFee)}</b></div>
            </div>
          </div>
        </div>

        <div id="book" className="card mt">
          <div className="card-head"><div className="card-title"><Icon name="calendar" /><h2>حجز موعد</h2></div></div>
          {p.onlineBookingEnabled ? <BookingForm profile={p} /> : <div className="alert warn"><Icon name="alert" /> الحجز الإلكتروني غير متاح حالياً</div>}
        </div>
        <div className="public-foot">© {new Date().getFullYear()} {p.name}</div>
      </div>
    </>
  )
}

function BookingForm({ profile }: { profile: Profile }) {
  const days = useMemo(() => {
    const list: Date[] = []
    const d = new Date()
    for (let i = 0; list.length < 10 && i < 30; i++) {
      const day = new Date(d.getFullYear(), d.getMonth(), d.getDate() + i)
      if (profile.workDays.includes(dayKey(day))) list.push(day)
    }
    return list
  }, [profile.workDays])

  const [date, setDate] = useState(days[0] ? isoDate(days[0]) : '')
  const [slots, setSlots] = useState<string[] | null>(null)
  const [time, setTime] = useState('')
  const [name, setName] = useState('')
  const [phone, setPhone] = useState('')
  const [type, setType] = useState<AppointmentType>('Consultation')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [booked, setBooked] = useState<{ token: string; at: string; estimatedAt: string } | null>(null)

  useEffect(() => {
    if (!date) return
    setSlots(null)
    setTime('')
    api<{ slots: string[] }>(`/api/public/clinics/${profile.slug}/slots?date=${date}`).then((r) => setSlots(r.slots)).catch(() => setSlots([]))
  }, [date, profile.slug])

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const r = await api<{ trackingToken: string; scheduledAt: string; estimatedAt: string }>(`/api/public/clinics/${profile.slug}/bookings`, { body: { name, phone, date, time, type } })
      setBooked({ token: r.trackingToken, at: r.scheduledAt, estimatedAt: r.estimatedAt })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  if (booked) {
    const link = `${window.location.origin}/track/${booked.token}`
    return (
      <div className="stack center" style={{ alignItems: 'center', gap: 16, padding: '12px 0' }}>
        <span className="stat-icon green" style={{ width: 64, height: 64, borderRadius: '50%' }}><Icon name="check" size={32} /></span>
        <h2>تم تأكيد الحجز</h2>
        <div className="ai-box" style={{ minWidth: 300 }}>
          <div className="kv"><span>اليوم</span><span>{new Date(booked.at).toLocaleDateString('ar-EG-u-nu-latn', { weekday: 'long', day: 'numeric', month: 'long' })}</span></div>
          <div className="kv"><span>الموعد</span><span>{to12h(booked.at.slice(11, 16))}</span></div>
          <div className="kv"><span>الوقت المتوقع للدخول</span><span style={{ color: 'var(--primary)' }}>{fmtTime(booked.estimatedAt)}</span></div>
        </div>
        <div className="row" style={{ justifyContent: 'center' }}>
          <Link className="btn" to={`/track/${booked.token}`}><Icon name="clock" /> متابعة الحجز</Link>
          <button className="btn ghost" onClick={() => navigator.clipboard?.writeText(link)}><Icon name="copy" /> نسخ الرابط</button>
        </div>
      </div>
    )
  }

  return (
    <form className="stack" style={{ gap: 18 }} onSubmit={submit}>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      <div className="field">
        <label>اليوم</label>
        <div className="chips">
          {days.map((d) => {
            const v = isoDate(d)
            return (
              <button type="button" key={v} className={`chip ${date === v ? 'active' : ''}`} onClick={() => setDate(v)}>
                {dayLabel[dayKey(d)]}<small>{d.toLocaleDateString('ar-EG-u-nu-latn', { day: 'numeric', month: 'short' })}</small>
              </button>
            )
          })}
        </div>
      </div>
      <div className="field">
        <label>الموعد</label>
        {slots === null ? <span className="spinner dark" /> : slots.length === 0 ? <div className="muted">لا توجد مواعيد متاحة</div> : (
          <div className="chips">
            {slots.map((s) => <button type="button" key={s} className={`chip ${time === s ? 'active' : ''}`} onClick={() => setTime(s)}>{to12h(s)}</button>)}
          </div>
        )}
      </div>
      <div className="grid cols-3">
        <div className="field"><label>الاسم الكامل</label><input className="input" value={name} onChange={(e) => setName(e.target.value)} required minLength={3} /></div>
        <div className="field"><label>رقم الهاتف</label><input className="input" dir="ltr" inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} required /></div>
        <div className="field">
          <label>نوع الزيارة</label>
          <select className="input" value={type} onChange={(e) => setType(e.target.value as AppointmentType)}>
            <option value="Consultation">كشف</option>
            <option value="FollowUp">استشارة</option>
          </select>
        </div>
      </div>
      <div className="row end"><button className="btn lg" disabled={busy || !time}>{busy ? <span className="spinner" /> : <Icon name="check" />} تأكيد الحجز</button></div>
    </form>
  )
}
