namespace TechCare.Clinic.Api.Infrastructure;

/// <summary>
/// PLAN_ADDENDUM.md 7.5: the password rule every password-changing endpoint applies — a minimum of ten
/// characters and a short blocklist of the most common passwords. The list compares case-insensitively and
/// after trimming, so "Password123" is caught the same way as "password123".
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "p@ssword", "p@ssw0rd",
        "admin", "admin123", "admin1234", "admin@123", "admin@1234", "admin2026",
        "12345678", "123456789", "1234567890", "12345678901", "123456789012",
        "00000000", "0000000000", "11111111", "1111111111", "2222222222",
        "qwerty", "qwerty123", "qwerty1234", "qwerty12345", "qwertyuiop",
        "1q2w3e4r", "1q2w3e4r5t", "1qaz2wsx", "1qazxsw2",
        "abc12345", "abc123456", "abcdef123",
        "iloveyou", "iloveyou1", "iloveyou123",
        "letmein", "letmein1", "letmein123",
        "welcome", "welcome1", "welcome123",
        "changeme", "changeme1", "changeme123", "temp1234", "temp12345", "temp123456",
        "clinic123", "clinic1234", "doctor123", "doctor1234"
    };

    /// <summary>The single rule shared by user creation, admin reset and the account's own change.</summary>
    public static (bool Valid, string? Message) Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
            return (false, $"كلمة المرور يجب أن تكون {MinLength} أحرف على الأقل");
        if (Common.Contains(password.Trim()))
            return (false, "كلمة المرور من الأكثر استخداما، اختر كلمة أخرى");
        return (true, null);
    }
}
