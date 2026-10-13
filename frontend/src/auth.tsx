import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { api, clearSession, loadSession, saveSession, type Role, type Session } from './api'

interface AuthState {
  session: Session | null
  /** PLAN_ADDENDUM.md 7.2: the clinic code is only sent when the username exists at more than one clinic. */
  login: (username: string, password: string, clinicSlug?: string) => Promise<Session>
  logout: () => void
  /** PLAN.md 1.4: the account's effective permission set (login/me payload, role defaults for old sessions). */
  permissions: string[]
  hasRole: (...roles: Role[]) => boolean
  hasPermission: (key: string) => boolean
}

/**
 * PLAN.md 1.4: what each role gets when the server carries no explicit set — mirrors the backend catalog
 * (Infrastructure\Permissions.cs). Used only for sessions saved before the permission list joined the login
 * response; fresh logins always carry the account's effective set.
 */
export const ROLE_DEFAULT_PERMISSIONS: Record<Role, string[]> = {
  Admin: ['dashboard', 'queue', 'appointments', 'patients', 'invoices', 'assistant', 'admin'],
  Doctor: ['dashboard', 'queue', 'appointments', 'patients', 'invoices', 'assistant'],
  Reception: ['dashboard', 'queue', 'appointments', 'patients', 'invoices'],
  SuperAdmin: [],
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => loadSession())

  // Resolves to the new session so a caller can route by role straight away (the owner panel signs in here too).
  const login = useCallback(async (username: string, password: string, clinicSlug?: string) => {
    const s = await api<Session>('/api/auth/login', { body: { username, password, clinicSlug: clinicSlug || null } })
    saveSession(s)
    setSession(s)
    return s
  }, [session])

  const logout = useCallback(() => {
    clearSession()
    setSession(null)
  }, [session])

  const hasRole = useCallback((...roles: Role[]) => !!session && roles.includes(session.user.role), [session])

  const permissions = useMemo(
    () => (session ? session.user.permissions ?? ROLE_DEFAULT_PERMISSIONS[session.user.role] ?? [] : []),
    [session],
  )

  const hasPermission = useCallback((key: string) => permissions.includes(key), [permissions])

  const value = useMemo(
    () => ({ session, login, logout, permissions, hasRole, hasPermission }),
    [session, login, logout, permissions, hasRole, hasPermission],
  )
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}

export function RequireAuth({
  children,
  roles,
  permissions: requiredPermissions,
}: {
  children: ReactNode
  roles?: Role[]
  /** PLAN.md 1.4: the account must hold every listed permission key (the API enforces the same rule). */
  permissions?: string[]
}) {
  const { session, permissions } = useAuth()
  const location = useLocation()
  if (!session) return <Navigate to="/login" state={{ from: location.pathname }} replace />
  // The owner (PLAN.md 1.1) belongs to no clinic, so the clinic shell has nothing to show it: its panel is /owner.
  if (session.user.role === 'SuperAdmin') return <Navigate to="/owner" replace />
  if (roles && !roles.includes(session.user.role)) return <Navigate to="/app" replace />
  if (requiredPermissions && !requiredPermissions.every((p) => permissions.includes(p)))
    return <Navigate to="/app" replace />
  return <>{children}</>
}

/** Guards the owner panel (PLAN.md 1.1). A clinic account — or nobody at all — never gets past this. */
export function RequireOwner({ children }: { children: ReactNode }) {
  const { session } = useAuth()
  if (!session) return <Navigate to="/owner/login" replace />
  if (session.user.role !== 'SuperAdmin') return <Navigate to="/app" replace />
  return <>{children}</>
}

export const isOwner = (role?: Role) => role === 'SuperAdmin'

export const isClinical = (role?: Role) => role === 'Doctor' || role === 'Admin'
