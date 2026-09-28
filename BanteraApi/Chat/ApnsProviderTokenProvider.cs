using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BanteraApi.Chat;

/// Shares the provider JWT across transient HTTP clients, alert pushes and VoIP pushes.
public sealed class ApnsProviderTokenProvider(IOptions<ApnsSettings> settingsOptions, TimeProvider timeProvider)
{
    private readonly ApnsSettings _settings = settingsOptions.Value;
    private readonly object _sync = new();
    private string? _token;
    private DateTimeOffset _issuedAt;

    public string GetToken()
    {
        lock (_sync)
        {
            var now = timeProvider.GetUtcNow();
            // Apple permits renewal every 20-60 minutes. Refresh on demand at 50,
            // leaving a safety margin before expiry without signing on every push.
            if (_token is not null && now >= _issuedAt && now - _issuedAt < TimeSpan.FromMinutes(50))
                return _token;

            _token = CreateProviderToken(now);
            _issuedAt = now;
            return _token;
        }
    }

    private string CreateProviderToken(DateTimeOffset now)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(NormalizePem(_settings.PrivateKeyPem!).ToCharArray());

        var signingKey = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = _settings.KeyId,
        };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256)
        {
            // Only the signed JWT is cached; each renewal uses a disposable ECDsa instance.
            // Without this, a cached provider can hold a reference to a previously disposed ECDsa.
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        var header = new JwtHeader(credentials)
        {
            ["kid"] = _settings.KeyId!,
        };
        var payload = new JwtPayload
        {
            { "iss", _settings.TeamId! },
            { "iat", now.ToUnixTimeSeconds() },
        };

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static string NormalizePem(string pem)
    {
        return pem.Replace("\\n", "\n").Trim();
    }
}
