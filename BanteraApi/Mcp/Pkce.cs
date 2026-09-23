using System.Security.Cryptography;
using System.Text;

namespace BanteraApi.Mcp;

/// <summary>
/// PKCE (RFC 7636) helpers. Only the S256 challenge method is supported — "plain"
/// is rejected, as required by OAuth 2.1 and the MCP authorization spec.
/// </summary>
public static class Pkce
{
    public const string S256 = "S256";

    /// <summary>Base64url encoding without padding (RFC 4648 §5).</summary>
    public static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string RandomToken(int bytes)
        => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>Computes the S256 challenge for a verifier.</summary>
    public static string ComputeS256(string codeVerifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>RFC 7636 §4.1: 43-128 characters from the unreserved set.</summary>
    public static bool IsValidVerifier(string? codeVerifier)
    {
        if (codeVerifier is null || codeVerifier.Length is < 43 or > 128)
            return false;

        foreach (var c in codeVerifier)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>An S256 challenge is always 43 base64url characters.</summary>
    public static bool IsValidChallenge(string? codeChallenge)
    {
        if (codeChallenge is null || codeChallenge.Length != 43)
            return false;

        foreach (var c in codeChallenge)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies a code verifier against a stored challenge in constant time.
    /// Returns false for malformed input rather than throwing.
    /// </summary>
    public static bool VerifyS256(string? codeVerifier, string? codeChallenge)
    {
        if (!IsValidVerifier(codeVerifier) || !IsValidChallenge(codeChallenge))
            return false;

        var computed = Encoding.ASCII.GetBytes(ComputeS256(codeVerifier!));
        var expected = Encoding.ASCII.GetBytes(codeChallenge!);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}
