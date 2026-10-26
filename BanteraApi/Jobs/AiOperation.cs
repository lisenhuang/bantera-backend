using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BanteraApi.Auth;
using Microsoft.Extensions.Options;

namespace BanteraApi.Jobs;

// Temporary encrypted work, not permanent conversation history. Account deletion cascades.
public sealed class AiOperation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Events { get; set; }
    public int StatusCode { get; set; } = 200;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(1);
    public DateTime? LeaseUntil { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Terminal => Status is "done" or "failed" or "cancelled";
}

public sealed record OperationInput(string Body, Dictionary<string, string?> RouteValues);
public sealed record OperationEvent(int Sequence, JsonElement Data);

public sealed class OperationCipher(IOptions<JwtSettings> options)
{
    // Domain-separated key; no new production secret. Rotating JWT secrets expires old work.
    private byte[] Key => HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.Secret), "bantera/ai-operation/v1"u8.ToArray());
    public string Seal(string text, Guid id)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var result = new byte[28 + bytes.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(result.AsSpan(0, 12), bytes, result.AsSpan(28), result.AsSpan(12, 16), id.ToByteArray());
        return Convert.ToBase64String(result);
    }
    public string Open(string text, Guid id)
    {
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length < 28) throw new CryptographicException();
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, id.ToByteArray());
        return Encoding.UTF8.GetString(plain);
    }
    public string Hash(string text) => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(text)));
    public Guid RequestId(Guid owner, string kind, string key) => new(Convert.FromHexString(Hash($"{owner}/{kind}/{key}"))[..16]);
}
