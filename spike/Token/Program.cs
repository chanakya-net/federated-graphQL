using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// usage: dotnet run -- <tenantId> [comma-separated services]
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: Token <tenantId> [service1,service2,...]");
    return 1;
}

var tenant = args[0];
string[] services = args.Length > 1 ? args[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
var now = DateTime.UtcNow;

var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
var token = handler.CreateToken(new SecurityTokenDescriptor
{
    Issuer = SpikeAuth.Issuer,
    Audience = SpikeAuth.Audience,
    IssuedAt = now,
    NotBefore = now,
    Expires = now.AddYears(5),
    SigningCredentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SpikeAuth.Key)), SecurityAlgorithms.HmacSha256),
    Claims = new Dictionary<string, object>
    {
        ["sub"] = "spike-user",
        [SpikeAuth.TenantClaim] = tenant,
        [SpikeAuth.ServicesClaim] = services, // JSON array -> multiple claims on the server side
    },
});

Console.WriteLine(token);
return 0;
