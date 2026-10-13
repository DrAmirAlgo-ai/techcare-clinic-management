import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api, errorMessage } from '../api'
import { Icon, type IconName } from '../components/Icon'
import { Modal } from '../components/Modal'
import { fmtShortDate, fmtTime, isoDate, methodLabel, money, type PaymentMethod } from '../format'

interface Row {
  id: number
  createdAt: string
  description: string
  amount: number
  discount: number
  paid: number
  method: PaymentMethod
  patientId: number
  patientName: string
  patientCode: string
  net: number
  remaining: number
}

interface Result { items: Row[]; totals: { net: number; paid: number; remaining: number; count: number } }

function Stat({ icon, tone, label, value }: { icon: IconName; tone: string; label: string; value: string | number }) {
  return (
    <div className="card stat-card">
      <span className={`stat-icon ${tone}`}><Icon name={icon} size={22} /></span>
      <div><div className="stat-label">{label}</div><div className="stat-value">{value}</div></div>
    </div>
  )
}

export function Invoices() {
  const [from, setFrom] = useState(isoDate())
  const [to, setTo] = useState(isoDate())
  const [data, setData] = useState<Result | null>(null)
  const [error, setError] = useState('')
  const [collecting, setCollecting] = useState<Row | null>(null)

  const load = useCallback(() => {
    api<Result>(`/api/invoices?from=${from}&to=${to}`).then(setData).catch((e) => setError(errorMessage(e)))
  }, [from, to])
  useEffect(load, [load])

  return (
    <>
      <div className="page-head">
        <h1>الحسابات</h1>
        <div className="row">
          <div className="field" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            <label>من</label>
            <input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} style={{ width: 160 }} />
          </div>
          <div className="field" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            <label>إلى</label>
            <input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} style={{ width: 160 }} />
          </div>
        </div>
      </div>
      {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}
      {!data ? <div className="loading"><span className="spinner dark lg" /></div> : (
        <>
          <div className="grid cols-4">
            <Stat icon="receipt" tone="blue" label="عدد الفواتير" value={data.totals.count} />
            <Stat icon="trendUp" tone="violet" label="إجمالي الصافي" value={money(data.totals.net)} />
            <Stat icon="cash" tone="green" label="المحصّل" value={money(data.totals.paid)} />
            <Stat icon="clock" tone="amber" label="المتبقي" value={money(data.totals.remaining)} />
          </div>
          <div className="card flush mt">
            {data.items.length === 0 ? <div className="empty"><Icon name="receipt" size={30} /> لا توجد فواتير في هذه الفترة</div> : (
              <div className="table-wrap">
                <table className="table">
                  <thead><tr><th>التاريخ</th><th>المريض</th><th>البيان</th><th>الصافي</th><th>المدفوع</th><th>المتبقي</th><th>طريقة الدفع</th><th></th></tr></thead>
                  <tbody>
                    {data.items.map((i) => (
                      <tr key={i.id}>
                        <td className="nowrap"><div>{fmtShortDate(i.createdAt)}</div><div className="cell-sub">{fmtTime(i.createdAt)}</div></td>
                        <td><Link className="cell-main" to={`/app/patients/${i.patientId}`}>{i.patientName}</Link><div className="cell-sub">ملف {i.patientCode}</div></td>
                        <td>{i.description}</td>
                        <td className="nowrap">{money(i.net)}</td>
                        <td className="nowrap">{money(i.paid)}</td>
                        <td className="nowrap">{i.remaining > 0 ? <span className="danger-text">{money(i.remaining)}</span> : <span className="badge green">مسدد</span>}</td>
                        <td>{methodLabel[i.method]}</td>
                        <td><div className="actions">{i.remaining > 0 && <button className="btn sm soft" onClick={() => setCollecting(i)}><Icon name="cash" size={15} /> تحصيل</button>}</div></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
      {collecting && <CollectModal row={collecting} onClose={() => setCollecting(null)} onSaved={() => { setCollecting(null); load() }} />}
    </>
  )
}

function CollectModal({ row, onClose, onSaved }: { row: Row; onClose: () => void; onSaved: () => void }) {
  const [amount, setAmount] = useState(String(row.remaining))
  const [error, setError] = useState('')

  async function save() {
    try {
      await api(`/api/invoices/${row.id}/payments`, { body: { amount: Number(amount) } })
      onSaved()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  return (
    <Modal title="تحصيل دفعة" onClose={onClose}>
      <div className="stack" style={{ gap: 16 }}>
        {error && <div className="alert error"><Icon name="alert" /> {error}</div>}
        <div className="ai-box">
          <div className="kv"><span>المريض</span><span>{row.patientName}</span></div>
          <div className="kv"><span>البيان</span><span>{row.description}</span></div>
          <div className="kv"><span>المتبقي</span><span className="danger-text">{money(row.remaining)}</span></div>
        </div>
        <div className="field"><label>المبلغ المحصّل</label><input className="input" value={amount} onChange={(e) => setAmount(e.target.value)} inputMode="decimal" autoFocus /></div>
        <div className="row end"><button className="btn" onClick={save}><Icon name="check" /> تأكيد التحصيل</button></div>
      </div>
    </Modal>
  )
}
