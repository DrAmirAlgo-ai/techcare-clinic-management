import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError, api, errorMessage, loadSession, openLibraryDocument } from '../api'
import { isClinical, useAuth } from '../auth'
import { Icon } from '../components/Icon'
import LabRequestsPicker from '../components/LabRequestsPicker'
import { Modal } from '../components/Modal'
import { PatientForm } from '../components/PatientForm'
import { RichText } from '../components/RichText'
import { VoiceInput } from '../components/VoiceInput'
import { fmtShortDate, methodLabel, money, type PaymentMethod } from '../format'

interface Patient {
  id: number
  code: string
  fullName: string
  birthDate?: string
  gender?: string
  phone?: string
  age?: number
  bloodType?: string
  chronicConditions?: string
  allergies?: string
  notes?: string
  /** PLAN_ADDENDUM.md 9.3: the identity has been hidden for good — the medical history stays. */
  isAnonymized?: boolean
}

interface RxItem { drugName: string; dose?: string; frequency?: string; duration?: string; instructions?: string }

interface Visit {
  id: number
  visitDate: string
  chiefComplaint?: string
  examination?: string
  diagnosis?: string
  notes?: string
  labRequests?: string
  followUpDate?: string
  bloodPressure?: string
  pulse?: number
  temperature?: number
  weightKg?: number
  heightCm?: number
  bloodSugar?: number
  doctorName?: string
  prescription: RxItem[]
}

export function PatientDetail() {
  const { id } = useParams()
  const [params] = useSearchParams()
  return <PatientFile key={`${id}-${params.get('appointment') ?? ''}`} />
}

function PatientFile() {
  const { id } = useParams()
  const [params] = useSearchParams()
  const appointmentId = params.get('appointment')
  const { session, hasPermission } = useAuth()
  const clinical = isClinical(session?.user.role)
  const admin = hasPermission('admin')
  const navigate = useNavigate()

  const [patient, setPatient] = useState<Patient | null>(null)
  const [visits, setVisits] = useState<Visit[]>([])
  const [error, setError] = useState('')
  const [editing, setEditing] = useState(false)
  const [showVisitForm, setShowVisitForm] = useState(!!appointmentId)
  const [savedVisitId, setSavedVisitId] = useState<number | null>(null)

  const load = useCallback(() => {
    api<Patient>(`/api/patients/${id}`).then(setPatient).catch((e) => setError(errorMessage(e)))
    if (clinical) api<Visit[]>(`/api/visits/by-patient/${id}`).then(setVisits).catch(() => {})
  }, [id, clinical])
  useEffect(load, [load])

  async function nextPatient() {
    try {
      const r = await api<{ id?: number; patientId?: number; message?: string }>('/api/queue/next', { method: 'POST', body: {} })
      navigate(r.patientId ? `/app/patients/${r.patientId}?appointment=${r.id}` : '/app/queue')
    } catch {
      navigate('/app/queue')
    }
  }

  if (error) return <div className="alert error"><Icon name="alert" /> {error}</div>
  if (!patient) return <div className="loading"><span className="spinner dark lg" /></div>

  return (
    <>
      <div className="card" style={{ marginBottom: 16 }}>
        <div className="row between">
          <div className="profile-head">
            <span className="profile-avatar">{patient.fullName.trim().charAt(0)}</span>
            <div>
              <h1>{patient.fullName}</h1>
              <div className="meta">
                <span><Icon name="file" size={15} /> ملف {patient.code}</span>
                {patient.isAnonymized && <span className="badge gray"><Icon name="shield" size={13} /> هوية مخفية</span>}
                {patient.age != null && <span><Icon name="user" size={15} /> {patient.age} سنة{patient.gender ? ` · ${patient.gender}` : ''}</span>}
                {patient.phone && <span><Icon name="phone" size={15} /> <span className="ltr">{patient.phone}</span></span>}
                {clinical && patient.bloodType && <span><Icon name="heart" size={15} /> {patient.bloodType}</span>}
              </div>
            </div>
          </div>
          <div className="row">
            <button className="btn ghost" onClick={() => setEditing(true)}><Icon name="edit" /> تعديل البيانات</button>
            {clinical && !showVisitForm && <button className="btn" onClick={() => setShowVisitForm(true)}><Icon name="stethoscope" /> تسجيل كشف</button>}
          </div>
        </div>
      </div>

      {savedVisitId && (
        <div className="alert success page-alert" style={{ justifyContent: 'space-between' }}>
          <span className="row tight"><Icon name="check" /> تم حفظ الكشف</span>
          <span className="row tight">
            <Link className="btn sm ghost" to={`/print/visit/${savedVisitId}`} target="_blank"><Icon name="printer" size={15} /> طباعة الروشتة</Link>
            <button className="btn sm ghost" onClick={() => navigate('/app/queue')}><Icon name="queue" size={15} /> مرضى اليوم</button>
            <button className="btn sm" onClick={nextPatient}>المريض التالي <Icon name="chevronLeft" size={15} /></button>
          </span>
        </div>
      )}

      <div className={clinical ? 'grid side' : ''}>
        <div style={{ minWidth: 0 }}>
          {clinical && showVisitForm && (
            <VisitForm
              patientId={patient.id}
              appointmentId={appointmentId ? Number(appointmentId) : undefined}
              lastVisit={visits[0]}
              onCancel={() => setShowVisitForm(false)}
              onSaved={(visitId) => { setShowVisitForm(false); setSavedVisitId(visitId); load() }}
            />
          )}

          {clinical && (
            <div className="card" style={{ marginBottom: 16 }}>
              <div className="card-head">
                <div className="card-title"><Icon name="activity" /><h3>سجل الزيارات</h3><span className="count-pill">{visits.length}</span></div>
              </div>
              {visits.length === 0 ? <div className="empty"><Icon name="file" size={30} /> لا توجد زيارات مسجلة</div> : (
                <div className="timeline">{visits.map((v) => <VisitCard key={v.id} visit={v} />)}</div>
              )}
            </div>
          )}

          <InvoicesPanel patientId={patient.id} appointmentId={appointmentId ? Number(appointmentId) : undefined} lastVisitId={visits[0]?.id} />
        </div>

        {clinical && (
          <div className="stack" style={{ gap: 16 }}>
            <div className="card">
              <div className="card-head"><div className="card-title"><Icon name="shield" /><h3>البيانات الطبية</h3></div></div>
              <div className="kv"><span>الحساسية</span>{patient.allergies ? <span className="danger-text">{patient.allergies}</span> : <span>لا يوجد</span>}</div>
              <div className="kv"><span>الأمراض المزمنة</span><span>{patient.chronicConditions || 'لا يوجد'}</span></div>
              <div className="kv"><span>فصيلة الدم</span><span>{patient.bloodType || '—'}</span></div>
              {patient.notes && <div className="kv"><span>ملاحظات</span><span style={{ fontWeight: 600 }}>{patient.notes}</span></div>}
            </div>
            {admin && <DataRights patient={patient} onReload={load} onGone={() => navigate('/app/patients')} />}
            <AiPanel patientId={patient.id} />
          </div>
        )}
      </div>

      {editing && (
        <Modal title="تعديل بيانات المريض" onClose={() => setEditing(false)}>
          <PatientForm
            id={patient.id}
            showMedical={clinical}
            initial={{
              fullName: patient.fullName, phone: patient.phone ?? '', birthDate: patient.birthDate ?? '', gender: patient.gender ?? '',
              bloodType: patient.bloodType ?? '', chronicConditions: patient.chronicConditions ?? '', allergies: patient.allergies ?? '', notes: patient.notes ?? '',
            }}
            onSaved={() => { setEditing(false); load() }}
            onCancel={() => setEditing(false)}
          />
        </Modal>
      )}
    </>
  )
}

