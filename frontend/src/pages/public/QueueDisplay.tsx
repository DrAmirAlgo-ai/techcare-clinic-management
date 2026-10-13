import { useCallback, useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api } from '../../api'
import { LogoMark } from '../../components/Icon'
import { minutesLabel } from '../../format'
import { useQueueSignal } from '../../useQueueSignal'

interface DisplayData {
  clinic: { name: string; doctorName: string; specialty: string }
  current?: { queueNumber?: number; name: string }
  next: { queueNumber?: number; name: string; estimatedWaitMinutes: number; isUrgent: boolean }[]
  waitingCount: number
  avgMinutesPerPatient: number
}

export function QueueDisplay() {
  const { slug } = useParams()
  const [d, setD] = useState<DisplayData | null>(null)
  const [now, setNow] = useState(new Date())
  // PLAN_ADDENDUM.md 7.10: if the server is unreachable the screen says so instead of showing stale numbers.
  const [error, setError] = useState(false)

  const load = useCallback(() => {
    api<DisplayData>(`/api/public/clinics/${slug}/queue`)
      .then((r) => { setD(r); setError(false) })
      .catch(() => setError(true))
  }, [slug])
  useEffect(load, [load])
  useQueueSignal(slug, load, 20000)
  useEffect(() => {
    const t = window.setInterval(() => setNow(new Date()), 1000)
    return () => window.clearInterval(t)
  }, [])

  return (
    <div className="display">
      <div className="head">
        <div className="row" style={{ gap: 18 }}>
          <LogoMark size={60} />
          <div>
            <h1>{d?.clinic.doctorName}</h1>
            <div className="spec">{d?.clinic.specialty}</div>
          </div>
        </div>
        <div style={{ textAlign: 'left' }}>
          <div className="clock">{now.toLocaleTimeString('ar-EG-u-nu-latn', { hour: 'numeric', minute: '2-digit' })}</div>
          <div style={{ color: '#9fb3c6', fontWeight: 600 }}>{now.toLocaleDateString('ar-EG-u-nu-latn', { weekday: 'long', day: 'numeric', month: 'long' })}</div>
        </div>
      </div>
      {error && !d && (
        <div style={{ padding: '8px 24px', color: '#f8b4b4', background: 'rgba(248, 100, 100, 0.12)' }}>
          تعذّر جلب بيانات الطابور — سيُعاد الاتصال تلقائياً.
        </div>
      )}
      <div className="body">
        <div className="now">
          <div className="label">في غرفة الكشف</div>
          {d?.current ? (
            <>
              <div className="num">{d.current.queueNumber}</div>
              <div className="name">{d.current.name}</div>
            </>
          ) : <div className="name" style={{ marginTop: 24, opacity: 0.85 }}>لا يوجد</div>}
        </div>
        <div>
          <div className="next-title"><span>الدور التالي</span><span>{d?.waitingCount ?? 0} في الانتظار</span></div>
          <div className="next">
            {d?.next.map((n, i) => (
              <div key={i} className={`next-item ${n.isUrgent ? 'urgent' : ''}`}>
                <span className="n">{n.queueNumber}</span>
                <span>{n.name}</span>
                <span className="eta">{n.isUrgent ? 'حالة طارئة' : minutesLabel(n.estimatedWaitMinutes)}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  )
}
