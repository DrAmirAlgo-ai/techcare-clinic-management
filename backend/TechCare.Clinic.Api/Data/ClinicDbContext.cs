using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Domain;
using TechCare.Clinic.Api.Infrastructure;

namespace TechCare.Clinic.Api.Data;

public class ClinicDbContext : DbContext
{
    private readonly TenantContext _tenant;

    public ClinicDbContext(DbContextOptions<ClinicDbContext> options, TenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    public int? CurrentClinicId => _tenant.ClinicId;

    public DbSet<Domain.Clinic> Clinics => Set<Domain.Clinic>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<PrescriptionItem> PrescriptionItems => Set<PrescriptionItem>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<LibraryDocument> LibraryDocuments => Set<LibraryDocument>();
    public DbSet<LibraryChunk> LibraryChunks => Set<LibraryChunk>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AiInteraction> AiInteractions => Set<AiInteraction>();
    public DbSet<VisitAttachment> VisitAttachments => Set<VisitAttachment>();
    public DbSet<QueueMessage> QueueMessages => Set<QueueMessage>();
    public DbSet<SubscriptionPayment> SubscriptionPayments => Set<SubscriptionPayment>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
     public DbSet<DrugCatalog> DrugCatalogs => Set<DrugCatalog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Domain.Clinic>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.ConsultationFee).HasPrecision(10, 2);
            e.Property(x => x.FollowUpFee).HasPrecision(10, 2);
        });

        b.Entity<User>(e =>
        {
            // Usernames are unique across the whole installation (one "admin" per system, the owner included): a
            // bare login can never be ambiguous, and no new clinic or new user may shadow an existing account.
            // The controllers check the same rule before writing; the index is the last line of defence.
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            // PLAN.md 10.6: a logically deleted account is invisible to every clinic-facing query — the staff
            // list, the permission user pickers, patient-file doctor dropdowns — while its row (and the visits
            // and audit entries pointing at it) stays on disk, like a deleted patient file (9.3).
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId && !x.IsDeleted);
        });

        b.Entity<Patient>(e =>
        {
            e.HasIndex(x => new { x.ClinicId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.ClinicId, x.Phone });
            // PLAN_ADDENDUM.md 9.3: logically deleted files are invisible to every clinic-facing query — the
            // search, the file itself, the queue, new bookings, even the dashboard counts — while the row (and
            // its medical history) stays on disk until the clinic's retention period has elapsed.
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId && !x.IsDeleted);
        });

        b.Entity<Visit>(e =>
        {
            e.HasIndex(x => new { x.ClinicId, x.PatientId, x.VisitDate });
            e.HasOne(x => x.Patient).WithMany(p => p.Visits).HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Doctor).WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Prescription).WithOne().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Temperature).HasPrecision(4, 1);
            e.Property(x => x.WeightKg).HasPrecision(5, 1);
            e.Property(x => x.HeightCm).HasPrecision(5, 1);
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });

        b.Entity<VisitAttachment>(e =>
        {
            // PLAN_FULL.md 5.1: an attachment dies with its visit, exactly like a prescription item does.
            e.HasOne(x => x.Visit).WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.StoredName).HasMaxLength(200);
            e.Property(x => x.OriginalName).HasMaxLength(500);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });
         b.Entity<CatalogItem>(e =>
          {
              e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
          });
        b.Entity<Appointment>(e =>
        {
            e.HasIndex(x => new { x.ClinicId, x.ScheduledAt });
            e.HasIndex(x => x.TrackingToken).IsUnique();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.SetNull);
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });

        b.Entity<Invoice>(e =>
        {
            e.HasIndex(x => new { x.ClinicId, x.CreatedAt });
            e.HasIndex(x => x.AppointmentId);
            e.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Amount).HasPrecision(10, 2);
            e.Property(x => x.Discount).HasPrecision(10, 2);
            e.Property(x => x.Paid).HasPrecision(10, 2);
            e.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });

        b.Entity<AuditLog>(e => e.HasIndex(x => new { x.ClinicId, x.AtUtc }));

        b.Entity<LibraryDocument>(e =>
        {
            e.HasIndex(x => x.FileName).IsUnique();
            e.HasMany(x => x.Chunks).WithOne(x => x.Document).HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<LibraryChunk>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Section).HasMaxLength(300);
            e.Property(x => x.SourceUrl).HasMaxLength(500);
            e.Property(x => x.SourceType).HasMaxLength(40);
            e.Property(x => x.Evidence).HasMaxLength(60);
            e.HasIndex(x => x.SourceType);
        });

        b.Entity<AiSettings>(e =>
        {
            // One row per clinic, and the global row (ClinicId null) — SQLite keeps NULLs distinct in a unique
            // index, so the "one global row" rule is enforced by the owner endpoint's upsert, not by the index.
            e.HasIndex(x => x.ClinicId).IsUnique();
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });

        b.Entity<AiInteraction>(e =>
        {
            // Like AuditLog, no global query filter: the owner's trail crosses every clinic on purpose, and the
            // clinic-admin endpoint applies "my clinic only" by hand (the 0.2 isolation tests cover that door).
            e.HasIndex(x => new { x.ClinicId, x.AtUtc });
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Sources).HasMaxLength(2000);
        });

        b.Entity<QueueMessage>(e =>
        {
            // PLAN.md 6.1: a note may outlive its appointment (the link then simply opens), so deleting the
            // appointment row drops the reference instead of the note.
            e.HasIndex(x => new { x.ClinicId, x.SentAt });
            e.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.SenderName).HasMaxLength(100);
            e.Property(x => x.Text).HasMaxLength(300);
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });

        b.Entity<SubscriptionPayment>(e =>
        {
            // PLAN.md 10.4: the owner books and reads payments across clinics (IgnoreQueryFilters there), while a
            // clinic's staff never reaches these rows at all — the filter is the wall.
            e.HasIndex(x => x.ClinicId);
            e.Property(x => x.Amount).HasPrecision(10, 2);
            e.Property(x => x.Note).HasMaxLength(300);
            e.Property(x => x.CreatedByUserName).HasMaxLength(100);
            e.HasQueryFilter(x => x.ClinicId == CurrentClinicId || x.ClinicId == 0);
        });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampClinicOwnership();
        var audits = CollectAudits();
        var result = await base.SaveChangesAsync(cancellationToken);

        if (audits.Count > 0)
        {
            foreach (var (entry, audit) in audits)
            {
                audit.EntityId ??= entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString();
                if (entry.Entity is Domain.Clinic clinic) audit.ClinicId = clinic.Id;
            }
            AuditLogs.AddRange(audits.Select(a => a.Audit));
            await base.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// New clinic-owned rows always belong to the caller's clinic, and existing rows can't be moved to another clinic.
    /// </summary>
    private void StampClinicOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<IClinicOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.ClinicId == 0)
            {
                entry.Entity.ClinicId = _tenant.RequireClinicId();
            }
            else if (entry.State == EntityState.Modified && entry.Property(x => x.ClinicId).IsModified)
            {
                throw new InvalidOperationException("Changing the clinic of an existing record is not allowed.");
            }
        }

        // Accounts are the one entity whose clinic is optional (IClinicOptional): the seeder creates the SuperAdmin
        // owner without a clinic (PLAN.md 1.1), and UsersController stamps a clinic on every account it creates.
        // An existing account may never be moved to another clinic — that would hand it another clinic's data.
        foreach (var entry in ChangeTracker.Entries<User>())
        {
            if (entry.State == EntityState.Modified && entry.Property(x => x.ClinicId).IsModified)
                throw new InvalidOperationException("Changing the clinic of an existing account is not allowed.");
        }

        // AI settings rows follow the same rule (PLAN.md 1.2): a global row and one row per clinic, and an
        // existing row may never be moved to another clinic — that would silently hand one clinic's AI to another.
        foreach (var entry in ChangeTracker.Entries<AiSettings>())
        {
            if (entry.State == EntityState.Modified && entry.Property(x => x.ClinicId).IsModified)
                throw new InvalidOperationException("Changing the clinic of an existing AI settings row is not allowed.");
        }
    }

    private List<(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Entry, AuditLog Audit)> CollectAudits()
    {
        // PLAN_ADDENDUM.md 7.4: every clinic-facing write is captured here, in this single place. AiSettings joins
        // the list so the owner's AI configuration changes land in the same trail as the rest of what it can do.
        var audited = new[] { typeof(Patient), typeof(Visit), typeof(VisitAttachment), typeof(Appointment), typeof(Invoice), typeof(User), typeof(Domain.Clinic), typeof(AiSettings), typeof(SubscriptionPayment) };
        return ChangeTracker.Entries()
            .Where(e => audited.Contains(e.Entity.GetType())
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (e, new AuditLog
            {
                ClinicId = (e.Entity as IClinicOwned)?.ClinicId
                    ?? (e.Entity as IClinicOptional)?.ClinicId
                    ?? (e.Entity as Domain.Clinic)?.Id
                    ?? (e.Entity as SubscriptionPayment)?.ClinicId
                    ?? _tenant.ClinicId,
                UserId = _tenant.UserId,
                UserName = _tenant.UserName,
                IpAddress = _tenant.IpAddress,
                Action = e.State.ToString(),
                Entity = e.Entity.GetType().Name,
                EntityId = e.State == EntityState.Added ? null
                    : e.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString()
            }))
            .ToList();
    }

    /// <summary>
    /// PLAN_ADDENDUM.md 7.4: the single door through which an explicit event — opening a patient file, logging in,
    /// or a library/owner operation that changes no tracked entity — joins the trail. The caller passes only
    /// who/what/where, never content: the trail must not hold medical data.
    /// </summary>
    public void LogAction(string action, string entity, object? entityId = null, int? userId = null, string? userName = null, int? clinicId = null)
    {
        AuditLogs.Add(new AuditLog
        {
            ClinicId = clinicId ?? _tenant.ClinicId,
            UserId = userId ?? _tenant.UserId,
            UserName = userName ?? _tenant.UserName,
            Action = action,
            Entity = entity,
            EntityId = entityId?.ToString(),
            IpAddress = _tenant.IpAddress
        });
    }

    public void LogRead(string entity, object entityId) => LogAction("Viewed", entity, entityId);
}
