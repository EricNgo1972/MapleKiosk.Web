using Microsoft.AspNetCore.Authorization;

namespace SPC.Infrastructure.Auth;

/// <summary>
/// Global handler that succeeds every pending authorization requirement when
/// the principal carries the <see cref="OwnerClaimType"/> claim. Lets the
/// tenant Owner act as super-user without being listed in every
/// <c>[Authorize(Roles = "...")]</c> attribute.
/// </summary>
public sealed class OwnerAuthorizationHandler : IAuthorizationHandler
{
    /// <summary>Claim type that triggers the global policy bypass.</summary>
    public const string OwnerClaimType = "owner";

    /// <summary>Canonical role name for the tenant Owner. Emitted alongside
    /// the owner claim so manual <c>User.IsInRole(...)</c> checks and
    /// <c>[Authorize(Roles = "...")]</c> attributes that opt into "Owner"
    /// also recognise the super-user.</summary>
    public const string OwnerRole = "Owner";

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (context.User.HasClaim(c => c.Type == OwnerClaimType && c.Value == "true"))
        {
            foreach (var requirement in context.PendingRequirements.ToList())
                context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
