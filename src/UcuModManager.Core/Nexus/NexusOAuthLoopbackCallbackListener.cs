using System.Net;
using System.Text;

namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthLoopbackCallbackListener : INexusOAuthCallbackListener
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpListener _listener = new();
    private readonly Uri _redirectUri;

    public NexusOAuthLoopbackCallbackListener(Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!redirectUri.IsAbsoluteUri || !redirectUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("OAuth loopback redirect uri must be an absolute http uri.", nameof(redirectUri));
        }

        if (!IPAddress.TryParse(redirectUri.Host, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("OAuth loopback redirect uri must use a loopback host such as 127.0.0.1.", nameof(redirectUri));
        }

        if (!string.IsNullOrEmpty(redirectUri.Query) || !string.IsNullOrEmpty(redirectUri.Fragment))
        {
            throw new ArgumentException("OAuth loopback redirect uri cannot contain a query or fragment.", nameof(redirectUri));
        }

        _redirectUri = redirectUri;
        _listener.IgnoreWriteExceptions = true;
        _listener.Prefixes.Add($"{redirectUri.Scheme}://{redirectUri.Host}:{redirectUri.Port}/");
    }

    public Task<NexusOAuthCallbackResult> WaitForCallbackAsync(
        string expectedState,
        CancellationToken cancellationToken = default)
    {
        return WaitForCallbackAsync(expectedState, DefaultTimeout, cancellationToken);
    }

    public void Start()
    {
        if (_listener.IsListening)
        {
            return;
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException exception)
        {
            throw new InvalidOperationException(
                $"UCU Mod Manager could not open the Nexus callback at {_redirectUri}. The local port may already be in use.",
                exception);
        }
    }

    public async Task<NexusOAuthCallbackResult> WaitForCallbackAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedState))
        {
            throw new ArgumentException("OAuth state is required.", nameof(expectedState));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "OAuth callback timeout must be positive.");
        }

        Start();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var registration = linkedSource.Token.Register(static state => ((HttpListener)state!).Close(), _listener);
        try
        {
            while (true)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(linkedSource.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (linkedSource.IsCancellationRequested
                                                  && exception is HttpListenerException or ObjectDisposedException or TaskCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new TimeoutException("Nexus sign-in timed out before the browser returned an authorization code.");
                }

                var result = NexusOAuthCallbackValidator.Validate(
                    context.Request.Url,
                    context.Request.QueryString,
                    _redirectUri,
                    expectedState);
                await WriteBrowserResponseAsync(context.Response, result, linkedSource.Token).ConfigureAwait(false);

                if (!ShouldKeepListening(result))
                {
                    return result;
                }
            }
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

    private static bool ShouldKeepListening(NexusOAuthCallbackResult result)
    {
        return result.Error is "invalid_callback_target" or "invalid_state" or "duplicate_parameter";
    }

    private static async Task WriteBrowserResponseAsync(
        HttpListenerResponse response,
        NexusOAuthCallbackResult result,
        CancellationToken cancellationToken)
    {
        var isSuccess = result.IsSuccess;
        response.StatusCode = isSuccess
            ? 200
            : result.Error == "invalid_callback_target" ? 404 : 400;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        var html = isSuccess
            ? "<!doctype html><html><body><h1>UCU Mod Manager</h1><p>Nexus sign-in completed. You can close this browser tab.</p></body></html>"
            : "<!doctype html><html><body><h1>UCU Mod Manager</h1><p>Nexus sign-in was not accepted. Return to the mod manager and try again.</p></body></html>";
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
