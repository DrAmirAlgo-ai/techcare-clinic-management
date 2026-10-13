import type { Role } from './api'

export const roleLabel: Record<Role, string> = {
  Admin: 'مدير العيادة',
  Doctor: 'طبيب',
  Reception: 'استقبال',
  SuperAdmin: 'مالك النظام',
}

export type AppointmentStatus = 'Booked' | 'Waiting' | 'InConsultation' | 'Done' | 'Cancelled' | 'NoShow'
export type AppointmentType = 'Consultation' | 'FollowUp'
export type PaymentMethod = 'Cash' | 'Card' | 'Wallet'

export const statusLabel: Record<AppointmentStatus, string> = {
  Booked: 'محجوز',
  Waiting: 'في الانتظار',
  InConsultation: 'في الكشف',
  Done: 'تم الكشف',
  Cancelled: 'ملغي',
  NoShow: 'لم يحضر',
}

export const statusTone: Record<AppointmentStatus, string> = {
  Booked: 'blue',
  Waiting: 'amber',
  InConsultation: 'green',
  Done: 'gray',
  Cancelled: 'red',
  NoShow: 'red',
}

export const typeLabel: Record<AppointmentType, string> = {
  Consultation: 'كشف',
  FollowUp: 'استشارة',
}

export type PaymentState = 'Unpaid' | 'Partial' | 'Paid'

export const paymentLabel: Record<PaymentState, string> = { Unpaid: 'لم يدفع', Partial: 'دفع جزء', Paid: 'مدفوع' }
export const paymentTone: Record<PaymentState, string> = { Unpaid: 'red', Partial: 'amber', Paid: 'green' }

export const methodLabel: Record<PaymentMethod, string> = {
  Cash: 'نقدي',
  Card: 'بطاقة',
  Wallet: 'محفظة إلكترونية',
}

export const dayLabel: Record<string, string> = {
  Sat: 'السبت', Sun: 'الأحد', Mon: 'الاثنين', Tue: 'الثلاثاء', Wed: 'الأربعاء', Thu: 'الخميس', Fri: 'الجمعة',
}

const pad = (n: number) => String(n).padStart(2, '0')

/** yyyy-MM-dd in the browser's local time (the clinic's time zone). */
export function isoDate(d: Date = new Date()) {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** yyyy-MM-ddTHH:mm without a time zone, the format the API stores as clinic-local time. */
export function localDateTime(d: Date) {
  return `${isoDate(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export function fmtTime(value?: string | null) {
  if (!value) return ''
  return new Date(value).toLocaleTimeString('ar-EG-u-nu-latn', { hour: 'numeric', minute: '2-digit' })
}

/** "HH:mm" → "5:30 م" */
export function to12h(t: string) {
  const [h, m] = t.split(':').map(Number)
  return `${((h + 11) % 12) + 1}:${String(m).padStart(2, '0')} ${h >= 12 ? 'م' : 'ص'}`
}

export function fmtDate(value?: string | null) {
  if (!value) return ''
  return new Date(value).toLocaleDateString('ar-EG-u-nu-latn', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })
}

export function fmtShortDate(value?: string | null) {
  if (!value) return ''
  return new Date(value).toLocaleDateString('ar-EG-u-nu-latn', { day: 'numeric', month: 'short', year: 'numeric' })
}

export function money(n?: number | null) {
  return `${(n ?? 0).toLocaleString('ar-EG-u-nu-latn')} ج.م`
}

export function minutesLabel(m?: number | null) {
  if (m == null) return ''
  if (m <= 0) return 'الآن'
  if (m < 60) return `${m} دقيقة`
  const h = Math.floor(m / 60)
  const r = m % 60
  return r ? `${h} ساعة و ${r} دقيقة` : `${h} ساعة`
}
