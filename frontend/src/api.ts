export type Role = 'Admin' | 'Doctor' | 'Reception' | 'SuperAdmin'

/** The clinic a signed-in account belongs to. The owner (PLAN.md 1.1) belongs to none, so the session holds null. */
export interface SessionClinic {
  id: number
  name: string
  slug: string
  doctorName: string
  // PLAN.md 10.2/10.3: the subscription numbers the layout's bar reports; sessions saved before the fields
  // joined the login payload lack them, hence optional.
  plan?: string
  subscriptionStatus?: string
  daysRemaining?: number | null
  aiEnabled?: boolean
  // PLAN.md 10.7: the clinic's own display name and whether its logo is uploaded; the sidebar shows them
  // instead of the platform default. Optional for sessions saved before the fields existed.
  displayName?: string
  hasLogo?: boolean
  // PLAN.md 10.12: prescription header fields.
  qualifications?: string
  workingHoursText?: string
}

export interface Session {
  token: string
  expiresAtUtc: string
  /** PLAN.md 1.4: the account's effective permission set (its role's defaults unless the admin customised it). */
  user: { id: number; fullName: string; username: string; role: Role; permissions?: string[] }
  clinic: SessionClinic | null
  /** PLAN_ADDENDUM.md 7.5: the account still carries its temporary password; the app forces the change. */
  mustChangePassword?: boolean
}

const SESSION_KEY = 'tc_clinic_session'

export function loadSession(): Session | null {
  const raw = localStorage.getItem(SESSION_KEY)
  if (!raw) return null
  try {
    const s = JSON.parse(raw) as Session
    return new Date(s.expiresAtUtc) > new Date() ? s : null
  } catch {
    return null
  }
}

export function saveSession(s: Session) {
  localStorage.setItem(SESSION_KEY, JSON.stringify(s))
}

export function clearSession() {
  localStorage.removeItem(SESSION_KEY)
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

interface RequestOptions {
  method?: string
  body?: unknown
  form?: FormData
}

export async function api<T = void>(path: string, options: RequestOptions = {}): Promise<T> {
  const session = loadSession()
  const headers: Record<string, string> = {}
  if (session) headers.Authorization = `Bearer ${session.token}`

  let body: BodyInit | undefined
  if (options.form) {
    body = options.form
  } else if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(options.body)
  }

  let res: Response
  try {
    res = await fetch(path, { method: options.method ?? (body ? 'POST' : 'GET'), headers, body })
  } catch {
    // PLAN_ADDENDUM.md 7.10: the browser could not reach the server at all (network down, server stopped) —
    // surface it as the same unified error every page already knows how to show.
    throw new ApiError(0, 'تعذّر الوصول إلى الخادم — تحقّق من اتصالك بالإنترنت')
  }

  if (res.status === 401 && session && !path.startsWith('/api/auth/login')) {
    // PLAN_ADDENDUM.md 7.10: unified 401 — the session is dropped and the login page says why.
    sessionStorage.setItem('tc_session_expired', '1')
    clearSession()
    window.location.href = '/login'
  }

  if (!res.ok) {
    let message = `حدث خطأ (${res.status})`
    try {
      const data = await res.json()
      if (data?.message) message = data.message
      else if (data?.title) message = data.title
    } catch {
      /* non-JSON error body */
    }
    if (res.status === 403 && message.startsWith('حدث خطأ')) message = 'ليس لديك صلاحية لتنفيذ هذا الإجراء'
    if (res.status === 429) message = 'تم تجاوز عدد المحاولات المسموح، يرجى المحاولة لاحقاً'
    throw new ApiError(res.status, message)
  }

  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : 'حدث خطأ غير متوقع'
}

/**
 * PLAN.md 1.3: opens the full cited file (GET api/library/documents/:id, doctor/admin only) in a new tab. A plain
 * link cannot carry the bearer token, so the file is fetched with the session and opened as a blob; the server
 * answers with the file's own bytes only — its folder path never travels to the browser.
 */
export async function openLibraryDocument(documentId: number): Promise<void> {
  const session = loadSession()
  const res = await fetch(`/api/library/documents/${documentId}`, {
    headers: session ? { Authorization: `Bearer ${session.token}` } : {},
  })
  if (!res.ok) {
    let message = `تعذّر فتح الملف (${res.status})`
    try {
      const data = await res.json()
      if (data?.message) message = data.message
    } catch {
      /* binary error body */
    }
    throw new ApiError(res.status, message)
  }
  const url = URL.createObjectURL(await res.blob())
  window.open(url, '_blank', 'noopener')
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
