import { useEffect, useState, type FormEvent } from 'react'
import { api, errorMessage, openLibraryDocument } from '../api'
import { Icon } from '../components/Icon'
import { Modal } from '../components/Modal'
import { PatientPicker, type PickedPatient } from '../components/PatientPicker'
import { RichText } from '../components/RichText'
import { VoiceInput } from '../components/VoiceInput'

interface AiStatus { reachable: boolean; baseUrl: string; chatModel: string; embeddingModel: string; chatModelLoaded: boolean; embeddingModelLoaded: boolean }
interface Source { n: number; documentTitle: string; fileName: string; pageNumber?: number; section?: string; url?: string; sourceType?: string; documentId?: number; snippet: string }
interface Exchange { question: string; patientName?: string; answer: string; sources: Source[]; foundInLibrary: boolean }

export function Assistant() {
  const [status, setStatus] = useState<AiStatus | null>(null)
  const [question, setQuestion] = useState('')
  const [patient, setPatient] = useState<PickedPatient | null>(null)
  const [picking, setPicking] = useState(false)
  const [history, setHistory] = useState<Exchange[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    api<AiStatus>('/api/ai/status').then(setStatus).catch(() => {})
  }, [])

  async function ask(e: FormEvent) {
    e.preventDefault()
    if (!question.trim()) return
    setBusy(true)
    setError('')
    try {
      const r = await api<{ answer: string; sources: Source[]; foundInLibrary: boolean }>('/api/ai/ask', { body: { question, patientId: patient?.id ?? null } })
      setHistory([{ question, patientName: patient?.fullName, answer: r.answer, sources: r.sources, foundInLibrary: r.foundInLibrary }, ...history])
      setQuestion('')
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  /** PLAN.md 1.3: the cited file opens through the secure document endpoint; the token travels in the fetch. */
  async function openCitedDocument(documentId: number) {
    try {
      await openLibraryDocument(documentId)
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  const online = status?.reachable && status.chatModelLoaded
  return (
    <>
      <div className="page-head">
        <h1>المساعد الطبي</h1>
        {status && (
          <span className={`badge ${online ? 'green' : 'red'}`}>
            {online ? `متصل · ${status.chatModel}` : 'غير متصل'}
          </span>
        )}
      </div>

      {/* PLAN_ADDENDUM.md 9.4: when the AI server is down the page still works — the form stays
          responsive and every failure lands as this clear note instead of a frozen button. */}
      {status && !online && (
        <div className="alert info" style={{ marginTop: 0 }}>
          <Icon name="alert" /> خدمة الذكاء غير متاحة الآن — ستظهر الإجابة أو رسالة الخطأ الواضحة عند توفر الخادم.
        </div>
      )}

      <form className="card stack" onSubmit={ask} style={{ gap: 14 }}>
        <textarea className="input" style={{ minHeight: 110 }} value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="السؤال الطبي" />
        {/* PLAN_FULL.md 5.2: dictate the question; on a browser without speech support the button says so and typing stays available. */}
        <VoiceInput value={question} onChange={setQuestion} />
        <div className="row between">
          {patient ? (
            <span className="selected-box" style={{ padding: '6px 12px' }}>
              <span className="row tight"><Icon name="user" size={16} /> <b>{patient.fullName}</b></span>
              <button type="button" className="icon-btn" style={{ width: 26, height: 26 }} onClick={() => setPatient(null)}><Icon name="x" size={14} /></button>
            </span>
          ) : (
            <button type="button" className="btn ghost sm" onClick={() => setPicking(true)}><Icon name="user" size={15} /> ربط بمريض</button>
          )}
          <button className="btn" disabled={busy || !question.trim()}>{busy ? <span className="spinner" /> : <Icon name="send" />} إرسال</button>
        </div>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      </form>

      <div className="stack mt" style={{ gap: 16, paddingBottom: 48 }}>
        {history.map((h, i) => (
          <div key={i} className="card">
            <div className="row tight" style={{ marginBottom: 12 }}>
              <span className="avatar" style={{ background: 'var(--primary-50)', color: 'var(--primary)', width: 30, height: 30, fontSize: 12 }}><Icon name="user" size={15} /></span>
              <h3 style={{ flex: 1 }}>{h.question}</h3>
              {h.patientName && <span className="badge teal">{h.patientName}</span>}
              {/* PLAN_ADDENDUM.md 9.1: unsourced answers are labelled clearly, never presented as sourced */}
              <span className={`badge ${h.foundInLibrary ? 'green' : 'red'}`}>
                {h.foundInLibrary ? 'من المكتبة الطبية' : 'غير مدعومة بمصدر'}
              </span>
            </div>
            <div className="ai-box"><RichText text={h.answer} /></div>
            {h.sources.length > 0 && (
              <div className="mt">
                <div className="section-title">المراجع</div>
                <div className="stack" style={{ gap: 6, marginTop: 8 }}>
                  {h.sources.map((s) => (
                    <details key={s.n} className="small">
                      <summary style={{ cursor: 'pointer' }}>
                        <span className="badge teal plain">{s.n}</span> <b dir="auto">{s.documentTitle}</b>
                        {s.pageNumber ? <span className="muted"> · صفحة {s.pageNumber}</span> : null}
                        {s.section ? <span className="muted" dir="auto"> · {s.section}</span> : null}
                        {s.sourceType ? <span className="muted" dir="ltr"> · {s.sourceType}</span> : null}
                      </summary>
                      <p className="muted" dir="auto" style={{ whiteSpace: 'pre-wrap', margin: '8px 0 0' }}>{s.snippet}</p>
                      <div className="row tight" style={{ marginTop: 6, gap: 10 }}>
                        {s.documentId != null && s.sourceType === 'uploaded' && (
                          <button type="button" className="btn ghost sm" onClick={() => openCitedDocument(s.documentId!)}>
                            <Icon name="file" size={14} /> فتح الملف الكامل
                          </button>
                        )}
                        {s.url && <a href={s.url} target="_blank" rel="noreferrer" className="small">فتح المصدر الأصلي</a>}
                      </div>
                    </details>
                  ))}
                </div>
              </div>
            )}
          </div>
        ))}
      </div>

      {picking && (
        <Modal title="اختيار مريض" onClose={() => setPicking(false)}>
          <PatientPicker onPick={(p) => { setPatient(p); setPicking(false) }} />
        </Modal>
      )}

      {/* PLAN_ADDENDUM.md 9.1: the mandatory medical disclaimer, pinned at the bottom of the assistant page. */}
      <div style={{
        position: 'fixed', left: 0, right: 0, bottom: 0, zIndex: 40,
        background: 'var(--amber-50)', color: 'var(--amber)', borderTop: '1px solid #fedf89',
        padding: '8px 16px', textAlign: 'center', fontSize: 13, fontWeight: 700,
      }}>
        مساعد لدعم القرار السريري — لا يغني عن تقدير الطبيب
      </div>
    </>
  )
}
