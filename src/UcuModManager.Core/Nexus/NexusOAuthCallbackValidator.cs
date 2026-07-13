using System.Collections.Specialized;

namespace UcuModManager.Core.Nexus;

public static class NexusOAuthCallbackValidator
{
    public static NexusOAuthCallbackResult Validate(
        Uri? requestUri,
        NameValueCollection query,
        Uri expectedRedirectUri,
        string expectedState)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(expectedRedirectUri);
        if (string.IsNullOrWhiteSpace(expectedState))
        {
            throw new ArgumentException("OAuth state is required.", nameof(expectedState));
        }

        if (!MatchesRedirectTarget(requestUri, expectedRedirectUri))
        {
            return Failure("invalid_callback_target", "The OAuth callback did not match the configured redirect uri.");
        }

        if (!TryGetSingleValue(query, "state", out var returnedState))
        {
            return Failure("duplicate_parameter", "The OAuth callback contained an invalid state parameter.");
        }

        if (!expectedState.Equals(returnedState, StringComparison.Ordinal))
        {
            return Failure("invalid_state", "The OAuth state returned by Nexus did not match the local request.");
        }

        if (!TryGetSingleValue(query, "error", out var error))
        {
            return Failure("duplicate_parameter", "The OAuth callback contained duplicate error parameters.");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            if (!TryGetSingleValue(query, "error_description", out var errorDescription))
            {
                return Failure("duplicate_parameter", "The OAuth callback contained duplicate error descriptions.");
            }

            var description = Truncate(errorDescription, 300);
            return Failure(Truncate(error, 100)!, description);
        }

        if (!TryGetSingleValue(query, "code", out var code))
        {
            return Failure("duplicate_parameter", "The OAuth callback contained duplicate authorization codes.");
        }

        return string.IsNullOrWhiteSpace(code)
            ? Failure("missing_code", "Nexus did not return an authorization code.")
            : new NexusOAuthCallbackResult(code, null, null);
    }

    private static bool MatchesRedirectTarget(Uri? requestUri, Uri expectedRedirectUri)
    {
        return requestUri is not null
            && requestUri.Scheme.Equals(expectedRedirectUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && requestUri.Host.Equals(expectedRedirectUri.Host, StringComparison.OrdinalIgnoreCase)
            && requestUri.Port == expectedRedirectUri.Port
            && requestUri.AbsolutePath.Equals(expectedRedirectUri.AbsolutePath, StringComparison.Ordinal);
    }

    private static bool TryGetSingleValue(NameValueCollection query, string key, out string? value)
    {
        var values = query.GetValues(key);
        if (values is null || values.Length == 0)
        {
            value = null;
            return true;
        }

        if (values.Length != 1)
        {
            value = null;
            return false;
        }

        value = values[0];
        return true;
    }

    private static NexusOAuthCallbackResult Failure(string error, string? description)
    {
        return new NexusOAuthCallbackResult(null, error, description);
    }

    private static string? Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "...";
    }
}
