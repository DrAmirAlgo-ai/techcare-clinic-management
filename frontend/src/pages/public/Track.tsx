import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api, errorMessage } from '../../api'
import { Icon, LogoMark } from '../../components/Icon'
import { fmtDate, fmtTime, minutesLabel, typeLabel, type AppointmentStatus, type AppointmentType } from '../../format'
import { useQueueSignal } from '../../useQueueSignal'

interface TrackData {
  clinic: { name: string; slug: string; doctorName: string; address?: string; phone?: string }
  patientName: string
  scheduledAt: string
  status: AppointmentStatus
  type: AppointmentType
  queueNumber?: number
  currentNumber?: number
  peopleAhead?: number
  estimatedWaitMinutes?: number
  estimatedAt?: string
  avgMinutesPerPatient: number
  canCancel: boolean
}

export function Track() {
  const { token } = useParams()
  const [d, setD] = useState<TrackData | null>(null)
  const [error, setError] = useState('')
  const [confirming, setConfirming] = useState(false)

  const load = useCallback(() => {
    api<TrackData>(`/api/public/track/${token}`).then(setD).catch((e) => setError(errorMessage(e)))
  }, [token])
  useEffect(load, [load])
  useQueueSignal(d?.clinic.slug, load)

  async function cancel() {
    try {
      await api(`/api/public/track/${token}/cancel`, { method: 'POST', body: {} })
      setConfirming(false)
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  if (error && !d) return <div className="public"><div className="empty"><Icon name="alert" size={30} /> الحجز غير موجود</div></div>
  if (!d) return <div className="loading"><span className="spinner dark lg" /></div>

  return (
    <div className="public">
      <div className="card track-card stack center" style={{ gap: 18, alignItems: 'stretch' }}>
        <div className="row" style={{ justifyContent: 'center', gap: 12 }}>
          <LogoMark size={40} />
          <div style={{ textAlign: 'start' }}>
            <h2>{d.clinic.doctorName}</h2>
            <div className="muted small">{d.clinic.name}</div>
          </div>
        </div>
        <div className="divider" />
        <div className="strong">{d.patientName} · {typeLabel[d.type]}</div>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}

        {d.status === 'Booked' && (
          <>
            <div className="ai-box">
              <div className="kv"><span>التاريخ</span><span>{fmtDate(d.scheduledAt)}</span></div>
              <div className="kv"><span>الموعد</span><span>{fmtTime(d.scheduledAt)}</span></div>
              {d.estimatedAt && <div className="kv"><span>الوقت المتوقع للدخول</span><span style={{ color: 'var(--primary)' }}>{fmtTime(d.estimatedAt)}</span></div>}
              <div className="kv"><span>الحالة</span><span className="badge blue">مؤكد</span></div>
            </div>
            {d.canCancel && !confirming && <button className="btn danger" onClick={() => setConfirming(true)}><Icon name="x" /> إلغاء الحجز</button>}
            {confirming && (
              <div className="row" style={{ justifyContent: 'center' }}>
                <button className="btn danger" onClick={cancel}>تأكيد الإلغاء</button>
                <button className="btn ghost" onClick={() => setConfirming(false)}>تراجع</button>
              </div>
            )}
          </>
        )}

        {d.status === 'Waiting' && (
          <>
            <div className="muted strong">رقمك</div>
            <div className="track-number">{d.queueNumber}</div>
            <div className="grid cols-3" style={{ gap: 10 }}>
              <div className="track-stat"><div className="muted small">الحالي</div><div className="v">{d.currentNumber ?? '—'}</div></div>
              <div className="track-stat"><div className="muted small">قبلك</div><div className="v">{d.peopleAhead ?? 0}</div></div>
              <div className="track-stat"><div className="muted small">الانتظار</div><div className="v" style={{ fontSize: 18 }}>{minutesLabel(d.estimatedWaitMinutes)}</div></div>
            </div>
            {d.estimatedAt && (
              <div className="ai-box"><div className="kv"><span>الوقت المتوقع للدخول</span><span style={{ color: 'var(--primary)' }}>{fmtTime(d.estimatedAt)}</span></div></div>
            )}
            <div className="row" style={{ justifyContent: 'center' }}><span className="badge green">تحديث مباشر</span></div>
          </>
        )}

        {d.status === 'InConsultation' && <div className="alert success" style={{ fontSize: 20, justifyContent: 'center' }}><Icon name="enter" /> تفضل بالدخول</div>}
        {d.status === 'Done' && <div className="alert success" style={{ justifyContent: 'center' }}><Icon name="check" /> تمت الزيارة</div>}
        {d.status === 'Cancelled' && <div className="alert warn" style={{ justifyContent: 'center' }}>تم إلغاء الحجز</div>}
        {d.status === 'NoShow' && <div className="alert warn" style={{ justifyContent: 'center' }}>انتهى الحجز</div>}

        <div className="row" style={{ justifyContent: 'center' }}>
          <Link className="btn ghost" to={`/c/${d.clinic.slug}`}><Icon name="globe" /> صفحة العيادة</Link>
          {d.clinic.phone && <a className="btn ghost" href={`tel:${d.clinic.phone}`}><Icon name="phone" /> اتصال</a>}
        </div>
      </div>
    </div>
  )
}
