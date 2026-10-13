import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api, errorMessage } from '../api'
import { Icon } from '../components/Icon'
import { fmtShortDate, money } from '../format'

interface PrintData {
  clinic: { name: string; doctorName: string; specialty: string; phone?: string; address?: string; slug?: string; displayName?: string; hasLogo?: boolean; qualifications?: string; workingHoursText?: string }
  patient: { fullName: string; code: string; birthDate?: string; gender?: string }
  visit: { id: number; visitDate: string; diagnosis?: string; labRequests?: string; followUpDate?: string }
  prescription: { drugName: string; dose?: string; frequency?: string; duration?: string; instructions?: string }[]
}

function Toolbar() {
  return (
    <div className="print-toolbar no-print">
      <button className="btn ghost" onClick={() => window.close()}>إغلاق</button>
      <button className="btn" onClick={() => window.print()}><Icon name="printer" /> طباعة</button>
    </div>
  )
}

export function PrintPrescription() {
  const { id } = useParams()
  const [d, setD] = useState<PrintData | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    api<PrintData>(`/api/visits/${id}/print`).then(setD).catch((e) => setError(errorMessage(e)))
  }, [id])

  if (error) return <div className="print-page"><div className="alert error"><Icon name="alert" /> {error}</div></div>
  if (!d) return <div className="loading"><span className="spinner dark lg" /></div>

  const age = d.patient.birthDate ? new Date().getFullYear() - new Date(d.patient.birthDate).getFullYear() : null
  const clinicName = d.clinic.displayName ?? d.clinic.name

  return (
      <div className="print-page rx-page">
        <Toolbar />
        <div className="rx-header">
          <div className="rx-header-left">
            {d.clinic.hasLogo && d.clinic.slug && (
              <img src={`/api/public/clinics/${d.clinic.slug}/logo?v=${Date.now()}`} alt="" className="rx-logo" />
            )}
            <div>
              <h1>{clinicName}</h1>
              <div className="rx-doctor">{d.clinic.doctorName}</div>
              <div className="rx-specialty">{d.clinic.specialty}</div>
            </div>
          </div>
          <div className="rx-header-right">
            {d.clinic.address && <div className="rx-contact">📍 {d.clinic.address}</div>}
            {d.clinic.phone && <div className="rx-contact ltr">📞 {d.clinic.phone}</div>}
            {d.clinic.qualifications && <div className="rx-qualifications">🎓 {d.clinic.qualifications}</div>}
            {d.clinic.workingHoursText && <div className="rx-hours">🕐 {d.clinic.workingHoursText}</div>}
          </div>
        </div>

        <hr className="rx-divider" />

        <div className="rx-patient-bar">
          <span><b>الاسم:</b> {d.patient.fullName}</span>
          <span><b>السن:</b> {age ?? '—'}</span>
          <span><b>رقم الملف:</b> {d.patient.code}</span>
          <span><b>التاريخ:</b> {fmtShortDate(d.visit.visitDate)}</span>
        </div>

        {d.visit.diagnosis && (
          <div className="rx-diagnosis">
            <b>التشخيص:</b> {d.visit.diagnosis}
          </div>
        )}

        <div className="rx-symbol">Rx</div>

        <table className="rx-table">
          <thead>
            <tr>
              <th style={{ width: '5%' }}>م</th>
              <th style={{ width: '35%' }}>اسم الدواء</th>
              <th style={{ width: '15%' }}>الجرعة</th>
              <th style={{ width: '15%' }}>عدد المرات</th>
              <th style={{ width: '15%' }}>المدة</th>
              <th style={{ width: '15%' }}>التعليمات</th>
            </tr>
          </thead>
          <tbody>
            {d.prescription.map((item, idx) => (
              <tr key={idx}>
                <td className="rx-num">{idx + 1}</td>
                <td className="rx-drug">{item.drugName}</td>
                <td>{item.dose ?? '—'}</td>
                <td>{item.frequency ?? '—'}</td>
                <td>{item.duration ?? '—'}</td>
                <td className="rx-instr">{item.instructions ?? '—'}</td>
              </tr>
            ))}
            {d.prescription.length === 0 && (
              <tr>
                <td colSpan={6} className="rx-empty">لا توجد أدوية في هذه الروشتة</td>
              </tr>
            )}
          </tbody>
        </table>

        {d.visit.labRequests && (
          <div className="rx-section">
            <h3>التحاليل والأشعة المطلوبة</h3>
            <pre className="rx-lab">{d.visit.labRequests}</pre>
          </div>
        )}

        {d.visit.followUpDate && (
          <div className="rx-section">
            <h3>موعد المتابعة</h3>
            <p>{fmtShortDate(d.visit.followUpDate)}</p>
          </div>
        )}

        <div className="rx-footer">
          <div className="rx-signature">
            <div className="rx-sign-line"></div>
            <span>توقيع الطبيب / الختم</span>
          </div>
          <div className="rx-clinic-name">{clinicName}</div>
        </div>
      </div>
    )
  }

