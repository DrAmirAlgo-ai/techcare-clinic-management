import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorMessage } from '../api'
import { useAuth } from '../auth'
import { ArrivalModal, type ArrivalTarget, type Fees } from '../components/FrontDesk'
import { Icon } from '../components/Icon'
import { Modal } from '../components/Modal'
import { PatientPicker, type PickedPatient } from '../components/PatientPicker'
import { fmtDate, fmtTime, isoDate, statusLabel, statusTone, to12h, typeLabel, type AppointmentStatus, type AppointmentType } from '../format'
import { useQueueSignal } from '../useQueueSignal'

interface Appt {
  id: number
  patientId?: number
  patientName: string
  phone?: string
  scheduledAt: string
  type: AppointmentType
  status: AppointmentStatus
  source: 'Reception' | 'Online'
  isUrgent: boolean
  queueNumber?: number
  notes?: string
  trackingToken: string
  patientCode?: string
  estimatedAt?: string
}

export function Appointments() {
  const { session } = useAuth()
  const [date, setDate] = useState(isoDate())
  const [rows, setRows] = useState<Appt[]>([])
  const [error, setError] = useState('')
  const [creating, setCreating] = useState(false)
  const [arriving, setArriving] = useState<ArrivalTarget | null>(null)
  const [fees, setFees] = useState<Fees>({ consultationFee: 0, followUpFee: 0 })

  const load = useCallback(() => {
    api<Appt[]>(`/api/appointments?date=${date}`).then(setRows).catch((e) => setError(errorMessage(e)))
  }, [date])
  useEffect(load, [load])
  useQueueSignal(session?.clinic?.slug, load)
  useEffect(() => {
    api<Fees>('/api/clinic').then((c) => setFees({ consultationFee: c.consultationFee, followUpFee: c.followUpFee })).catch(() => {})
  }, [])

  async function act(path: string) {
    setError('')
    try {
      await api(path, { method: 'POST', body: {} })
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const shift = (days: number) => {
    const d = new Date(`${date}T00:00`)
    d.setDate(d.getDate() + days)
    setDate(isoDate(d))
  }
  const isToday = date === isoDate()
  const active = rows.filter((r) => r.status !== 'Cancelled').length

  return (
    <>
      <div className="page-head">
        <div>
          <h1>حجز المواعيد</h1>
          <div className="sub">{fmtDate(`${date}T00:00`)} · {active} موعد</div>
        </div>
        <div className="row">
          <div className="date-nav">
            <button className="icon-btn light" onClick={() => shift(-1)} title="اليوم السابق"><Icon name="chevronRight" /></button>
            <input className="input" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
            <button className="icon-btn light" onClick={() => shift(1)} title="اليوم التالي"><Icon name="chevronLeft" /></button>
          </div>
          {!isToday && <button className="btn ghost" onClick={() => setDate(isoDate())}>اليوم</button>}
          <button className="btn" onClick={() => setCreating(true)}><Icon name="plus" /> موعد جديد</button>
        </div>
      </div>
      {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}
      <div className="card flush">
        {rows.length === 0 ? <div className="empty"><Icon name="calendar" size={30} /> لا توجد مواعيد في هذا اليوم</div> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>الموعد</th><th>الوقت المتوقع</th><th>المريض</th><th>الهاتف</th><th>النوع</th><th>المصدر</th><th>الحالة</th><th></th></tr></thead>
              <tbody>
                {rows.map((a) => (
                  <tr key={a.id}>
                    <td className="nowrap strong">{fmtTime(a.scheduledAt)}</td>
                    <td className="nowrap">{a.estimatedAt ? <span className="badge teal plain">{fmtTime(a.estimatedAt)}</span> : <span className="muted">—</span>}</td>
                    <td>
                      <div className="row tight">
                        {a.patientId ? <Link className="cell-main" to={`/app/patients/${a.patientId}`}>{a.patientName}</Link> : <span className="cell-main">{a.patientName}</span>}
                        {!a.patientId && <span className="badge amber">بدون ملف</span>}
                      </div>
                      {a.patientCode && <div className="cell-sub">ملف {a.patientCode}</div>}
                    </td>
                    <td><span className="ltr">{a.phone}</span></td>
                    <td>{typeLabel[a.type]}</td>
                    <td>{a.source === 'Online' ? <span className="badge blue">حجز إلكتروني</span> : <span className="badge gray">الاستقبال</span>}</td>
                    <td><span className={`badge ${statusTone[a.status]}`}>{statusLabel[a.status]}{a.queueNumber ? ` · ${a.queueNumber}` : ''}</span></td>
                    <td>
                      <div className="actions">
                        {a.status === 'Booked' && isToday && (
                          <button className="btn sm" onClick={() => setArriving({ appointmentId: a.id, patientId: a.patientId, patientName: a.patientName, phone: a.phone, type: a.type })}>
                            <Icon name="check" size={15} /> وصل
                          </button>
                        )}
                        <a className="icon-btn light" href={`/track/${a.trackingToken}`} target="_blank" rel="noreferrer" title="صفحة المتابعة"><Icon name="link" size={16} /></a>
                        {(a.status === 'Booked' || a.status === 'Waiting') && <button className="icon-btn light" style={{ color: 'var(--red)' }} title="إلغاء الموعد" onClick={() => act(`/api/appointments/${a.id}/cancel`)}><Icon name="x" size={16} /></button>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {creating && <NewAppointment date={date} onClose={() => setCreating(false)} onSaved={() => { setCreating(false); load() }} />}
      {arriving && <ArrivalModal target={arriving} fees={fees} onClose={() => setArriving(null)} onDone={(warning) => { setArriving(null); setError(warning ?? ''); load() }} />}
    </>
  )
}

function NewAppointment({ date, onClose, onSaved }: { date: string; onClose: () => void; onSaved: () => void }) {
  const [patient, setPatient] = useState<PickedPatient | null>(null)
  const [day, setDay] = useState(date < isoDate() ? isoDate() : date)
  const [slots, setSlots] = useState<string[] | null>(null)
  const [time, setTime] = useState('')
  const [type, setType] = useState<AppointmentType>('Consultation')
  const [notes, setNotes] = useState('')
  const [error, setError] = useState('')

  const loadSlots = useCallback(() => {
    setSlots(null)
    setTime('')
    api<{ slots: string[] }>(`/api/appointments/slots?date=${day}`).then((r) => setSlots(r.slots)).catch(() => setSlots([]))
  }, [day])
  useEffect(loadSlots, [loadSlots])

  async function save() {
    if (!patient || !time) return
    try {
      await api('/api/appointments', { body: { patientId: patient.id, scheduledAt: `${day}T${time}:00`, type, notes } })
      onSaved()
    } catch (e) {
      setError(errorMessage(e))
      loadSlots()
    }
  }

  return (
    <Modal title="موعد جديد" onClose={onClose}>
      {!patient ? <PatientPicker onPick={setPatient} /> : (
        <div className="stack" style={{ gap: 16 }}>
          {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
          <div className="selected-box">
            <div className="row tight"><Icon name="user" /> <b>{patient.fullName}</b></div>
            <button className="btn sm ghost" onClick={() => setPatient(null)}>تغيير</button>
          </div>
          <div className="grid cols-2">
            <div className="field"><label>التاريخ</label><input className="input" type="date" min={isoDate()} value={day} onChange={(e) => setDay(e.target.value)} /></div>
            <div className="field">
              <label>نوع الزيارة</label>
              <select className="input" value={type} onChange={(e) => setType(e.target.value as AppointmentType)}>
                <option value="Consultation">كشف</option>
                <option value="FollowUp">استشارة</option>
              </select>
            </div>
          </div>
          <div className="field">
            <label>المواعيد المتاحة</label>
            {slots === null ? <span className="spinner dark" /> : slots.length === 0 ? <div className="muted strong">لا توجد مواعيد متاحة في هذا اليوم</div> : (
              <div className="chips" style={{ maxHeight: 220, overflowY: 'auto' }}>
                {slots.map((s) => <button type="button" key={s} className={`chip ${time === s ? 'active' : ''}`} onClick={() => setTime(s)}>{to12h(s)}</button>)}
              </div>
            )}
          </div>
          <div className="field"><label>ملاحظات</label><input className="input" value={notes} onChange={(e) => setNotes(e.target.value)} /></div>
          <div className="row end"><button className="btn" onClick={save} disabled={!time}><Icon name="check" /> حفظ الموعد</button></div>
        </div>
      )}
    </Modal>
  )
}
