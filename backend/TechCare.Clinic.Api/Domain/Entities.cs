namespace TechCare.Clinic.Api.Domain;

public interface IClinicOwned
{
    int ClinicId { get; set; }
}

/// <summary>
/// A row whose clinic is optional: the SuperAdmin owner account (PLAN.md 1.1) is signed in without a clinic.
/// It is still filtered by the current clinic exactly like <see cref="IClinicOwned"/>, so one clinic never sees
/// another clinic's accounts — and with no clinic in the request only the owner account itself is visible.
/// </summary>
public interface IClinicOptional
{
    int? ClinicId { get; set; }
}

public enum UserRole
{
    Admin = 1,
    Doctor = 2,
    Reception = 3,
    /// <summary>
    /// Owner of the whole installation (PLAN.md 1.1): not a member of any clinic, so it is deliberately kept out
    /// of the clinic role sets in <c>Infrastructure\Roles.cs</c> and reaches the API only through <c>api/owner</c>.
    /// Kept away from 0 on purpose, so a default(UserRole) can never be the most powerful role.
    /// </summary>
    SuperAdmin = 4
}

public enum AppointmentStatus
{
    Booked = 1,
    Waiting = 2,
    InConsultation = 3,
    Done = 4,
    Cancelled = 5,
    NoShow = 6
}

public enum AppointmentType
{
    Consultation = 1,
    FollowUp = 2
}

public enum AppointmentSource
{
    Reception = 1,
    Online = 2
}

public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    Wallet = 3
}

/// <summary>
/// PLAN.md 10.2 — the commercial plan a clinic is on. Basic keeps the clinic fully working without the AI
/// assistant (the 10.3 plan gate locks it); Pro unlocks everything.
/// </summary>
public enum ClinicPlan
{
    Basic = 1,
    Pro = 2
}

/// <summary>
/// PLAN.md 10.2 — the lifecycle of a clinic's subscription. A fresh clinic starts in trial; the owner switches
/// the state and renews it from the owner panel (10.4).
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>Free trial period; its end date sits on <c>TrialEndsAtUtc</c>.</summary>
    Trial = 1,

    /// <summary>Paid and in service; its end date sits on <c>SubscriptionEndsAtUtc</c> (null means no end date).</summary>
    Active = 2,

    /// <summary>Stopped by the owner; the clinic is read-only until it is reactivated.</summary>
    Suspended = 3,

    /// <summary>Marked ended by the owner (the end date passed without renewal).</summary>
    Expired = 4
}

