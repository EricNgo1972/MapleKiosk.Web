namespace SPC.Infrastructure.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Authentication";

    public string AuthHostUrl { get; set; } = "";
    public string TokenSigningKey { get; set; } = "";
}
