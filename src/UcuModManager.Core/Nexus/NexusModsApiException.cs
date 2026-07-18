namespace UcuModManager.Core.Nexus;

public sealed class NexusModsApiException : InvalidOperationException
{
    public NexusModsApiException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    public bool ShouldPauseRequests => StatusCode is 401 or 403 or 429 || StatusCode >= 500;
}