/**
 * PLAN_ADDENDUM.md 9.3 — the data rights, admin only: export the complete file as JSON or open its
 * printable PDF view, hide the patient's identity for good, or remove the file logically. The server
 * records every one of these in the clinic's audit trail; the retention period behind a delete is set
 * in the clinic settings.
 */
function DataRights({ patient, onReload, onGone }: { patient: Patient; onReload: () => void; onGone: () => void }) {
  const [busy, setBusy] = useState('')
  const [confirming, setConfirming] = useState<'anonymize' | 'delete' | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  async function exportJson() {
    setBusy('json')
    setError('')
    try {
      const session = loadSession()
      const res = await fetch(`/api/patients/${patient.id}/export`, {
        headers: session ? { Authorization: `Bearer ${session.token}` } : {},
      })
      if (!res.ok) {
        let message = `حدث خطأ (${res.status})`
        try {
          const data = await res.json()
          if (data?.message) message = data.message
          else if (data?.title) message = data.title
        } catch { /* non-JSON body */ }
        throw new ApiError(res.status, message)
      }
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `PatientFile-${patient.code}.json`
      document.body.appendChild(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy('')
    }
  }

  async function anonymize() {
    setBusy('anonymize')
    setError('')
    try {
      await api(`/api/patients/${patient.id}/anonymize`, { method: 'POST' })
      setNotice('تم إخفاء هوية المريض — سجله الطبي باقٍ كما هو')
      setConfirming(null)
      onReload()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy('')
    }
  }

  async function deleteFile() {
    setBusy('delete')
    setError('')
    try {
      await api(`/api/patients/${patient.id}`, { method: 'DELETE' })
      onGone()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy('')
      setConfirming(null)
    }
  }

  return (
    <>
      <div className="card">
        <div className="card-head">
          <div className="card-title"><Icon name="shield" /><h3>حقوق البيانات</h3></div>
        </div>
        <p className="small muted" style={{ marginTop: 0, marginBottom: 10 }}>
          تصدير الملف الكامل أو إخفاء الهوية أو الحذف المنطقي — يُسجَّل كل إجراء في سجل العيادة.
        </p>
        <div className="row" style={{ flexWrap: 'wrap', gap: 8 }}>
          <button className="btn sm" onClick={exportJson} disabled={busy !== ''}>
            {busy === 'json' ? <span className="spinner" /> : <Icon name="file" size={15} />} تصدير JSON
          </button>
          <button className="btn sm" onClick={() => window.open(`/print/patient/${patient.id}`, '_blank')} disabled={busy !== ''}>
            <Icon name="printer" size={15} /> تصدير PDF
          </button>
          <button className="btn sm ghost" style={{ color: 'var(--amber)' }} onClick={() => { setConfirming('anonymize'); setError('') }} disabled={patient.isAnonymized || busy !== ''}>
            <Icon name="shield" size={15} /> إخفاء الهوية
          </button>
          <button className="btn sm ghost" style={{ color: 'var(--red)' }} onClick={() => { setConfirming('delete'); setError('') }} disabled={busy !== ''}>
            <Icon name="userX" size={15} /> حذف الملف
          </button>
        </div>
        {notice && <div className="alert success" style={{ marginTop: 12 }}><Icon name="check" /> {notice}</div>}
        {error && <div className="alert error" style={{ marginTop: 12 }}><Icon name="alert" /> {error}</div>}
      </div>

      {confirming === 'anonymize' && (
        <Modal title="إخفاء هوية المريض" onClose={() => setConfirming(null)}>
          <p>سيُستبدل اسم المريض ورقم هاتفه وتاريخ ميله وجنسه بـ«مريض مجهول»، ولا يمكن التراجع عن ذلك. يبقى سجله الطبي كما هو.</p>
          <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
            <button className="btn ghost" onClick={() => setConfirming(null)}>إلغاء</button>
            <button className="btn" style={{ background: 'var(--amber)', borderColor: 'var(--amber)' }} onClick={anonymize} disabled={busy === 'anonymize'}>
              {busy === 'anonymize' ? <span className="spinner" /> : <Icon name="shield" />} تأكيد
            </button>
          </div>
        </Modal>
      )}

      {confirming === 'delete' && (
        <Modal title="حذف ملف المريض" onClose={() => setConfirming(null)}>
          <p>سيختفي الملف من كل الشاشات فوراً (حذف منطقي). تبقى بياناته محفوظة على الخادم لمدة الاحتفاظ المعينة في إعدادات العيادة، ثم يُزال نهائياً.</p>
          <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
            <button className="btn ghost" onClick={() => setConfirming(null)}>إلغاء</button>
            <button className="btn" style={{ background: 'var(--red)', borderColor: 'var(--red)' }} onClick={deleteFile} disabled={busy === 'delete'}>
              {busy === 'delete' ? <span className="spinner" /> : <Icon name="userX" />} تأكيد الحذف
            </button>
          </div>
        </Modal>
      )}
    </>
  )
}

interface VisitAttachment {
  id: number
  originalName: string
  contentType: string
  sizeBytes: number
  uploadedAtUtc: string
}

/** PLAN_FULL.md 5.1: a thumbnail preview — the session must open the protected endpoint, an <img> cannot carry the token. */
function AttachmentThumb({ att, onOpen }: { att: VisitAttachment; onOpen: (url: string) => void }) {
  const [url, setUrl] = useState('')
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    const session = loadSession()
    let objectUrl = ''
    let alive = true
    fetch(`/api/visits/attachments/${att.id}`, { headers: session ? { Authorization: `Bearer ${session.token}` } : {} })
      .then((res) => (res.ok ? res.blob() : null))
      .then((blob) => {
        if (!alive || !blob) return
        objectUrl = URL.createObjectURL(blob)
        setUrl(objectUrl)
      })
      .catch(() => { if (alive) setFailed(true) })
    return () => {
      alive = false
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [att.id])

  if (failed) return <span className="attach-thumb failed" title={att.originalName}><Icon name="file" size={14} /></span>
  if (!url) return <span className="attach-thumb loading" title={att.originalName} />
  // PLAN.md 10.11: a PDF has no thumbnail — an icon opens the blob in a new tab instead.
  const isPdf = att.contentType === 'application/pdf'
  return (
    <button type="button" className="attach-thumb" onClick={() => onOpen(url)} title={att.originalName}>
      {isPdf ? <Icon name="file" size={22} /> : <img src={url} alt={att.originalName} />}
    </button>
  )
}

function VisitCard({ visit: v }: { visit: Visit }) {
  const vitals: [string, string | number | undefined][] = [
    ['الضغط', v.bloodPressure],
    ['النبض', v.pulse],
    ['الحرارة', v.temperature],
    ['السكر', v.bloodSugar],
    ['الوزن', v.weightKg],
  ]
  const shown = vitals.filter(([, value]) => value != null && value !== '')

  // PLAN_FULL.md 5.1: the lab scans and X-rays kept with this visit — preview them and add more without leaving the file.
  const [attachments, setAttachments] = useState<VisitAttachment[]>([])
  const [uploading, setUploading] = useState(false)
  const [uploadError, setUploadError] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)

  const loadAttachments = useCallback(() => {
    api<VisitAttachment[]>(`/api/visits/${v.id}/attachments`).then(setAttachments).catch(() => {})
  }, [v.id])
  useEffect(loadAttachments, [loadAttachments])

  async function openFiles(e: ChangeEvent<HTMLInputElement>) {
    const files = Array.from(e.target.files ?? [])
    e.target.value = ''
    if (files.length === 0) return
    setUploading(true)
    setUploadError('')
    try {
      // One by one: each image gets its own server-side signature check and its own random storage name.
      for (const file of files) {
        const form = new FormData()
        form.append('file', file)
        await api(`/api/visits/${v.id}/attachments`, { form })
      }
      loadAttachments()
    } catch (err) {
      setUploadError(errorMessage(err))
    } finally {
      setUploading(false)
    }
  }

  return (
    <div className="visit">
      <div className="visit-head">
        <div className="row tight">
          <span className="badge teal plain">{fmtShortDate(v.visitDate)}</span>
          {v.doctorName && <span className="small muted strong">{v.doctorName}</span>}
        </div>
        <div className="row tight">
          <button type="button" className="btn sm ghost" onClick={() => fileRef.current?.click()} disabled={uploading}>
            <Icon name="upload" size={15} /> {uploading ? 'جارٍ الرفع…' : 'رفع مرفق'}
          </button>
          <Link className="btn sm ghost" to={`/print/visit/${v.id}`} target="_blank"><Icon name="printer" size={15} /> طباعة</Link>
        </div>
      </div>
      <input ref={fileRef} type="file" accept="image/png,image/jpeg,application/pdf" multiple style={{ display: 'none' }} onChange={openFiles} />
      {uploadError && <div className="small" style={{ color: 'var(--red)', padding: '4px 0' }}><Icon name="alert" size={14} /> {uploadError}</div>}
      <dl>
        {v.chiefComplaint && <><dt>الشكوى</dt><dd>{v.chiefComplaint}</dd></>}
        {v.examination && <><dt>الفحص</dt><dd>{v.examination}</dd></>}
        {v.diagnosis && <><dt>التشخيص</dt><dd className="strong">{v.diagnosis}</dd></>}
        {shown.length > 0 && (
          <><dt>العلامات الحيوية</dt><dd><div className="vitals">{shown.map(([k, value]) => <span key={k} className="vital">{k} <b>{value}</b></span>)}</div></dd></>
        )}
        {v.prescription.length > 0 && (
          <><dt>الروشتة</dt><dd>
            <ul className="rx-mini">
              {v.prescription.map((i, n) => <li key={n}><b>{i.drugName}</b> <span className="muted">{[i.dose, i.frequency, i.duration].filter(Boolean).join(' · ')}</span></li>)}
            </ul>
          </dd></>
        )}
        {v.labRequests && <><dt>التحاليل</dt><dd>{v.labRequests}</dd></>}
        {v.followUpDate && <><dt>المتابعة</dt><dd>{fmtShortDate(v.followUpDate)}</dd></>}
        {attachments.length > 0 && (
          <><dt>مرفقات التحاليل والأشعة</dt><dd>
            <div className="attach-strip">
              {attachments.map((a) => <AttachmentThumb key={a.id} att={a} onOpen={(url) => window.open(url, '_blank', 'noopener')} />)}
            </div>
          </dd></>
        )}
      </dl>
    </div>
  )
}

