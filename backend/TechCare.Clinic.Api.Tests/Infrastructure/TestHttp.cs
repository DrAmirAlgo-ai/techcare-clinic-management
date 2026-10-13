using System.Net.Http.Json;
using System.Text.Json;

namespace TechCare.Clinic.Api.Tests.Infrastructure;

/// <summary>Small HTTP helpers shared by the isolation tests, kept out of the test classes so both read the same way.</summary>
public static class TestHttp
{
    /// <summary>Parses a response body as JSON, whatever it contains.</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>
    /// The first bookable (date, time) in the coming week, read from whichever slots endpoint the caller is allowed
    /// to use, so a test never has to guess a clinic's working hours or its day off.
    /// </summary>
    public static async Task<(DateOnly Date, string Time)> FirstFreeSlotAsync(HttpClient client, Func<DateOnly, string> slotsPath)
    {
        var start = DateOnly.FromDateTime(DateTime.Today);
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = start.AddDays(offset);
            var response = await client.GetAsync(slotsPath(date));
            response.EnsureSuccessStatusCode();
            var slots = (await JsonAsync(response)).GetProperty("slots").EnumerateArray().Select(s => s.GetString()!).ToList();
            if (slots.Count > 0) return (date, slots[0]);
        }

        throw new InvalidOperationException("The clinic offers no bookable slot in the next week; check its working hours and days.");
    }
}
