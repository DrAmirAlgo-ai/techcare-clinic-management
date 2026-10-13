import { useState } from 'react'
import { api, errorMessage } from '../api'
import { methodLabel, money, typeLabel, type AppointmentType, type PaymentMethod } from '../format'
import { Icon } from './Icon'
import { Modal } from './Modal'
import { PatientPicker, type PickedPatient } from './PatientPicker'

export interface Fees { consultationFee: number; followUpFee: number }

export interface ArrivalTarget {
  appointmentId: number
  patientId?: number
  patientName: string
  phone?: string
  type: AppointmentType
}

const feeFor = (fees: Fees, type: AppointmentType) => (type === 'Consultation' ? fees.consultationFee : fees.followUpFee)

function PaymentFields({ amount, setAmount, discount, setDiscount, method, setMethod }: {
  amount: string; setAmount: (v: string) => void
  discount: string; setDiscount: (v: string) => void
  method: PaymentMethod; setMethod: (m: PaymentMethod) => void
}) {
  const net = Number(amount || 0) - Number(discount || 0)
  return (
    <>
      <div className="grid cols-2">
        <div className="field"><label>المبلغ</label><input className="input" value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" /></div>
        <div className="field"><label>الخصم</label><input className="input" value={discount} onChange={(e) => setDiscount(e.target.value)} inputMode="decimal" /></div>
      </div>
      <div className="row between">
        <div className="segmented">
          {(['Cash', 'Card', 'Wallet'] as PaymentMethod[]).map((m) => (
            <button key={m} type="button" className={method === m ? 'active' : ''} onClick={() => setMethod(m)}>{methodLabel[m]}</button>
          ))}
        </div>
        <div className="strong" style={{ fontSize: 18 }}>{money(net)}</div>
      </div>
    </>
  )
}

/** Front-desk arrival in one step: pick (or link) the patient file, choose the visit type, and take the fee. */
export function ArrivalModal({ target, fees, onClose, onDone }: {
  target?: ArrivalTarget
  fees: Fees
  onClose: () => void
  onDone: (warning?: string) => void
}) {
  const [patient, setPatient] = useState<PickedPatient | null>(target?.patientId ? { id: target.patientId, fullName: target.patientName } : null)
  const [type, setType] = useState<AppointmentType>(target?.type ?? 'Consultation')
  const [urgent, setUrgent] = useState(false)
  const [payNow, setPayNow] = useState(true)
  const [amount, setAmount] = useState(String(feeFor(fees, target?.type ?? 'Consultation')))
  const [discount, setDiscount] = useState('0')
  const [method, setMethod] = useState<PaymentMethod>('Cash')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  function chooseType(t: AppointmentType) {
    setType(t)
    setAmount(String(feeFor(fees, t)))
  }

  async function submit() {
    if (!patient) return
    setBusy(true)
    setError('')
    let appointmentId: number
    try {
      if (target) {
        if (!target.patientId) await api(`/api/appointments/${target.appointmentId}/link-patient`, { body: { patientId: patient.id } })
        await api(`/api/queue/${target.appointmentId}/check-in`, { method: 'POST', body: {} })
        appointmentId = target.appointmentId
      } else {
        const r = await api<{ id: number }>('/api/queue/walk-in', { body: { patientId: patient.id, type, isUrgent: urgent } })
        appointmentId = r.id
      }
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
      return
    }

    const net = Number(amount || 0) - Number(discount || 0)
    if (payNow && Number(amount) > 0) {
      try {
        await api('/api/invoices', {
          body: { patientId: patient.id, visitId: null, appointmentId, description: typeLabel[type], amount: Number(amount), discount: Number(discount || 0), paid: net, method },
        })
      } catch (e) {
        onDone(`تم تسجيل الوصول، ولم يتم حفظ الدفع: ${errorMessage(e)}`)
        return
      }
    }
    onDone()
  }

  return (
    <Modal title={target ? `تسجيل وصول: ${target.patientName}` : 'تسجيل وصول مريض'} onClose={onClose}>
      {!patient ? (
        <PatientPicker onPick={setPatient} initialName={target?.patientName} initialPhone={target?.phone} />
      ) : (
        <div className="stack" style={{ gap: 16 }}>
          {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
          <div className="selected-box">
            <div className="row tight"><Icon name="user" /> <b>{patient.fullName}</b></div>
            {!target?.patientId && <button className="btn sm ghost" onClick={() => setPatient(null)}>تغيير</button>}
          </div>

          <div className="grid cols-2" style={{ gap: 8 }}>
            {(['Consultation', 'FollowUp'] as AppointmentType[]).map((t) => (
              <button key={t} type="button" className={`btn ${type === t ? 'soft' : 'ghost'}`} onClick={() => chooseType(t)}>
                {typeLabel[t]} · {money(feeFor(fees, t))}
              </button>
            ))}
          </div>
          {!target && <label className="check"><input type="checkbox" checked={urgent} onChange={(e) => setUrgent(e.target.checked)} /> حالة طارئة</label>}

          <div className="section-title row between" style={{ alignItems: 'center' }}>
            <span>الدفع</span>
            <div className="segmented">
              <button type="button" className={payNow ? 'active' : ''} onClick={() => setPayNow(true)}>دفع الآن</button>
              <button type="button" className={!payNow ? 'active' : ''} onClick={() => setPayNow(false)}>لاحقاً</button>
            </div>
          </div>
          {payNow && <PaymentFields amount={amount} setAmount={setAmount} discount={discount} setDiscount={setDiscount} method={method} setMethod={setMethod} />}

          <div className="row end">
            <button className="btn lg" onClick={submit} disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="check" />} تسجيل الوصول</button>
          </div>
        </div>
      )}
    </Modal>
  )
}