const emptyRx: RxItem = { drugName: '', dose: '', frequency: '', duration: '', instructions: '' }

function VisitForm({ patientId, appointmentId, lastVisit, onSaved, onCancel }: {
  patientId: number
  appointmentId?: number
  lastVisit?: Visit
  onSaved: (visitId: number) => void
  onCancel: () => void
}) {
  const [f, setF] = useState({
    chiefComplaint: '', examination: '', diagnosis: '', notes: '', labRequests: '', followUpDate: '',
    bloodPressure: '', pulse: '', temperature: '', weightKg: '', heightCm: '', bloodSugar: '',
  })
  const [rx, setRx] = useState<RxItem[]>([{ ...emptyRx }])
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const [alerts, setAlerts] = useState<{ type: string; message: string; details?: string }[]>([])
  const [drugSuggestions, setDrugSuggestions] = useState<string[]>([])
  const [activeRxIndex, setActiveRxIndex] = useState<number | null>(null)
  const [secondOpinion, setSecondOpinion] = useState(false)
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  const num = (s: string) => (s.trim() === '' ? null : Number(s))

  // PLAN_FULL.md 3.1: when the prescription or the diagnosis changes, the light endpoint re-checks the draft
  // (registered allergy / drug interaction / missing lab) and the result shows as a banner above the form.
  useEffect(() => {
    const hasContent = rx.some((r) => r.drugName.trim()) || f.diagnosis.trim() !== '' || f.temperature.trim() !== ''
    if (!hasContent) { setAlerts([]); return }
    const t = setTimeout(() => {
      api<{ alerts: { type: string; message: string; details?: string }[] }>('/api/visits/drafts/alerts', {
        method: 'POST',
        body: {
          patientId,
          chiefComplaint: f.chiefComplaint,
          diagnosis: f.diagnosis,
          labRequests: f.labRequests,
          temperature: f.temperature.trim() === '' ? null : Number(f.temperature),
          prescription: rx.filter((i) => i.drugName.trim()),
        },
      }).then((r) => setAlerts(r.alerts)).catch(() => setAlerts([]))
    }, 700)
    return () => clearTimeout(t)
  }, [patientId, f.chiefComplaint, f.diagnosis, f.labRequests, f.temperature, rx])

  // Drug-name suggestions while typing in the prescription.
  const fetchDrugSuggestions = useCallback(async (searchTerm: string) => {
    if (searchTerm.length < 2) {
      setDrugSuggestions([])
      return
    }
    try {
      const results = await api<{ id: number; name: string }[]>(`/api/catalog/drugs?q=${encodeURIComponent(searchTerm)}`)
      setDrugSuggestions(results.map((r) => r.name))
    } catch {
      setDrugSuggestions([])
    }
  }, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    setSaving(true)
    setError('')
    try {
      const r = await api<{ id: number }>('/api/visits', {
        body: {
          patientId, appointmentId: appointmentId ?? null,
          chiefComplaint: f.chiefComplaint, examination: f.examination, diagnosis: f.diagnosis, notes: f.notes,
          labRequests: f.labRequests, followUpDate: f.followUpDate || null, bloodPressure: f.bloodPressure || null,
          pulse: num(f.pulse), temperature: num(f.temperature), weightKg: num(f.weightKg), heightCm: num(f.heightCm), bloodSugar: num(f.bloodSugar),
          prescription: rx.filter((i) => i.drugName.trim()),
        },
      })
      onSaved(r.id)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }

  const setRxField = (i: number, k: keyof RxItem, value: string) => setRx(rx.map((r, n) => (n === i ? { ...r, [k]: value } : r)))

  const handleDrugNameChange = (i: number, value: string) => {
    setRxField(i, 'drugName', value)
    setActiveRxIndex(i)
    fetchDrugSuggestions(value)
  }

  return (
    <form className="card stack" style={{ gap: 18, marginBottom: 16 }} onSubmit={submit}>
      {alerts.length > 0 && (
        <div className="alert" style={alerts.some((a) => a.type === 'Serious')
          ? { background: '#fef2f2', border: '1px solid #fecaca', color: '#991b1b' }
          : { background: '#fffbeb', border: '1px solid #fde68a', color: '#92400e' }}>
          <div style={{ fontWeight: 700 }}><Icon name="alert" /> تنبيه أثناء كتابة الكشف</div>
          {alerts.map((a, i) => (
            <div key={i}>{a.message}{a.details ? <span> — {a.details}</span> : null}</div>
          ))}
        </div>
      )}
      <div className="card-head" style={{ marginBottom: 0 }}>
        <div className="card-title"><Icon name="stethoscope" /><h2>كشف جديد</h2></div>
        {appointmentId && <span className="badge green">زيارة اليوم</span>}
      </div>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}

      <div className="section-title">العلامات الحيوية</div>
      <div className="grid cols-4">
        <div className="field"><label>الضغط</label><input className="input" value={f.bloodPressure} onChange={set('bloodPressure')} placeholder="120/80" dir="ltr" /></div>
        <div className="field"><label>النبض</label><input className="input" value={f.pulse} onChange={set('pulse')} inputMode="numeric" /></div>
        <div className="field"><label>الحرارة</label><input className="input" value={f.temperature} onChange={set('temperature')} inputMode="decimal" /></div>
        <div className="field"><label>السكر العشوائي</label><input className="input" value={f.bloodSugar} onChange={set('bloodSugar')} inputMode="numeric" /></div>
        <div className="field"><label>الوزن (كجم)</label><input className="input" value={f.weightKg} onChange={set('weightKg')} inputMode="decimal" /></div>
        <div className="field"><label>الطول (سم)</label><input className="input" value={f.heightCm} onChange={set('heightCm')} inputMode="decimal" /></div>
        <div className="field"><label>موعد المتابعة</label><input className="input" type="date" value={f.followUpDate} onChange={set('followUpDate')} /></div>
      </div>

      <div className="section-title">الكشف</div>
      <div className="grid cols-2">
        <div className="field"><label>الشكوى</label><textarea className="input" value={f.chiefComplaint} onChange={set('chiefComplaint')} /><VoiceInput value={f.chiefComplaint} onChange={(t) => setF({ ...f, chiefComplaint: t })} /></div>
        <div className="field"><label>الفحص</label><textarea className="input" value={f.examination} onChange={set('examination')} /><VoiceInput value={f.examination} onChange={(t) => setF({ ...f, examination: t })} /></div>
      </div>
      <div className="row" style={{ alignItems: 'flex-end', gap: 8 }}>
        <div className="field" style={{ flex: 1 }}><label>التشخيص</label><input className="input" value={f.diagnosis} onChange={set('diagnosis')} /></div>
        <button type="button" className="btn ghost" style={{ height: 40 }} disabled={!f.diagnosis.trim() || secondOpinion}
          onClick={() => setSecondOpinion(true)} title="احتمالات تفريقية بدليل من المكتبة وتحذيرات من ملف المريض">
          <Icon name="sparkles" size={15} /> رأي ثاني
        </button>
      </div>

      <div className="row between section-title" style={{ alignItems: 'center' }}>
        <span>الروشتة</span>
        <span className="row tight">
          {lastVisit && lastVisit.prescription.length > 0 && (
            <button type="button" className="btn sm ghost" onClick={() => setRx(lastVisit.prescription.map((i) => ({ ...emptyRx, ...i })))}><Icon name="copy" size={15} /> تكرار آخر روشتة</button>
          )}
          <button type="button" className="btn sm soft" onClick={() => setRx([...rx, { ...emptyRx }])}><Icon name="plus" size={15} /> دواء</button>
        </span>
      </div>
      <div className="stack">
        {rx.map((item, i) => (
          <div className="rx-row" key={i}>
            <div className="field" style={{ position: 'relative' }}>
              <label>الدواء</label>
              <input
                className="input"
                dir="ltr"
                value={item.drugName}
                onChange={(e) => handleDrugNameChange(i, e.target.value)}
                onBlur={() => setTimeout(() => setActiveRxIndex(null), 150)}
              />
              {activeRxIndex === i && drugSuggestions.length > 0 && (
                <div className="suggestions-dropdown" style={{ position: 'absolute', top: '100%', left: 0, right: 0, zIndex: 1000, marginTop: 4 }}>
                  {drugSuggestions.map((suggestion, index) => (
                    <div
                      key={index}
                      className="suggestion-item"
                      onMouseDown={() => {
                        setRxField(i, 'drugName', suggestion)
                        setActiveRxIndex(null)
                        setDrugSuggestions([])
                      }}
                      style={{ padding: '8px 12px', background: 'white', border: '1px solid #ddd', cursor: 'pointer' }}
                    >
                      {suggestion}
                    </div>
                  ))}
                </div>
              )}
            </div>
            <div className="field"><label>الجرعة</label><input className="input" value={item.dose} onChange={(e) => setRxField(i, 'dose', e.target.value)} /></div>
            <div className="field"><label>عدد المرات</label><input className="input" value={item.frequency} onChange={(e) => setRxField(i, 'frequency', e.target.value)} /></div>
            <div className="field"><label>المدة</label><input className="input" value={item.duration} onChange={(e) => setRxField(i, 'duration', e.target.value)} /></div>
            <div className="field"><label>تعليمات</label><input className="input" value={item.instructions} onChange={(e) => setRxField(i, 'instructions', e.target.value)} /></div>
            <button type="button" className="icon-btn light" style={{ color: 'var(--red)', height: 40, width: 40 }} onClick={() => setRx(rx.filter((_, n) => n !== i))} title="حذف"><Icon name="x" size={16} /></button>
          </div>
        ))}
      </div>

      <div className="grid cols-2">
        <div className="field"><label>التحاليل والأشعة</label><LabRequestsPicker value={f.labRequests} onChange={(v) => setF({ ...f, labRequests: v })} /></div>
        <div className="field"><label>ملاحظات</label><textarea className="input" value={f.notes} onChange={set('notes')} /><VoiceInput value={f.notes} onChange={(t) => setF({ ...f, notes: t })} /></div>
      </div>

      <div className="row end">
        <button type="button" className="btn ghost" onClick={onCancel}>إلغاء</button>
        <button className="btn lg" disabled={saving}>{saving ? <span className="spinner" /> : <Icon name="check" />} حفظ الكشف</button>
      </div>

      {secondOpinion && (
        <SecondOpinionModal
          patientId={patientId}
          diagnosis={f.diagnosis}
          chiefComplaint={f.chiefComplaint}
          examination={f.examination}
          onClose={() => setSecondOpinion(false)}
        />
      )}
    </form>
  )
}

