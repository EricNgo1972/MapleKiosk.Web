using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SPC.Infrastructure.Auth;

public static class TokenSigner
{
    public const string Issuer = "maplekiosk-auth";
    public const string Audience = "maplekiosk-tenant";

    public static async Task<(bool ok, string email, string? name, string? picture)> TryVerifyAsync(string raw, string key)
    {
        if (string.IsNullOrEmpty(raw)) return (false, "", null, null);

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(raw, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ClockSkew = TimeSpan.FromSeconds(30),
        });

        if (!result.IsValid) return (false, "", null, null);

        var email = result.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? "";
        var name = result.ClaimsIdentity.FindFirst(JwtRegisteredClaimNames.Name)?.Value;
        var picture = result.ClaimsIdentity.FindFirst("picture")?.Value;
        return (!string.IsNullOrEmpty(email), email, name, picture);
    }
}
