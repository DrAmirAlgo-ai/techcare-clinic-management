using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// Appointment and visit times are stored as the clinic's local wall-clock time,
/// so "today" and working hours match what the clinic sees regardless of server location.
/// </summary>
public static class ClinicClock
{
    public static DateTime Now(Domain.Clinic clinic)
    {
        var zone = ResolveZone(clinic.TimeZoneId);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone), DateTimeKind.Unspecified);
    }

    public static DateOnly Today(Domain.Clinic clinic) => DateOnly.FromDateTime(Now(clinic));

    private static TimeZoneInfo ResolveZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}