public class Clinic
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string DoctorName { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string? About { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public decimal ConsultationFee { get; set; }
    public decimal FollowUpFee { get; set; }
    public int AvgMinutesPerPatient { get; set; } = 15;

    /// <summary>
    /// PLAN_ADDENDUM.md 9.3: how many days a logically deleted patient file is kept before the retention
    /// service may physically remove it. The clinic admin tunes it in the clinic settings.
    /// </summary>
    public int DataRetentionDays { get; set; } = 30;
    public TimeOnly WorkStart { get; set; } = new(17, 0);
    public TimeOnly WorkEnd { get; set; } = new(22, 0);
    public string WorkDays { get; set; } = "Sat,Sun,Mon,Tue,Wed,Thu";
    public string TimeZoneId { get; set; } = "Africa/Cairo";
    public bool OnlineBookingEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // ── PLAN.md 10.2: the subscription. A clinic registered from the owner panel starts on Basic in the free
    //    trial; the owner switches plans, renews and records payments from the panel (10.4).
    public ClinicPlan Plan { get; set; } = ClinicPlan.Basic;
    public SubscriptionStatus SubscriptionStatus { get; set; } = SubscriptionStatus.Trial;
    public DateTime? TrialEndsAtUtc { get; set; }
    public DateTime? SubscriptionEndsAtUtc { get; set; }

    /// <summary>
    /// PLAN.md 10.3 — the per-clinic AI switch: a Pro clinic with this flag off is locked out of the assistant
    /// too, so the owner can cut the AI without downgrading the whole plan.
    /// </summary>
    public bool AiEnabled { get; set; } = true;

    /// <summary>Per-clinic override of the free-trial length in days; null uses the installation default.</summary>
    public int? TrialDays { get; set; }

    // ── PLAN.md 10.7: the clinic's own identity. DisplayName replaces the default "TechCare Clinic" label in
        //    the app sidebar, the public page and printouts; when null the clinic Name is shown. LogoPath is the
        //    random stored name of the uploaded logo inside this clinic's folder under Files:AttachmentsPath.
        public string? DisplayName { get; set; }
        public string? LogoPath { get; set; }

        /// <summary>
        /// PLAN.md 10.12: doctor's qualifications for the prescription header (e.g., "ماجستير الباطنة، زمالة القلب").
        /// </summary>
        public string? Qualifications { get; set; }

        /// <summary>
        /// PLAN.md 10.12: working hours text for the prescription header (e.g., "السبت–الأربعاء ٥–١٠ م، الجمعة راحة").
        /// </summary>
        public string? WorkingHoursText { get; set; }
    }

/// <summary>
/// A staff account. <see cref="ClinicId"/> stays null for the SuperAdmin owner account (PLAN.md 1.1), which is
/// not attached to any clinic; every other account always belongs to exactly one clinic.
/// </summary>
public class User : IClinicOptional
{
    public int Id { get; set; }
    public int? ClinicId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; }

    /// <summary>
    /// PLAN.md 1.4: the account's explicit permission set, comma-separated keys from
    /// <see cref="Infrastructure.Permissions.All"/> that the clinic admin configured in the settings. Null means
    /// "not customised": the role's default set applies, which is how every pre-existing account keeps working
    /// unchanged. A stored empty string is an explicit "no permissions at all".
    /// </summary>
    public string? Permissions { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// PLAN.md 10.6: logical delete. The account disappears from every list and can never sign in again, while
    /// the rows it owns elsewhere (visits by this doctor, audit entries) keep their data untouched. On delete
    /// the username is renamed so the globally unique name is free for a new account to reuse.
    /// </summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Bumped every time the account's security state changes (deactivation, password reset, role change).
    /// The JWT carries the value it had at login, and the token is re-checked against this on every request,
    /// so any state change voids every previously issued token (PLAN_ADDENDUM.md 7.1).
    /// </summary>
    public int TokenVersion { get; set; }

    /// <summary>
    /// PLAN_ADDENDUM.md 7.5: an account the admin (or the owner) created starts with a temporary password and
    /// must change it at its first login. The login response carries the flag so the UI can force the change.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// PLAN_ADDENDUM.md 7.5: consecutive failed logins for this account (the IP rate limit still stands in front
    /// of it). Reaching the threshold locks the account and resets the counter; a successful login clears both.
    /// </summary>
    public int FailedLoginCount { get; set; }

    /// <summary>PLAN_ADDENDUM.md 7.5: while this moment is in the future, every login for the account is refused.</summary>
    public DateTime? LockedUntilUtc { get; set; }
}

public class Patient : IClinicOwned
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }
    public string? Gender { get; set; }
    public string? Phone { get; set; }
    public string? BloodType { get; set; }
    public string? ChronicConditions { get; set; }
    public string? Allergies { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.3: identity hidden for good — the name and contact fields are replaced by an
    /// anonymous marker while the medical history stays. One-way: an anonymised file can never be anonymised
    /// again, and no endpoint restores the original values.
    /// </summary>
    public bool IsAnonymized { get; set; }

    /// <summary>
    /// PLAN_ADDENDUM.md 9.3: logically deleted. The global query filter keeps such rows out of every
    /// clinic-facing query (the file is "gone" for the whole UI), while the retention service physically
    /// removes the row — and its visits, invoices and attachments — once the clinic's retention period has
    /// elapsed.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>PLAN_ADDENDUM.md 9.3: when the file was logically deleted (UTC), so the retention service can
    /// compare it against the clinic's <see cref="Clinic.DataRetentionDays"/>. Null while the file is live.</summary>
    public DateTime? DeletedAtUtc { get; set; }

    public List<Visit> Visits { get; set; } = new();
}