export interface CollectTarget {
  appointmentId: number
  patientId?: number
  patientName: string
  type: AppointmentType
  invoiceId?: number
  amountDue: number
  amountPaid: number
}

/** Collects the visit fee: creates the visit's invoice, or takes the rest of a partly paid one. */
export function CollectFeeModal({ target, fees, onClose, onDone }: {
  target: CollectTarget
  fees: Fees
  onClose: () => void
  onDone: () => void
}) {
  const remaining = target.amountDue - target.amountPaid
  const [amount, setAmount] = useState(String(target.invoiceId ? remaining : feeFor(fees, target.type)))
  const [discount, setDiscount] = useState('0')
  const [method, setMethod] = useState<PaymentMethod>('Cash')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit() {
    setBusy(true)
    setError('')
    try {
      if (target.invoiceId) {
        await api(`/api/invoices/${target.invoiceId}/payments`, { body: { amount: Number(amount) } })
      } else {
        const net = Number(amount || 0) - Number(discount || 0)
        await api('/api/invoices', {
          body: { patientId: target.patientId, visitId: null, appointmentId: target.appointmentId, description: typeLabel[target.type], amount: Number(amount), discount: Number(discount || 0), paid: net, method },
        })
      }
      onDone()
    } catch (e) {
      setError(errorMessage(e))
      setBusy(false)
    }
  }

  return (
    <Modal title={`تحصيل: ${target.patientName}`} onClose={onClose}>
      <div className="stack" style={{ gap: 16 }}>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        {target.invoiceId ? (
          <>
            <div className="ai-box">
              <div className="kv"><span>قيمة الزيارة</span><span>{money(target.amountDue)}</span></div>
              <div className="kv"><span>المدفوع</span><span>{money(target.amountPaid)}</span></div>
              <div className="kv"><span>المتبقي</span><span className="danger-text">{money(remaining)}</span></div>
            </div>
            <div className="field"><label>المبلغ المحصّل</label><input className="input" value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" autoFocus /></div>
          </>
        ) : (
          <>
            <div className="selected-box"><div className="row tight"><Icon name="receipt" /> <b>{typeLabel[target.type]}</b></div></div>
            <PaymentFields amount={amount} setAmount={setAmount} discount={discount} setDiscount={setDiscount} method={method} setMethod={setMethod} />
          </>
        )}
        <div className="row end"><button className="btn lg" onClick={submit} disabled={busy}>{busy ? <span className="spinner" /> : <Icon name="check" />} تأكيد التحصيل</button></div>
      </div>
    </Modal>
  )
}
