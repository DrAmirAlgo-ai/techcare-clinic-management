using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TechCare.Clinic.Api.Data;

namespace TechCare.Clinic.Api.Features.Queue;

/// <summary>
/// Pushes a payload-free "queueChanged" signal to everyone watching a clinic's queue
/// (staff dashboard, waiting-room screen, patient tracking page). Clients then refetch
/// through their own authorized endpoint, so no patient data travels over the hub.
/// </summary>
public class QueueHub : Hub
{
    public static string GroupName(int clinicId) => $"clinic:{clinicId}";

    private readonly ClinicDbContext _db;

    public QueueHub(ClinicDbContext db) => _db = db;

    public async Task<bool> JoinClinic(string slug)
    {
        var clinicId = await _db.Clinics
            .Where(c => c.Slug == slug.ToLower())
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
        if (clinicId == null) return false;

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(clinicId.Value));
        return true;
    }
}

public class QueueNotifier
{
    private readonly IHubContext<QueueHub> _hub;
    private readonly ClinicDbContext _db;

    public QueueNotifier(IHubContext<QueueHub> hub, ClinicDbContext db)
    {
        _hub = hub;
        _db = db;
    }

    public Task QueueChangedAsync(int? clinicId = null)
    {
        var id = clinicId ?? _db.CurrentClinicId;
        return id == null ? Task.CompletedTask : _hub.Clients.Group(QueueHub.GroupName(id.Value)).SendAsync("queueChanged");
    }

    /// <summary>
    /// PLAN.md 6.1: a note landed on today's front-desk ↔ clinical team thread. Payload-free like
    /// <see cref="QueueChangedAsync"/> — the receivers refetch the thread through their own authorized endpoint,
    /// so no message text travels over the hub.
    /// </summary>
    public Task MessagesChangedAsync(int? clinicId = null)
    {
        var id = clinicId ?? _db.CurrentClinicId;
        return id == null ? Task.CompletedTask : _hub.Clients.Group(QueueHub.GroupName(id.Value)).SendAsync("messageSent");
    }
}
