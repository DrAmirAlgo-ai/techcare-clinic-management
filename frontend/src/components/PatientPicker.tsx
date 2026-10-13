import { useEffect, useState } from 'react'
import { api } from '../api'
import { useAuth, isClinical } from '../auth'
import { Icon } from './Icon'
import { PatientForm } from './PatientForm'

export interface PatientRow {
  id: number
  code: string
  fullName: string
  phone?: string
  visitsCount?: number
  lastVisit?: string
}

export interface PickedPatient { id: number; fullName: string; phone?: string }

/** Search an existing patient file, or create a new one inline (prefilled from an online booking). */
export function PatientPicker({ onPick, initialName = '', initialPhone = '' }: {
  onPick: (p: PickedPatient) => void
  initialName?: string
  initialPhone?: string
}) {
  const { session } = useAuth()
  const [q, setQ] = useState(initialPhone || initialName)
  const [rows, setRows] = useState<PatientRow[]>([])
  const [creating, setCreating] = useState(false)

  useEffect(() => {
    const t = window.setTimeout(() => {
      api<PatientRow[]>(`/api/patients?take=10&q=${encodeURIComponent(q)}`).then(setRows).catch(() => setRows([]))
    }, 250)
    return () => window.clearTimeout(t)
  }, [q])

  if (creating) {
    return (
      <PatientForm
        initial={{ fullName: initialName, phone: initialPhone }}
        showMedical={isClinical(session?.user.role)}
        onSaved={(p) => onPick(p)}
        onCancel={() => setCreating(false)}
      />
    )
  }

  return (
    <div className="stack">
      <div className="row">
        <div className="search" style={{ flex: 1 }}>
          <Icon name="search" />
          <input className="input" placeholder="بحث بالاسم أو الهاتف أو رقم الملف" value={q} onChange={(e) => setQ(e.target.value)} autoFocus />
        </div>
        <button className="btn soft" onClick={() => setCreating(true)}><Icon name="userPlus" /> مريض جديد</button>
      </div>
      <div className="stack" style={{ maxHeight: 340, overflowY: 'auto', gap: 8 }}>
        {rows.map((p) => (
          <div key={p.id} className="list-item" onClick={() => onPick(p)}>
            <div>
              <div className="cell-main">{p.fullName}</div>
              <div className="cell-sub">ملف {p.code}{p.phone ? <> · <span className="ltr">{p.phone}</span></> : ''}</div>
            </div>
            <span className="badge gray plain">{p.visitsCount ?? 0} زيارة</span>
          </div>
        ))}
        {rows.length === 0 && <div className="empty"><Icon name="search" size={28} /> لا توجد نتائج</div>}
      </div>
    </div>
  )
}
