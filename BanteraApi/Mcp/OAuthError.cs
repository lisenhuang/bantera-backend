using System.Text.Json.Serialization;

namespace BanteraApi.Mcp;

/// <summary>RFC 6749 error response body.</summary>
public sealed class OAuthError(string error, string? errorDescription = null)
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = error;

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; } = errorDescription;
}

public static class OAuthErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidClient = "invalid_client";
    public const string InvalidGrant = "invalid_grant";
    public const string UnauthorizedClient = "unauthorized_client";
    public const string UnsupportedGrantType = "unsupported_grant_type";
    public const string UnsupportedResponseType = "unsupported_response_type";
    public const string InvalidScope = "invalid_scope";
    public const string InvalidTarget = "invalid_target";
    public const string AccessDenied = "access_denied";
    public const string ServerError = "server_error";
    public const string InvalidRedirectUri = "invalid_redirect_uri";
    public const string InvalidClientMetadata = "invalid_client_metadata";

    /// <summary>
    /// Builds a redirect back to the client carrying an OAuth error, preserving state.
    /// Only ever called with a redirect URI that has already been matched against the
    /// client's registered list.
    /// </summary>
    public static string BuildErrorRedirect(string redirectUri, string error, string? description, string? state)
    {
        var separator = redirectUri.Contains('?') ? '&' : '?';
        var query = $"error={Uri.EscapeDataString(error)}";

        if (!string.IsNullOrEmpty(description))
            query += $"&error_description={Uri.EscapeDataString(description)}";

        if (!string.IsNullOrEmpty(state))
            query += $"&state={Uri.EscapeDataString(state)}";

        return $"{redirectUri}{separator}{query}";
    }

    /// <summary>Builds the success redirect carrying the authorization code.</summary>
    public static string BuildCodeRedirect(string redirectUri, string code, string? state, string issuer)
    {
        var separator = redirectUri.Contains('?') ? '&' : '?';
        var query = $"code={Uri.EscapeDataString(code)}&iss={Uri.EscapeDataString(issuer)}";

        if (!string.IsNullOrEmpty(state))
            query += $"&state={Uri.EscapeDataString(state)}";

        return $"{redirectUri}{separator}{query}";
    }
}
