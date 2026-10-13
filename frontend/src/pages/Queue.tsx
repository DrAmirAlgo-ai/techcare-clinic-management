import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, errorMessage } from '../api'
import { isClinical, useAuth } from '../auth'
import { ArrivalModal, CollectFeeModal, type ArrivalTarget, type CollectTarget, type Fees } from '../components/FrontDesk'
import { Icon } from '../components/Icon'
import { fmtTime, minutesLabel, paymentLabel, paymentTone, typeLabel, type AppointmentType, type PaymentState } from '../format'
import { useQueueSignal } from '../useQueueSignal'

interface Entry {
  appointmentId: number
  patientId?: number
  patientName: string
  phone?: string
  queueNumber?: number
  type: AppointmentType
  source: 'Reception' | 'Online'
  isUrgent: boolean
  scheduledAt: string
  checkedInAt?: string
  startedAt?: string
  finishedAt?: string
  estimatedWaitMinutes: number
  estimatedAt?: string
  invoiceId?: number
  amountDue: number
  amountPaid: number
  payment: PaymentState
}

interface Snapshot extends Fees {
  current?: Entry
  waiting: Entry[]
  bookedNotArrived: Entry[]
  done: Entry[]
  unpaidCount: number
  avgMinutesPerPatient: number
}

// PLAN.md 6.1: one line of the day's front-desk ↔ clinical team thread.
interface Message {
  id: number
  appointmentId?: number
  patientName?: string
  senderId: number
  senderName: string
  text: string
  sentAt: string
}

function Lane({ tone, title, count, children }: { tone: string; title: string; count: number; children: ReactNode }) {
  return (
    <section className={`lane ${tone}`}>
      <div className="lane-head">
        <h3><span className="lane-dot" /> {title}</h3>
        <span className="count-pill">{count}</span>
      </div>
      {count === 0 ? <div className="lane-empty">لا يوجد</div> : children}
    </section>
  )
}

function PayBadge({ e }: { e: Entry }) {
  return <span className={`badge ${paymentTone[e.payment]}`}>{paymentLabel[e.payment]}</span>
}