/**
 * PLAN_ADDENDUM.md 9.3: the printable view of the complete patient file (GET api/patients/:id/export).
 * The admin "تصدير PDF" button opens this page; the browser's print dialog saves it as a PDF. It renders
 * the same payload the JSON download does, so one fetch on the server stands behind both exports.
 */
interface ExportVisit {
  id: number
  visitDate: string
  doctorName?: string
  chiefComplaint?: string
  examination?: string
  diagnosis?: string
  labRequests?: string
  prescription: { drugName: string; dose?: string; frequency?: string; duration?: string; instructions?: string }[]
}

interface ExportData {
  exportedAtUtc: string
  clinic: { name: string; doctorName: string; specialty?: string; phone?: string; address?: string; slug?: string; displayName?: string; hasLogo?: boolean }
  patient: {
    code: string; fullName: string; birthDate?: string; gender?: string; phone?: string
    bloodType?: string; chronicConditions?: string; allergies?: string; notes?: string; isAnonymized?: boolean
  }
  visits: ExportVisit[]
  appointments: { scheduledAt: string; type: string; status: string }[]
  invoices: { description: string; net: number; paid: number; remaining: number; createdAt: string }[]
}

const APPOINTMENT_STATUS: Record<string, string> = {
  Booked: 'مجدول',
  Waiting: 'بالانتظار',
  InConsultation: 'قيد الكشف',
  Done: 'مكتمل',
  Cancelled: 'ملغى',
  NoShow: 'لم يحضر'
}

