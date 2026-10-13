import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { AuthProvider, RequireAuth, RequireOwner, useAuth } from './auth'
import { ErrorBoundary } from './components/ErrorBoundary'
import { Layout } from './components/Layout'
import { Appointments } from './pages/Appointments'
import { Assistant } from './pages/Assistant'
import { Dashboard } from './pages/Dashboard'
import { Invoices } from './pages/Invoices'
import { Login } from './pages/Login'
import { PatientDetail } from './pages/PatientDetail'
import { Patients } from './pages/Patients'
import { PrintDocument, PrintPatientFile, PrintPrescription } from './pages/Print'
import { Queue } from './pages/Queue'
import { Settings } from './pages/Settings'
import { OwnerHome } from './pages/owner/OwnerHome'
import { OwnerLogin } from './pages/owner/OwnerLogin'
import { DoctorSite } from './pages/public/DoctorSite'
import { QueueDisplay } from './pages/public/QueueDisplay'
import { Track } from './pages/public/Track'


export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        {/* PLAN_ADDENDUM.md 7.10: the whole tree is caught — no white screen, ever. */}
        <ErrorBoundary>
        <Routes>
          <Route path="/" element={<Navigate to="/app" replace />} />
          <Route path="/login" element={<Login />} />

          {/* The installation owner's own panel (PLAN.md 1.1), deliberately outside the clinic shell. */}
          <Route path="/owner/login" element={<OwnerLogin />} />
          <Route path="/owner" element={<RequireOwner><OwnerHome /></RequireOwner>} />

          <Route path="/c/:slug" element={<DoctorSite />} />
          <Route path="/c/:slug/display" element={<QueueDisplay />} />
          <Route path="/track/:token" element={<Track />} />

          <Route path="/app" element={<RequireAuth><Layout /></RequireAuth>}>
            <Route index element={<IndexLanding />} />
            <Route path="queue" element={<Queue />} />
            <Route path="appointments" element={<Appointments />} />
            <Route path="patients" element={<Patients />} />
            <Route path="patients/:id" element={<PatientDetail />} />
            <Route path="invoices" element={<Invoices />} />
            {/* PLAN.md 1.4: the assistant page is gated by the assistant permission, not the doctor/admin roles. */}
            <Route path="assistant" element={<RequireAuth permissions={['assistant']}><Assistant /></RequireAuth>} />
            {/* PLAN.md 1.3: the library page moved to the owner's panel (/owner), off every clinic account's routes. */}
            <Route path="settings" element={<Settings />} />
          </Route>

          <Route path="/print/visit/:id" element={<RequireAuth permissions={['assistant']}><PrintPrescription /></RequireAuth>} />
          {/* PLAN_ADDENDUM.md 9.3: the printable patient file behind the admin's "تصدير PDF" button. */}
          <Route path="/print/patient/:id" element={<RequireAuth permissions={['admin']}><PrintPatientFile /></RequireAuth>} />
          <Route path="/print/document" element={<RequireAuth permissions={['assistant']}><PrintDocument /></RequireAuth>} />
          <Route path="*" element={<Navigate to="/app" replace />} />
        </Routes>
        </ErrorBoundary>
      </BrowserRouter>
    </AuthProvider>
  )
}

/**
 * PLAN.md 1.4: the dashboard is a page like any other — an account the admin customised may not hold it.
 * Landing on /app sends such an account to the first page it actually has; with no page at all the dashboard
 * renders and its own API answers say the account has no permission.
 */
function IndexLanding() {
  const { permissions } = useAuth()
  if (permissions.includes('dashboard')) return <Dashboard />
  const first = ['queue', 'appointments', 'patients', 'invoices'].find((p) => permissions.includes(p))
  if (first) return <Navigate to={`/app/${first}`} replace />
  return <Dashboard />
}
