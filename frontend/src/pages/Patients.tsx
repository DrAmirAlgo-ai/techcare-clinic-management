import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, errorMessage } from '../api'
import { isClinical, useAuth } from '../auth'
import { Icon } from '../components/Icon'
import { Modal } from '../components/Modal'
import { PatientForm } from '../components/PatientForm'
import type { PatientRow } from '../components/PatientPicker'
import { fmtShortDate } from '../format'

export function Patients() {
  const { session } = useAuth()
  const navigate = useNavigate()
  const [q, setQ] = useState('')
  const [rows, setRows] = useState<PatientRow[] | null>(null)
  const [error, setError] = useState('')
  const [creating, setCreating] = useState(false)

  useEffect(() => {
    const t = window.setTimeout(() => {
      api<PatientRow[]>(`/api/patients?take=100&q=${encodeURIComponent(q)}`).then(setRows).catch((e) => setError(errorMessage(e)))
    }, 250)
    return () => window.clearTimeout(t)
  }, [q])

  return (
    <>
      <div className="page-head">
        <h1>المرضى</h1>
        <button className="btn" onClick={() => setCreating(true)}><Icon name="userPlus" /> مريض جديد</button>
      </div>
      {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}
      <div className="card flush">
        <div className="card-head">
          <div className="search" style={{ width: 360, maxWidth: '100%' }}>
            <Icon name="search" />
            <input className="input" placeholder="بحث بالاسم أو الهاتف أو رقم الملف" value={q} onChange={(e) => setQ(e.target.value)} />
          </div>
          {rows && <span className="muted small strong">{rows.length} ملف</span>}
        </div>
        {!rows ? <div className="loading"><span className="spinner dark lg" /></div> : rows.length === 0 ? (
          <div className="empty"><Icon name="users" size={30} /> {q ? 'لا توجد نتائج مطابقة' : 'لا توجد ملفات مرضى'}</div>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>رقم الملف</th><th>الاسم</th><th>الهاتف</th><th>الزيارات</th><th>آخر زيارة</th><th></th></tr></thead>
              <tbody>
                {rows.map((p) => (
                  <tr key={p.id} className="clickable" onClick={() => navigate(`/app/patients/${p.id}`)}>
                    <td><span className="badge teal plain">{p.code}</span></td>
                    <td className="cell-main">{p.fullName}</td>
                    <td><span className="ltr">{p.phone}</span></td>
                    <td>{p.visitsCount}</td>
                    <td>{fmtShortDate(p.lastVisit) || '—'}</td>
                    <td className="muted" style={{ textAlign: 'end' }}><Icon name="chevronLeft" size={16} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {creating && (
        <Modal title="مريض جديد" onClose={() => setCreating(false)}>
          <PatientForm showMedical={isClinical(session?.user.role)} onSaved={(p) => navigate(`/app/patients/${p.id}`)} onCancel={() => setCreating(false)} />
        </Modal>
      )}
    </>
  )
}
