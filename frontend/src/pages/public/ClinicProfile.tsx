import { useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api, errorMessage } from '../../api'
import { Icon } from '../../components/Icon'

export function ClinicProfile() {
  const { slug } = useParams<{ slug: string }>()
  const [clinic, setClinic] = useState<any>(null)
  const [error, setError] = useState<string>('')
  const [loading, setLoading] = useState<boolean>(true)

  useEffect(() => {
    if (!slug) {
      setError('slug غير محدد')
      setLoading(false)
      return
    }

    api<any>(`/api/public/clinics/${slug}`).then(
      (data) => {
        setClinic(data)
        setLoading(false)
      },
      (err) => {
        setError(errorMessage(err))
        setLoading(false)
      }
    )
  }, [slug])

  if (loading) return <div className="loading"><span className="spinner dark lg" /></div>
  if (error) return <div className="alert error"><Icon name="alert" /> {error}</div>
  if (!clinic) return <div className="alert error"><Icon name="alert" /> العيادة غير موجودة</div>

  return (
    <div className="page">
      <div className="clinic-profile card stack" style={{ gap: 24 }}>
        <div className="clinic-info">
          {clinic.hasLogo && clinic.slug && (
            <img src={`/api/public/clinics/${clinic.slug}/logo`} alt="" style={{ width: 80, height: 80, objectFit: 'contain' }} />
          )}
          <div>
            <h1>{clinic.displayName ?? clinic.name}</h1>
            <div className="muted strong">{clinic.specialty}</div>
          </div>
        </div>
        <div className="mt">
          <p>{clinic.about ?? 'لا يوجد وصف متاح'}</p>
        </div>
        <div className="mt">
          <p><strong>الهاتف:</strong> {clinic.phone}</p>
          <p><strong>العنوان:</strong> {clinic.address}</p>
        </div>
        <div className="mt">
          <p><strong>رسوم الكشف:</strong> {clinic.consultationFee} ج.م</p>
          <p><strong>رسوم المتابعة:</strong> {clinic.followUpFee} ج.م</p>
        </div>
        <div className="mt">
          <a href={`/appointments/new?clinicId=${clinic.id}`} className="btn">احجز الآن</a>
        </div>
      </div>
    </div>
  )
}