public class Visit : IClinicOwned
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public int PatientId { get; set; }
    public Patient? Patient { get; set; }
    public int DoctorId { get; set; }
    public User? Doctor { get; set; }
    public int? AppointmentId { get; set; }
    public DateTime VisitDate { get; set; }

    public string? ChiefComplaint { get; set; }
    public string? Examination { get; set; }
    public string? Diagnosis { get; set; }
    public string? Notes { get; set; }
    public string? LabRequests { get; set; }
    public DateOnly? FollowUpDate { get; set; }

    public string? BloodPressure { get; set; }
    public int? Pulse { get; set; }
    public decimal? Temperature { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? HeightCm { get; set; }
    public int? BloodSugar { get; set; }

    public List<PrescriptionItem> Prescription { get; set; } = new();
}

public class PrescriptionItem
{
    public int Id { get; set; }
    public int VisitId { get; set; }
    public string DrugName { get; set; } = string.Empty;
    public string? Dose { get; set; }
    public string? Frequency { get; set; }
    public string? Duration { get; set; }
    public string? Instructions { get; set; }
}

/// <summary>
/// PLAN_FULL.md 5.1 — a lab scan or an X-ray the clinic keeps with a saved visit. The image itself lives on
/// disk in the clinic's own folder (outside wwwroot) under a random name, never the caller's; this row is the
/// only door to the file, and its clinic filter keeps another clinic's attachments unreachable.
/// </summary>
public class VisitAttachment : IClinicOwned
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public int VisitId { get; set; }
    public Visit? Visit { get; set; }
    /// <summary>The file name on disk: a random name generated by the server (PLAN_ADDENDUM.md 7.8).</summary>
    public string StoredName { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Appointment : IClinicOwned
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public DateTime ScheduledAt { get; set; }
    public AppointmentType Type { get; set; } = AppointmentType.Consultation;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Booked;
    public AppointmentSource Source { get; set; } = AppointmentSource.Reception;
    public bool IsUrgent { get; set; }
    public int? QueueNumber { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Notes { get; set; }
    public string TrackingToken { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Invoice : IClinicOwned
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public int PatientId { get; set; }
    public Patient? Patient { get; set; }
    public int? VisitId { get; set; }
    /// <summary>The queue/booking this invoice pays for, so the front desk can see who has paid today.</summary>
    public int? AppointmentId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal Discount { get; set; }
    public decimal Paid { get; set; }
    public PaymentMethod Method { get; set; } = PaymentMethod.Cash;
    public DateTime CreatedAt { get; set; }
    public int CreatedByUserId { get; set; }

    public decimal Net => Amount - Discount;
    public decimal Remaining => Net - Paid;
}

/// <summary>
/// PLAN_ADDENDUM.md 7.4: one row of the audit trail — who did what on which entity, when and from which IP.
/// It never carries medical content: only the coordinates of the event.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public int? ClinicId { get; set; }
    public int? UserId { get; set; }

    /// <summary>A snapshot of the account's name at event time, so the trail stays meaningful if the account is renamed or deleted later.</summary>
    public string? UserName { get; set; }

    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string? EntityId { get; set; }

    /// <summary>The IP the event came from; null for background and seed-time writes that carry no request.</summary>
    public string? IpAddress { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public class LibraryDocument
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }
    public int ChunkCount { get; set; }
    public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;
    public List<LibraryChunk> Chunks { get; set; } = new();
}

public class LibraryChunk
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public LibraryDocument? Document { get; set; }
    public int Index { get; set; }
    public int? PageNumber { get; set; }
    /// <summary>Set for pre-chunked knowledge-base entries (.jsonl), where one file holds many topics.</summary>
    public string? Title { get; set; }
    public string? Section { get; set; }
    public string? SourceUrl { get; set; }
    /// <summary>guideline | drug_label | egypt_catalog | pubmed | uploaded — derived from the source file so a search can be routed to the right kind of evidence.</summary>
    public string? SourceType { get; set; }
    /// <summary>For pubmed chunks, the study's evidence line read at index time from the chunk header ("Meta-analysis", "RCT", …) — PLAN.md 2.2.</summary>
    public string? Evidence { get; set; }
    /// <summary>For knowledge-base chunks, the publication year from the file's "updated" field — PLAN.md 2.2.</summary>
    public int? Year { get; set; }
    public string Text { get; set; } = string.Empty;
    public byte[] Embedding { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// The AI configuration the owner manages from the panel (PLAN.md 1.2): a row whose clinic is null is the
/// installation-wide default, and a row that has a clinic overrides it for that clinic only. LmStudioClient
/// never reads appsettings directly for these values — it resolves the effective row through AiSettingsService.
/// </summary>
public class AiSettings : IClinicOptional
{
    public int Id { get; set; }
    public int? ClinicId { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    /// <summary>PLAN_ADDENDUM.md 9.2: off by default — the question/answer text joins the interaction log only when a clinic turns this on.</summary>
    public bool LogQuestionText { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// PLAN_ADDENDUM.md 9.2: one row per AI request — who asked about whom, which library sources the answer leaned
/// on, and how long it took. The question and answer text are stored only when the clinic enabled that in the AI
/// settings; otherwise the row stays content-free. It is shown on the audit screen but is not part of the 7.4
/// event trail (no <c>AuditLog</c> row is written for it).
/// </summary>
public class AiInteraction
{
    public long Id { get; set; }
    public int? ClinicId { get; set; }
    public int? UserId { get; set; }
    public int? PatientId { get; set; }
    /// <summary>ask | summary | differential | draft-report | draft-referral</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>The cited library titles, as a JSON string array capped at ten entries; empty when the answer had none.</summary>
    public string Sources { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string? QuestionText { get; set; }
    public string? AnswerText { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// PLAN.md 6.1 — a short note the front desk and the clinical team exchange about today's queue (who is ready,
/// who stepped out, ...). One thread per day: the endpoint only lists today's rows, and the optional appointment
/// link keeps the note pointed at the patient it is about. The sender's name is snapshotted so the thread stays
/// readable even if the account is renamed or deleted later.
/// </summary>
public class QueueMessage : IClinicOwned
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    /// <summary>The queue entry this note is about, when it is about one; null for a general note.</summary>
    public int? AppointmentId { get; set; }
    public int SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// <summary>The clinic's local wall-clock time, like every other queue timestamp.</summary>
    public DateTime SentAt { get; set; }
}

/// <summary>
/// PLAN.md 10.4 — a payment the owner books by hand against a clinic: amount, its own date and a short note.
/// It belongs to the owner's revenue story, not to the clinic's data, so it carries no medical content; the
/// clinic filter keeps a clinic's staff from ever seeing it.
/// </summary>
public class SubscriptionPayment
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The account that recorded the payment, snapshotted by name ("مالك النظام" for the owner).</summary>
    public string? CreatedByUserName { get; set; }
}



public class DrugCatalog
{
    public int Id { get; set; }
    public string TradeName { get; set; } = string.Empty;
    public string ActiveIngredient { get; set; } = string.Empty;
    public string? FormStrength { get; set; }
}
public enum CatalogItemType
{
    Lab = 1,
    Radiology = 2
}

public class CatalogItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CatalogItemType Type { get; set; }
    public int ClinicId { get; set; }
}


