using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TechCare.Clinic.Api.Domain;

namespace TechCare.Clinic.Api.Infrastructure;

public class JwtOptions
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "TechCareClinic";
    public string Audience { get; set; } = "TechCareClinic";
    // 7.1 (PLAN_ADDENDUM.md): a shorter default session; disabling an account or changing its
    // password/role now voids the token immediately anyway.
    public int ExpiresMinutes { get; set; } = 240;
}

public class JwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(JwtOptions options) => _options = options;

    public (string Token, DateTime ExpiresAtUtc) CreateToken(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(ClaimNames.TokenVersion, user.TokenVersion.ToString())
        };

        // The SuperAdmin owner belongs to no clinic (PLAN.md 1.1). The claim is left out rather than sent as 0, so
        // TenantContext.ClinicId stays null, every clinic-scoped query filter fails closed and each clinic endpoint
        // refuses the token on roles before it could ever touch data.
        if (user.ClinicId is int clinicId)
            claims.Add(new Claim(ClaimNames.ClinicId, clinicId.ToString()));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_options.ExpiresMinutes);

        var token = new JwtSecurityToken(_options.Issuer, _options.Audience, claims,
            expires: expires, signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = _options.Issuer,
        ValidAudience = _options.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
}