export function Queue() {
  const { session } = useAuth()
  const navigate = useNavigate()
  const clinical = isClinical(session?.user.role)
  const [q, setQ] = useState<Snapshot | null>(null)
  const [error, setError] = useState('')
  const [arrival, setArrival] = useState<ArrivalTarget | 'walk-in' | null>(null)
  const [collecting, setCollecting] = useState<CollectTarget | null>(null)
  const [, setTick] = useState(0)
  // PLAN.md 6.1: today's team thread, its composer, and the instant "new note" notice.
  const [msgs, setMsgs] = useState<Message[]>([])
  const [msgText, setMsgText] = useState('')
  const [msgAppt, setMsgAppt] = useState('')
  const [msgBusy, setMsgBusy] = useState(false)
  const [msgError, setMsgError] = useState('')
  const [flash, setFlash] = useState<Message | null>(null)
  const knownMsgIds = useRef<Set<number>>(new Set())
  const myId = useRef(session?.user.id)
  const flashTimer = useRef<number>(0)
  useEffect(() => { myId.current = session?.user.id }, [session?.user.id])

  const load = useCallback(() => {
    api<Snapshot>('/api/queue').then(setQ).catch((e) => setError(errorMessage(e)))
  }, [])
  useEffect(load, [load])

  // The hub signal carries no payload: refetch the thread through the authorized endpoint. A note that is not
  // ours raises the notice; our own notes (optimistically present by now) never do.
  const loadMessages = useCallback(async () => {
    try {
      const list = await api<Message[]>('/api/queue/messages')
      setMsgs(list)
      const fresh = list.filter((m) => !knownMsgIds.current.has(m.id))
      knownMsgIds.current = new Set(list.map((m) => m.id))
      const newest = fresh[fresh.length - 1]
      if (newest && newest.senderId !== myId.current) {
        setFlash(newest)
        window.clearTimeout(flashTimer.current)
        flashTimer.current = window.setTimeout(() => setFlash(null), 10000)
      }
    } catch {
      // the board's own error already covers the connection; the thread silently retries on the next signal
    }
  }, [])
  useEffect(() => { loadMessages() }, [loadMessages])
  useQueueSignal(session?.clinic?.slug, load, 30000, loadMessages)
  useEffect(() => {
    const t = window.setInterval(() => setTick((x) => x + 1), 30000)
    return () => window.clearInterval(t)
  }, [])

  async function act(path: string, body?: unknown) {
    setError('')
    try {
      await api(path, { method: 'POST', body: body ?? {} })
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  async function callNext() {
    setError('')
    try {
      const r = await api<{ id?: number; patientId?: number; message?: string }>('/api/queue/next', { method: 'POST', body: {} })
      if (r.message) setError(r.message)
      else if (clinical && r.patientId) navigate(`/app/patients/${r.patientId}?appointment=${r.id}`)
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const collect = (e: Entry) => setCollecting({ ...e })
  const openFile = (e: Entry) => navigate(`/app/patients/${e.patientId}?appointment=${e.appointmentId}`)
  const cur = q?.current

  async function sendNote(e: FormEvent) {
    e.preventDefault()
    const text = msgText.trim()
    if (!text || msgBusy) return
    setMsgError('')
    setMsgBusy(true)
    try {
      await api('/api/queue/messages', { method: 'POST', body: { text, appointmentId: msgAppt ? Number(msgAppt) : undefined } })
      setMsgText('')
      setMsgAppt('')
      await loadMessages()
    } catch (err) {
      setMsgError(errorMessage(err))
    } finally {
      setMsgBusy(false)
    }
  }

  const noteTargets = q ? [...(cur ? [cur] : []), ...q.waiting, ...q.bookedNotArrived] : []
  const elapsed = cur?.startedAt ? Math.floor((Date.now() - new Date(cur.startedAt).getTime()) / 60000) : 0

  return (
    <>
      <div className="page-head">
        <div>
          <h1>مرضى اليوم</h1>
          {q && (
            <div className="sub row tight">
              <span>متوسط الكشف {minutesLabel(q.avgMinutesPerPatient)}</span>
              {q.unpaidCount > 0 && <span className="badge red">{q.unpaidCount} لم يدفع</span>}
            </div>
          )}
        </div>
        <div className="row">
          <button className="btn ghost lg" onClick={() => setArrival('walk-in')}><Icon name="userPlus" /> تسجيل وصول مريض</button>
          <button className="btn lg" onClick={callNext}>المريض التالي <Icon name="chevronLeft" /></button>
        </div>
      </div>
      {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}

      {!q ? <div className="loading"><span className="spinner dark lg" /></div> : (
        <div className="board">
          <Lane tone="booked" title="لم يصل بعد" count={q.bookedNotArrived.length}>
            {q.bookedNotArrived.map((b) => (
              <div className="pcard" key={b.appointmentId}>
                <div className="pcard-top">
                  <span className="time-chip">{fmtTime(b.scheduledAt)}</span>
                  <div>
                    <div className="cell-main">{b.patientName}</div>
                    <div className="cell-sub">الدخول المتوقع {fmtTime(b.estimatedAt)}</div>
                  </div>
                </div>
                <div className="pcard-tags">
                  <span className="badge gray plain">{typeLabel[b.type]}</span>
                  {b.source === 'Online' && <span className="badge blue plain">حجز إلكتروني</span>}
                  {!b.patientId && <span className="badge amber plain">بدون ملف</span>}
                </div>
                <div className="pcard-actions">
                  <button className="btn sm" onClick={() => setArrival(b)}><Icon name="check" size={15} /> وصل</button>
                  <button className="icon-btn light" title="لم يحضر" onClick={() => act(`/api/queue/${b.appointmentId}/no-show`)}><Icon name="userX" size={16} /></button>
                </div>
              </div>
            ))}
          </Lane>

          <Lane tone="waiting" title="في الانتظار" count={q.waiting.length}>
            {q.waiting.map((w) => (
              <div className={`pcard ${w.isUrgent ? 'urgent' : ''}`} key={w.appointmentId}>
                <div className="pcard-top">
                  <span className="num-chip">{w.queueNumber}</span>
                  <div>
                    <div className="cell-main">{w.patientName}</div>
                    <div className="cell-sub">يدخل {fmtTime(w.estimatedAt)} · بعد {minutesLabel(w.estimatedWaitMinutes)}</div>
                  </div>
                </div>
                <div className="pcard-tags">
                  <span className="badge gray plain">{typeLabel[w.type]}</span>
                  {w.isUrgent && <span className="badge red plain">طارئ</span>}
                  <PayBadge e={w} />
                </div>
                <div className="pcard-actions">
                  <button className="btn sm" onClick={() => act(`/api/queue/${w.appointmentId}/start`)}><Icon name="enter" size={15} /> إدخال</button>
                  {w.payment !== 'Paid' && <button className="btn sm soft" onClick={() => collect(w)}><Icon name="cash" size={15} /> تحصيل</button>}
                  <span className="spacer" />
                  <button className="icon-btn light" title={w.isUrgent ? 'إلغاء الطارئ' : 'طارئ'} onClick={() => act(`/api/queue/${w.appointmentId}/urgent`, { isUrgent: !w.isUrgent })}><Icon name="zap" size={16} /></button>
                  <button className="icon-btn light" title="تأجيل لآخر الدور" onClick={() => act(`/api/queue/${w.appointmentId}/postpone`)}><Icon name="undo" size={16} /></button>
                  <button className="icon-btn light" style={{ color: 'var(--red)' }} title="غادر" onClick={() => act(`/api/queue/${w.appointmentId}/no-show`)}><Icon name="userX" size={16} /></button>
                </div>
              </div>
            ))}
          </Lane>

          <Lane tone="inside" title="في الكشف" count={cur ? 1 : 0}>
            {cur && (
              <div className="pcard current-patient">
                <div className="pcard-top">
                  <span className="big-number" style={{ fontSize: 40 }}>{cur.queueNumber}</span>
                  <div>
                    <h3>{cur.patientName}</h3>
                    <div className="muted small">{typeLabel[cur.type]} · منذ {elapsed < 1 ? 'لحظات' : minutesLabel(elapsed)}</div>
                  </div>
                </div>
                <div className="pcard-tags"><PayBadge e={cur} /></div>
                <div className="pcard-actions">
                  {clinical && cur.patientId && <button className="btn sm white" onClick={() => openFile(cur)}><Icon name="stethoscope" size={15} /> فتح الملف</button>}
                  <button className="btn sm ghost" onClick={() => act(`/api/queue/${cur.appointmentId}/finish`)}><Icon name="check" size={15} /> إنهاء</button>
                  {cur.payment !== 'Paid' && <button className="btn sm ghost" onClick={() => collect(cur)}><Icon name="cash" size={15} /> تحصيل</button>}
                </div>
              </div>
            )}
          </Lane>

          <Lane tone="done" title="تم الكشف" count={q.done.length}>
            {q.done.map((d) => (
              <div className="pcard" key={d.appointmentId}>
                <div className="pcard-top">
                  <span className="num-chip muted-chip">{d.queueNumber}</span>
                  <div>
                    {d.patientId ? <Link className="cell-main" to={`/app/patients/${d.patientId}`}>{d.patientName}</Link> : <div className="cell-main">{d.patientName}</div>}
                    <div className="cell-sub">خرج {fmtTime(d.finishedAt)}</div>
                  </div>
                </div>
                <div className="pcard-tags">
                  <span className="badge gray plain">{typeLabel[d.type]}</span>
                  <PayBadge e={d} />
                </div>
                {d.payment !== 'Paid' && (
                  <div className="pcard-actions">
                    <button className="btn sm soft" onClick={() => collect(d)}><Icon name="cash" size={15} /> تحصيل</button>
                  </div>
                )}
              </div>
            ))}
          </Lane>
        </div>
      )}

      {/* PLAN.md 6.1: the day's front-desk ↔ clinical team thread, live over the queue hub. */}
      {q && (
        <section className="messages">
          <div className="lane-head">
            <h3><span className="lane-dot" /> رسائل الفريق</h3>
            <span className="muted small">بين الاستقبال والطبيب · اليوم فقط</span>
          </div>
          {flash && (
            <div className="alert info page-alert">
              <Icon name="send" size={16} />
              <span>رسالة جديدة من {flash.senderName}{flash.patientName ? ` (${flash.patientName})` : ''}</span>
              <button className="icon-btn sm" title="إخفاء" onClick={() => setFlash(null)}><Icon name="x" size={14} /></button>
            </div>
          )}
          <div className="messages-thread">
            {msgs.length === 0 ? (
              <div className="lane-empty">لا رسائل بعد — اكتب ملاحظة قصيرة لفريق اليوم</div>
            ) : msgs.map((m) => (
              <div key={m.id} className={`message ${m.senderId === session?.user.id ? 'mine' : ''}`}>
                <div className="message-meta">
                  <strong>{m.senderName}</strong>
                  {m.patientName && <span className="badge blue plain">{m.patientName}</span>}
                  <span className="muted small">{fmtTime(m.sentAt)}</span>
                </div>
                <div className="message-text">{m.text}</div>
              </div>
            ))}
          </div>
          {msgError && <div className="alert error page-alert"><Icon name="alert" /> {msgError}</div>}
          <form className="messages-composer" onSubmit={sendNote}>
            <select className="input" value={msgAppt} onChange={(e) => setMsgAppt(e.target.value)} disabled={msgBusy}>
              <option value="">رسالة عامة</option>
              {noteTargets.map((t) => (
                <option key={t.appointmentId} value={t.appointmentId}>
                  {t.queueNumber != null ? `رقم ${t.queueNumber} · ` : ''}{t.patientName}
                </option>
              ))}
            </select>
            <input className="input" value={msgText} onChange={(e) => setMsgText(e.target.value)}
              placeholder="اكتب ملاحظة قصيرة للفريق…" maxLength={300} disabled={msgBusy} />
            <button className="btn" type="submit" disabled={msgBusy || !msgText.trim()}><Icon name="send" size={15} /> إرسال</button>
          </form>
        </section>
      )}

      {arrival && q && (
        <ArrivalModal
          target={arrival === 'walk-in' ? undefined : arrival}
          fees={q}
          onClose={() => setArrival(null)}
          onDone={(warning) => { setArrival(null); setError(warning ?? ''); load() }}
        />
      )}
      {collecting && q && (
        <CollectFeeModal target={collecting} fees={q} onClose={() => setCollecting(null)} onDone={() => { setCollecting(null); load() }} />
      )}
    </>
  )
}
