using Bridge.Hosting;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Bridge.Api;

/// <summary>Microsoft Entra ID bearer tokens with app roles: Sync.Read to look, Sync.Operate to replay
/// dead letters and trigger reconciliation. Locally, the same roles in tokens the API signs itself.</summary>
public static class Auth
{
    public const string Read = "Sync.Read";
    public const string Operate = "Sync.Operate";
    public const string LocalIssuer = "merchant-crm-bridge-local";

    public static void AddBridgeAuth(this IServiceCollection services, AuthSettings settings, IHostEnvironment env, bool demo)
    {
        var local = settings.Mode.Equals("Local", StringComparison.OrdinalIgnoreCase);
        if (local && !env.IsDevelopment() && !demo)
            throw new InvalidOperationException("Auth:Mode=Local is only allowed in Development or demo mode.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            if (local)
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = LocalIssuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.LocalSigningKey)),
                    RoleClaimType = "roles",
                    NameClaimType = "name",
                };
            }
            else
            {
                o.Authority = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidAudiences = [settings.Audience],
                    RoleClaimType = "roles",
                    NameClaimType = "name",
                };
            }
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(Read, p => p.RequireRole(Read, Operate))
            .AddPolicy(Operate, p => p.RequireRole(Operate));
    }

    public static string IssueLocalToken(AuthSettings settings, string name, IEnumerable<string> roles, TimeProvider clock)
    {
        var claims = new List<Claim> { new("name", name), new("sub", name), new("oid", Guid.NewGuid().ToString()) };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        var token = new JwtSecurityToken(
            issuer: LocalIssuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: clock.GetUtcNow().UtcDateTime,
            expires: clock.GetUtcNow().UtcDateTime.AddHours(8),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.LocalSigningKey)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