export function PrintPatientFile() {
  const { id } = useParams()
  const [d, setD] = useState<ExportData | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    api<ExportData>(`/api/patients/${id}/export`).then(setD).catch((e) => setError(errorMessage(e)))
  }, [id])

  if (error) return <div className="print-page"><div className="alert error"><Icon name="alert" /> {error}</div></div>
  if (!d) return <div className="loading"><span className="spinner dark lg" /></div>

  const age = d.patient.birthDate ? new Date().getFullYear() - new Date(d.patient.birthDate).getFullYear() : null

  return (
    <div className="print-page">
      <Toolbar />
      <div className="print-head">
        <div>
          <h1>{d.clinic.doctorName}</h1>
          <div className="muted strong">{d.clinic.specialty}</div>
        </div>
        <div style={{ textAlign: 'left' }}>
          <b>{d.clinic.displayName ?? d.clinic.name}</b>
          {d.clinic.address && <div className="small muted">{d.clinic.address}</div>}
          {d.clinic.phone && <div className="small muted ltr">{d.clinic.phone}</div>}
          <div className="small muted" style={{ marginTop: 6 }}>ملف مريض — صادر بتاريخ {fmtShortDate(d.exportedAtUtc)}</div>
        </div>
      </div>

      <h2 style={{ fontSize: 18, marginTop: 18 }}>بيانات المريض {d.patient.isAnonymized ? '— هوية مخفية' : ''}</h2>
      <div className="print-patient">
        <span>الاسم: <b>{d.patient.fullName}</b></span>
        <span>الكود: <b>{d.patient.code}</b></span>
        <span>العمر: <b>{age ?? '—'}</b></span>
        <span>الجنس: <b>{d.patient.gender ?? '—'}</b></span>
        {d.patient.phone && <span>الهاتف: <b className="ltr">{d.patient.phone}</b></span>}
        {d.patient.bloodType && <span>فصيلة الدم: <b>{d.patient.bloodType}</b></span>}
      </div>
      {d.patient.chronicConditions && <p className="small mt">الأمراض المزمنة: {d.patient.chronicConditions}</p>}
      {d.patient.allergies && <p className="small">الحساسية: <span className="danger-text">{d.patient.allergies}</span></p>}
      {d.patient.notes && <p className="small">ملاحظات: {d.patient.notes}</p>}

      <h2 style={{ fontSize: 18 }} className="mt">سجل الزيارات ({d.visits.length})</h2>
      {d.visits.length === 0
        ? <p className="muted small">لا توجد زيارات مسجلة</p>
        : d.visits.map((v) => (
          <div key={v.id} style={{ border: '1px solid var(--line)', borderRadius: 8, padding: '10px 14px', marginBottom: 10 }}>
            <div className="row between">
              <b>{fmtShortDate(v.visitDate)}</b>
              {v.doctorName && <span className="muted small">{v.doctorName}</span>}
            </div>
            {v.diagnosis && <p style={{ margin: '6px 0 0' }}>التشخيص: <b>{v.diagnosis}</b></p>}
            {v.chiefComplaint && <p className="small" style={{ margin: '4px 0 0' }}>الشكوى: {v.chiefComplaint}</p>}
            {v.examination && <p className="small" style={{ margin: '4px 0 0' }}>الفحص: {v.examination}</p>}
            {v.labRequests && <p className="small" style={{ margin: '4px 0 0' }}>الطلبات: {v.labRequests}</p>}
            {v.prescription.length > 0 && (
              <ul className="rx-list" style={{ marginTop: 8 }}>
                {v.prescription.map((i, n) => (
                  <li key={n}>
                    <div dir="ltr" style={{ textAlign: 'left', fontWeight: 800 }}>{i.drugName}</div>
                    <div className="small">{[i.dose, i.frequency, i.duration].filter(Boolean).join(' — ')}</div>
                    {i.instructions && <div className="small muted">{i.instructions}</div>}
                  </li>
                ))}
              </ul>
            )}
          </div>
        ))}

      {d.appointments.length > 0 && (
        <>
          <h2 style={{ fontSize: 18 }} className="mt">المواعيد ({d.appointments.length})</h2>
          {d.appointments.map((a, n) => (
            <div key={n} className="row between small" style={{ padding: '4px 0', borderBottom: '1px dashed var(--line)' }}>
              <span>{fmtShortDate(a.scheduledAt)} — {a.type === 'FollowUp' ? 'متابعة' : 'كشف'}</span>
              <span className="muted">{APPOINTMENT_STATUS[a.status] ?? a.status}</span>
            </div>
          ))}
        </>
      )}

      {d.invoices.length > 0 && (
        <>
          <h2 style={{ fontSize: 18 }} className="mt">الفواتير ({d.invoices.length})</h2>
          {d.invoices.map((i, n) => (
            <div key={n} className="row between small" style={{ padding: '4px 0', borderBottom: '1px dashed var(--line)' }}>
              <span>{i.description} — {fmtShortDate(i.createdAt)}</span>
              <span>{money(i.net)} ج.م {i.remaining > 0 ? `· متبقٍ ${money(i.remaining)}` : '· مسدد'}</span>
            </div>
          ))}
        </>
      )}

      <div className="print-foot">
        <span>توقيع / ختم العيادة</span>
        <span>{d.clinic.displayName ?? d.clinic.name}</span>
      </div>
    </div>
  )
}

export function PrintDocument() {
  const [doc] = useState<{ title: string; text: string } | null>(() => {
    const raw = sessionStorage.getItem('tc_print_doc')
    return raw ? JSON.parse(raw) : null
  })
  if (!doc) return <div className="print-page"><div className="empty">لا يوجد مستند</div></div>

  return (
    <div className="print-page">
      <Toolbar />
      <h1 className="center">{doc.title}</h1>
      <div className="doc-text mt">{doc.text}</div>
    </div>
  )
}
