import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorMessage } from '../api'
import { useAuth } from '../auth'
import { Icon, type IconName } from '../components/Icon'
import { minutesLabel, money } from '../format'
import { useQueueSignal } from '../useQueueSignal'

interface DashboardData {
  date: string
  totalPatients: number
  newPatientsToday: number
  appointmentsToday: number
  booked: number
  waiting: number
  done: number
  noShow: number
  revenueToday: number
  unpaidToday: number
  queue: {
    current?: { queueNumber?: number; patientName: string }
    waitingCount: number
    avgMinutesPerPatient: number
    avgIsLearned: boolean
  }
}

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

export function Dashboard() {
  const { session } = useAuth()
  const [data, setData] = useState<DashboardData | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(() => {
    api<DashboardData>('/api/dashboard').then(setData).catch((e) => setError(errorMessage(e)))
  }, [])
  useEffect(load, [load])
  useQueueSignal(session?.clinic?.slug, load)

  return (
    <>
      <div className="page-head">
        <div>
          <h1>مرحباً، {session?.user.fullName}</h1>
        </div>
        <Link className="btn" to="/app/queue"><Icon name="queue" /> مرضى اليوم</Link>
      </div>
      {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}
      {!data ? <div className="loading"><span className="spinner dark lg" /></div> : (
        <>
          <div className="grid cols-4">
            <Stat icon="calendar" tone="blue" label="لم يصل بعد" value={data.booked} />
            <Stat icon="clock" tone="amber" label="في الانتظار" value={data.waiting} />
            <Stat icon="check" tone="green" label="تم الكشف" value={data.done} sub={data.noShow ? `${data.noShow} لم يحضر` : undefined} />
            <Stat icon="cash" tone="teal" label="إيرادات اليوم" value={money(data.revenueToday)} sub={data.unpaidToday ? `${data.unpaidToday} لم يدفع` : undefined} />
          </div>
          <div className="grid cols-2 mt">
            <div className="card current-patient stack" style={{ gap: 14 }}>
              <div className="eyebrow"><span className="live-dot" /> في غرفة الكشف</div>
              {data.queue.current ? (
                <div className="row" style={{ gap: 18 }}>
                  <span className="big-number">{data.queue.current.queueNumber}</span>
                  <h2 style={{ fontSize: 22 }}>{data.queue.current.patientName}</h2>
                </div>
              ) : <h2 style={{ fontSize: 22 }}>لا يوجد مريض حالياً</h2>}
              <div className="row between muted" style={{ position: 'relative', zIndex: 1 }}>
                <span>{data.queue.waitingCount} في الانتظار</span>
                {data.queue.waitingCount > 0 && <span>المدة المتوقعة {minutesLabel(data.queue.waitingCount * data.queue.avgMinutesPerPatient)}</span>}
              </div>
            </div>
            <div className="card">
              <div className="card-head"><div className="card-title"><Icon name="users" /><h3>المرضى</h3></div></div>
              <div className="kv"><span>إجمالي الملفات</span><span>{data.totalPatients}</span></div>
              <div className="kv"><span>ملفات جديدة اليوم</span><span>{data.newPatientsToday}</span></div>
              <div className="kv"><span>متوسط مدة الكشف</span><span>{minutesLabel(data.queue.avgMinutesPerPatient)}</span></div>
            </div>
          </div>
        </>
      )}
    </>
  )
}
