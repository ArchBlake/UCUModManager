namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthException : InvalidOperationException
{
    public NexusOAuthException(string message, int? statusCode = null, string? errorCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int? StatusCode { get; }

    public string? ErrorCode { get; }

    public bool RequiresReauthentication
    {
        get
        {
            if (ErrorCode is not null
                && (ErrorCode.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase)
                    || ErrorCode.Equals("invalid_token", StringComparison.OrdinalIgnoreCase)
                    || ErrorCode.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
                    || ErrorCode.Equals("unauthorized_client", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return StatusCode is 400 or 401 or 403;
        }
    }
}

public sealed class NexusOAuthAuthenticationRequiredException : InvalidOperationException
{
    public NexusOAuthAuthenticationRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class NexusOAuthTokenValidationException : InvalidOperationException
{
    public NexusOAuthTokenValidationException(string message, bool isExpired, Exception? innerException = null)
        : base(message, innerException)
    {
        IsExpired = isExpired;
    }

    public bool IsExpired { get; }
}