interface DiffCandidate { diagnosis: string; probability: number; evidence: string; warnings: string[] }
interface DiffSource { n: number; documentTitle: string; fileName?: string; pageNumber?: number; sourceType?: string; score: number; documentId?: number | null; snippet: string }
interface DifferentialData { candidates: DiffCandidate[]; note?: string | null; foundInLibrary: boolean; sources: DiffSource[] }

// PLAN_FULL.md 3.2: the second opinion next to the diagnosis — differential probabilities, each with its
// library evidence and the warnings that apply to this patient's constraints.
function SecondOpinionModal({ patientId, diagnosis, chiefComplaint, examination, onClose }: {
  patientId: number
  diagnosis: string
  chiefComplaint: string
  examination: string
  onClose: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [data, setData] = useState<DifferentialData | null>(null)

  useEffect(() => {
    let cancelled = false
    async function run() {
      setBusy(true)
      setError('')
      try {
        const r = await api<DifferentialData>('/api/ai/differential', { body: { patientId, diagnosis, chiefComplaint, examination } })
        if (!cancelled) setData(r)
      } catch (e) {
        if (!cancelled) setError(errorMessage(e))
      } finally {
        if (!cancelled) setBusy(false)
      }
    }
    run()
    return () => { cancelled = true }
  }, [patientId, diagnosis, chiefComplaint, examination])

  const candidates = data?.candidates ?? []

  return (
    <Modal title="رأي ثاني — الاحتمالات التفريقية" onClose={onClose} wide>
      <div className="stack" style={{ gap: 14 }}>
        <div className="small muted">تشخيص قيد الكتابة: <b style={{ color: 'var(--ink)' }}>{diagnosis}</b></div>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        {busy && <div className="row"><span className="spinner dark" /> جارٍ جلب الاحتمالات من المساعد الطبي…</div>}
        {!busy && data && candidates.length === 0 && !data.note && (
          <div className="alert error"><Icon name="alert" /> لم يصل رد مفهّم من المساعد — أعد المحاولة</div>
        )}
        {candidates.map((c, i) => (
          <div key={i} className="ai-box" style={{ padding: 12 }}>
            <div className="row between" style={{ gap: 8 }}>
              <span style={{ fontWeight: 700 }}>{c.diagnosis}</span>
              <span className="badge teal plain" style={{ fontVariantNumeric: 'tabular-nums' }}>{c.probability}%</span>
            </div>
            <div style={{ height: 6, background: 'var(--bg)', borderRadius: 99, overflow: 'hidden', margin: '6px 0' }}>
              <div style={{ height: '100%', width: `${Math.min(100, Math.max(0, c.probability))}%`, background: i === 0 ? 'var(--primary)' : '#94a3b8' }} />
            </div>
            {c.evidence && <div className="small" style={{ lineHeight: 1.65 }}>{c.evidence}</div>}
            {c.warnings.length > 0 && (
              <div style={{ marginTop: 6, display: 'flex', flexDirection: 'column', gap: 4 }}>
                {c.warnings.map((w, n) => (
                  <span key={n} className="small" style={{ color: 'var(--red)', display: 'flex', alignItems: 'center', gap: 6 }}>
                    <Icon name="alert" size={13} /> {w}
                  </span>
                ))}
              </div>
            )}
          </div>
        ))}
        {data?.note && (
          <div className="alert" style={{ background: 'var(--amber-50)', border: '1px solid #fedf89', color: 'var(--amber)' }}>
            <Icon name="alert" /> {data.note}
          </div>
        )}
        {data && !data.foundInLibrary && (
          <div className="small muted">لا يوجد دليل في مكتبة العيادة لهذا التشخيص — الاحتمالات غير موثقة من المكتبة.</div>
        )}
        {data && data.sources.length > 0 && (
          <div>
            <div className="section-title">المصادر</div>
            <div className="stack" style={{ gap: 8 }}>
              {data.sources.map((s) => (
                <div key={s.n} className="ai-box" style={{ padding: 10 }}>
                  <div className="row between" style={{ gap: 8 }}>
                    <span className="small strong">[{s.n}] {s.documentTitle}{s.pageNumber ? ` · صفحة ${s.pageNumber}` : ''}</span>
                    {s.documentId != null && (
                      <button type="button" className="btn sm ghost" onClick={() => openLibraryDocument(s.documentId!)}>
                        <Icon name="external" size={14} /> فتح الملف
                      </button>
                    )}
                  </div>
                  {s.snippet && <div className="small muted" style={{ marginTop: 4 }}>{s.snippet}</div>}
                </div>
              ))}
            </div>
          </div>
        )}
        <div className="small muted">أداة مساعدة للقرار — التشخيص النهائي بيد الطبيب المعالج.</div>
      </div>
    </Modal>
  )
}

function AiPanel({ patientId }: { patientId: number }) {
  const [summary, setSummary] = useState('')
  const [draft, setDraft] = useState('')
  const [draftKind, setDraftKind] = useState<'report' | 'referral' | null>(null)
  const [referTo, setReferTo] = useState('')
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')

  async function run(kind: 'summary' | 'report' | 'referral') {
    setBusy(kind)
    setError('')
    try {
      if (kind === 'summary') {
        const r = await api<{ summary: string }>(`/api/ai/patients/${patientId}/summary`, { method: 'POST', body: {} })
        setSummary(r.summary)
      } else {
        const r = await api<{ text: string }>('/api/ai/draft', { body: { patientId, kind, referTo } })
        setDraft(r.text)
      }
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setBusy('')
    }
  }

  function printDraft() {
    sessionStorage.setItem('tc_print_doc', JSON.stringify({ title: draftKind === 'referral' ? 'خطاب تحويل' : 'تقرير طبي', text: draft }))
    window.open('/print/document', '_blank')
  }

  return (
    <div className="card">
      <div className="card-head"><div className="card-title"><Icon name="sparkles" /><h3>المساعد الطبي</h3></div></div>
      <div className="stack">
        <button className="btn soft block" onClick={() => run('summary')} disabled={!!busy}>
          {busy === 'summary' ? <span className="spinner dark" /> : <Icon name="activity" />} ملخص الملف الطبي
        </button>
        <div className="grid cols-2" style={{ gap: 8 }}>
          <button className="btn ghost" onClick={() => { setDraftKind('report'); setDraft('') }} disabled={!!busy}><Icon name="file" /> تقرير طبي</button>
          <button className="btn ghost" onClick={() => { setDraftKind('referral'); setDraft('') }} disabled={!!busy}><Icon name="send" /> خطاب تحويل</button>
        </div>
        {error && !draftKind && <div className="alert error"><Icon name="alert" /> {error}</div>}
        {summary && <div className="ai-box"><RichText text={summary} /></div>}
      </div>

      {draftKind && (
        <Modal title={draftKind === 'referral' ? 'خطاب تحويل' : 'تقرير طبي'} onClose={() => setDraftKind(null)} wide>
          <div className="stack" style={{ gap: 16 }}>
            {draftKind === 'referral' && (
              <div className="field"><label>جهة التحويل</label><input className="input" value={referTo} onChange={(e) => setReferTo(e.target.value)} /></div>
            )}
            <div className="row">
              <button className="btn" onClick={() => run(draftKind)} disabled={!!busy}>{busy ? <span className="spinner" /> : <Icon name="sparkles" />} {draft ? 'إعادة الإنشاء' : 'إنشاء'}</button>
              {draft && <button className="btn ghost" onClick={printDraft}><Icon name="printer" /> طباعة</button>}
            </div>
            {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
            {draft && <textarea className="input" style={{ minHeight: 380 }} value={draft} onChange={(e) => setDraft(e.target.value)} />}
          </div>
        </Modal>
      )}
    </div>
  )
}

interface InvoiceRow { id: number; createdAt: string; description: string; amount: number; discount: number; paid: number; method: PaymentMethod; net: number; remaining: number }

function InvoicesPanel({ patientId, appointmentId, lastVisitId }: { patientId: number; appointmentId?: number; lastVisitId?: number }) {
  const [rows, setRows] = useState<InvoiceRow[]>([])
  const [creating, setCreating] = useState(false)
  const [fee, setFee] = useState({ consultation: 0, followUp: 0 })

  const load = useCallback(() => {
    api<{ items: InvoiceRow[] }>(`/api/invoices?patientId=${patientId}`).then((r) => setRows(r.items)).catch(() => {})
  }, [patientId])
  useEffect(load, [load])
  useEffect(() => {
    api<{ consultationFee: number; followUpFee: number }>('/api/clinic').then((c) => setFee({ consultation: c.consultationFee, followUp: c.followUpFee })).catch(() => {})
  }, [])

  return (
    <div className="card flush">
      <div className="card-head">
        <div className="card-title"><Icon name="receipt" /><h3>الفواتير</h3><span className="count-pill">{rows.length}</span></div>
        <button className="btn sm" onClick={() => setCreating(true)}><Icon name="plus" size={15} /> فاتورة</button>
      </div>
      {rows.length === 0 ? <div className="empty"><Icon name="receipt" size={30} /> لا توجد فواتير</div> : (
        <div className="table-wrap">
          <table className="table">
            <thead><tr><th>التاريخ</th><th>البيان</th><th>الصافي</th><th>المدفوع</th><th>المتبقي</th><th>طريقة الدفع</th></tr></thead>
            <tbody>
              {rows.map((i) => (
                <tr key={i.id}>
                  <td className="nowrap">{fmtShortDate(i.createdAt)}</td>
                  <td>{i.description}</td>
                  <td className="nowrap">{money(i.net)}</td>
                  <td className="nowrap">{money(i.paid)}</td>
                  <td className="nowrap">{i.remaining > 0 ? <span className="danger-text">{money(i.remaining)}</span> : <span className="badge green">مسدد</span>}</td>
                  <td>{methodLabel[i.method]}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {creating && (
        <InvoiceModal patientId={patientId} appointmentId={appointmentId} visitId={lastVisitId} fees={fee} onClose={() => setCreating(false)} onSaved={() => { setCreating(false); load() }} />
      )}
    </div>
  )
}

function InvoiceModal({ patientId, appointmentId, visitId, fees, onClose, onSaved }: {
  patientId: number
  appointmentId?: number
  visitId?: number
  fees: { consultation: number; followUp: number }
  onClose: () => void
  onSaved: () => void
}) {
  const [description, setDescription] = useState('كشف')
  const [amount, setAmount] = useState(String(fees.consultation || ''))
  const [discount, setDiscount] = useState('0')
  const [paid, setPaid] = useState(String(fees.consultation || ''))
  const [method, setMethod] = useState<PaymentMethod>('Cash')
  const [linkVisit, setLinkVisit] = useState(!!visitId && !appointmentId)
  const [error, setError] = useState('')
  const net = Number(amount || 0) - Number(discount || 0)

  function preset(kind: 'consultation' | 'followUp') {
    const value = kind === 'consultation' ? fees.consultation : fees.followUp
    setDescription(kind === 'consultation' ? 'كشف' : 'استشارة')
    setAmount(String(value))
    setPaid(String(value - Number(discount || 0)))
  }

  async function save() {
    try {
      // PLAN_FULL.md 4.3: opened from the queue the invoice belongs to today's appointment; the visit link is
      // resolved on the server from it (and back-filled when the visit is recorded).
      await api('/api/invoices', {
        body: { patientId, appointmentId: appointmentId ?? null, visitId: appointmentId ? null : linkVisit ? visitId : null, description, amount: Number(amount), discount: Number(discount || 0), paid: Number(paid || 0), method },
      })
      onSaved()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <Modal title="فاتورة جديدة" onClose={onClose}>
      <div className="stack" style={{ gap: 16 }}>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        <div className="grid cols-2" style={{ gap: 8 }}>
          <button className={`btn ${description === 'كشف' ? 'soft' : 'ghost'}`} onClick={() => preset('consultation')}>كشف · {money(fees.consultation)}</button>
          <button className={`btn ${description === 'استشارة' ? 'soft' : 'ghost'}`} onClick={() => preset('followUp')}>استشارة · {money(fees.followUp)}</button>
        </div>
        <div className="field"><label>البيان</label><input className="input" value={description} onChange={(e) => setDescription(e.target.value)} /></div>
        <div className="grid cols-3">
          <div className="field"><label>المبلغ</label><input className="input" value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" /></div>
          <div className="field"><label>الخصم</label><input className="input" value={discount} onChange={(e) => setDiscount(e.target.value)} inputMode="decimal" /></div>
          <div className="field"><label>المدفوع</label><input className="input" value={paid} onChange={(e) => setPaid(e.target.value)} inputMode="decimal" /></div>
        </div>
        <div className="field">
          <label>طريقة الدفع</label>
          <div className="segmented">
            {(['Cash', 'Card', 'Wallet'] as PaymentMethod[]).map((m) => (
              <button key={m} type="button" className={method === m ? 'active' : ''} onClick={() => setMethod(m)}>{methodLabel[m]}</button>
            ))}
          </div>
        </div>
        <div className="ai-box">
          <div className="kv"><span>الصافي</span><span>{money(net)}</span></div>
          <div className="kv"><span>المتبقي</span><span>{money(net - Number(paid || 0))}</span></div>
          {appointmentId && <div className="kv"><span>الربط</span><span>كش اليوم من الطابور</span></div>}
        </div>
        {visitId && !appointmentId && <label className="check"><input type="checkbox" checked={linkVisit} onChange={(e) => setLinkVisit(e.target.checked)} /> ربط بآخر زيارة</label>}
        <div className="row end"><button className="btn" onClick={save}><Icon name="check" /> حفظ الفاتورة</button></div>
      </div>
    </Modal>
  )
}