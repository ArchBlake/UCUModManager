using System.Net;
using System.Text;

namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthLoopbackCallbackListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Uri _redirectUri;

    public NexusOAuthLoopbackCallbackListener(Uri redirectUri)
    {
        if (!redirectUri.IsAbsoluteUri || !redirectUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("OAuth loopback redirect uri must be an absolute http uri.", nameof(redirectUri));
        }

        if (!IPAddress.TryParse(redirectUri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("OAuth loopback redirect uri must use a loopback host such as 127.0.0.1.", nameof(redirectUri));
        }

        _redirectUri = redirectUri;
        _listener.Prefixes.Add($"{redirectUri.Scheme}://{redirectUri.Host}:{redirectUri.Port}/");
    }

    public async Task<NexusOAuthCallbackResult> WaitForCallbackAsync(
        string expectedState,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedState))
        {
            throw new ArgumentException("OAuth state is required.", nameof(expectedState));
        }

        _listener.Start();
        using var registration = cancellationToken.Register(static state => ((HttpListener)state!).Close(), _listener);
        try
        {
            var context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = BuildResult(context.Request, expectedState);
            await WriteBrowserResponseAsync(context.Response, result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
    }

    public void Dispose()
    {
        _listener.Close();
    }

    private NexusOAuthCallbackResult BuildResult(HttpListenerRequest request, string expectedState)
    {
        if (request.Url is null
            || !request.Url.AbsolutePath.Equals(_redirectUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            return new NexusOAuthCallbackResult(null, "invalid_callback_path", "The OAuth callback path did not match the configured redirect uri.");
        }

        var error = request.QueryString["error"];
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new NexusOAuthCallbackResult(null, error, request.QueryString["error_description"]);
        }

        var state = request.QueryString["state"];
        if (!expectedState.Equals(state, StringComparison.Ordinal))
        {
            return new NexusOAuthCallbackResult(null, "invalid_state", "The OAuth state returned by Nexus did not match the local request.");
        }

        var code = request.QueryString["code"];
        return string.IsNullOrWhiteSpace(code)
            ? new NexusOAuthCallbackResult(null, "missing_code", "Nexus did not return an authorization code.")
            : new NexusOAuthCallbackResult(code, null, null);
    }

    private static async Task WriteBrowserResponseAsync(
        HttpListenerResponse response,
        NexusOAuthCallbackResult result,
        CancellationToken cancellationToken)
    {
        var isSuccess = result.IsSuccess;
        response.StatusCode = isSuccess ? 200 : 400;
        response.ContentType = "text/html; charset=utf-8";
        var html = isSuccess
            ? "<!doctype html><html><body><h1>UCU ModManager</h1><p>Nexus sign-in completed. You can close this browser tab.</p></body></html>"
            : "<!doctype html><html><body><h1>UCU ModManager</h1><p>Nexus sign-in failed. Return to the mod manager and try again.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }
}

public sealed record NexusOAuthCallbackResult(
    string? AuthorizationCode,
    string? Error,
    string? ErrorDescription)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(AuthorizationCode) && string.IsNullOrWhiteSpace(Error);
}
