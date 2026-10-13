import { useState, type FormEvent } from 'react'
import { api, errorMessage } from '../api'
import { Icon } from './Icon'

export interface PatientFormValue {
  fullName: string
  birthDate: string
  gender: string
  phone: string
  bloodType: string
  chronicConditions: string
  allergies: string
  notes: string
}

export const emptyPatient: PatientFormValue = {
  fullName: '', birthDate: '', gender: '', phone: '', bloodType: '', chronicConditions: '', allergies: '', notes: '',
}

/** Creates (no id) or updates a patient. Medical fields are only shown to doctor/admin; the API ignores them for reception anyway. */
export function PatientForm({ id, initial, showMedical, onSaved, onCancel }: {
  id?: number
  initial?: Partial<PatientFormValue>
  showMedical: boolean
  onSaved: (patient: { id: number; code?: string; fullName: string; phone?: string }) => void
  onCancel?: () => void
}) {
  const [v, setV] = useState<PatientFormValue>({ ...emptyPatient, ...initial })
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  const set = (k: keyof PatientFormValue) => (e: { target: { value: string } }) => setV({ ...v, [k]: e.target.value })

  async function submit(e: FormEvent) {
    e.preventDefault()
    setSaving(true)
    setError('')
    const body = { ...v, birthDate: v.birthDate || null, gender: v.gender || null }
    try {
      if (id) {
        await api(`/api/patients/${id}`, { method: 'PUT', body })
        onSaved({ id, fullName: v.fullName, phone: v.phone })
      } else {
        const r = await api<{ id: number; code: string }>('/api/patients', { body })
        onSaved({ id: r.id, code: r.code, fullName: v.fullName, phone: v.phone })
      }
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={submit} className="stack" style={{ gap: 16 }}>
      {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
      <div className="section-title">البيانات الأساسية</div>
      <div className="grid cols-2">
        <div className="field">
          <label>الاسم الكامل</label>
          <input className="input" value={v.fullName} onChange={set('fullName')} required autoFocus />
        </div>
        <div className="field">
          <label>رقم الهاتف</label>
          <input className="input" dir="ltr" value={v.phone} onChange={set('phone')} inputMode="tel" />
        </div>
        <div className="field">
          <label>تاريخ الميلاد</label>
          <input className="input" type="date" value={v.birthDate} onChange={set('birthDate')} />
        </div>
        <div className="field">
          <label>النوع</label>
          <select className="input" value={v.gender} onChange={set('gender')}>
            <option value="">—</option>
            <option value="ذكر">ذكر</option>
            <option value="أنثى">أنثى</option>
          </select>
        </div>
      </div>
      {showMedical && (
        <>
          <div className="section-title">البيانات الطبية</div>
          <div className="grid cols-2">
            <div className="field">
              <label>فصيلة الدم</label>
              <select className="input" value={v.bloodType} onChange={set('bloodType')}>
                <option value="">—</option>
                {['A+', 'A-', 'B+', 'B-', 'AB+', 'AB-', 'O+', 'O-'].map((b) => <option key={b}>{b}</option>)}
              </select>
            </div>
            <div className="field">
              <label>الحساسية</label>
              <input className="input" value={v.allergies} onChange={set('allergies')} />
            </div>
          </div>
          <div className="field">
            <label>الأمراض المزمنة</label>
            <input className="input" value={v.chronicConditions} onChange={set('chronicConditions')} />
          </div>
          <div className="field">
            <label>ملاحظات</label>
            <textarea className="input" value={v.notes} onChange={set('notes')} />
          </div>
        </>
      )}
      <div className="row end">
        {onCancel && <button type="button" className="btn ghost" onClick={onCancel}>إلغاء</button>}
        <button className="btn" disabled={saving}>{saving ? <span className="spinner" /> : <Icon name="check" />} حفظ</button>
      </div>
    </form>
  )
}
