using System.Net;
using System.Text;

namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthLoopbackCallbackListener : INexusOAuthCallbackListener
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpListener _listener = new();
    private readonly Uri _redirectUri;
    private readonly string? _logoDataUri;

    public NexusOAuthLoopbackCallbackListener(Uri redirectUri, byte[]? logoPng = null)
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
        _logoDataUri = logoPng is { Length: > 0 }
            ? $"data:image/png;base64,{Convert.ToBase64String(logoPng)}"
            : null;
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

    private async Task WriteBrowserResponseAsync(
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
        response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        var html = BuildBrowserResponseHtml(isSuccess);
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private string BuildBrowserResponseHtml(bool isSuccess)
    {
        var title = isSuccess ? "Nexus account connected" : "Nexus sign-in failed";
        var message = isSuccess
            ? "Authorization was received securely. You can return to UCU Mod Manager."
            : "Authorization was not accepted. Return to UCU Mod Manager and try again.";
        var statusClass = isSuccess ? "success" : "failure";
        var statusSymbol = isSuccess ? "&#10003;" : "!";
        var logo = _logoDataUri is null
            ? "<div class=\"wordmark\" aria-label=\"UCU Mod Manager\">UCU</div>"
            : $"<img class=\"logo\" src=\"{_logoDataUri}\" alt=\"UCU Mod Manager\">";

        return $$"""
            <!doctype html>
            <html lang="en" data-theme="ucu-dark">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="color-scheme" content="dark">
              <title>{{title}} | UCU Mod Manager</title>
              <style>
                :root { color-scheme: dark; font-family: "Segoe UI", Arial, sans-serif; background: #0f0f0f; }
                * { box-sizing: border-box; }
                html, body { min-height: 100%; margin: 0; background: #0f0f0f; color: #f2f2f2; letter-spacing: 0; }
                body { min-height: 100vh; display: grid; place-items: center; padding: 24px; }
                main { width: min(100%, 520px); padding: 34px 32px 32px; text-align: center; background: #181818; border: 1px solid #303030; border-radius: 6px; box-shadow: 0 18px 55px rgba(0, 0, 0, .32); }
                .logo { display: block; width: min(230px, 68vw); max-height: 148px; margin: 0 auto 24px; object-fit: contain; }
                .wordmark { margin-bottom: 24px; color: #58c7b8; font-size: 42px; font-weight: 700; }
                .status { width: 44px; height: 44px; display: grid; place-items: center; margin: 0 auto 18px; border: 1px solid; border-radius: 50%; font-size: 24px; font-weight: 700; }
                .status.success { color: #69d8ca; border-color: #58c7b8; background: #142c29; }
                .status.failure { color: #ffb45c; border-color: #e89c43; background: #302419; }
                h1 { margin: 0; font-size: 24px; line-height: 1.25; font-weight: 650; }
                p { margin: 12px auto 0; max-width: 410px; color: #b8b8b8; font-size: 15px; line-height: 1.55; }
                .brand { margin-top: 26px; padding-top: 18px; border-top: 1px solid #2a2a2a; color: #58c7b8; font-size: 13px; font-weight: 600; }
                @media (max-width: 520px) { body { padding: 14px; } main { padding: 28px 20px 26px; } h1 { font-size: 21px; } }
              </style>
            </head>
            <body>
              <main aria-labelledby="page-title">
                {{logo}}
                <div class="status {{statusClass}}" aria-hidden="true">{{statusSymbol}}</div>
                <h1 id="page-title">{{title}}</h1>
                <p>{{message}}</p>
                <div class="brand">UCU Mod Manager</div>
              </main>
            </body>
            </html>
            """;
    }
}

public sealed record NexusOAuthCallbackResult(
    string? AuthorizationCode,
    string? Error,
    string? ErrorDescription)
{
    public bool IsSuccess => !string.IsNullOrWhiteSpace(AuthorizationCode) && string.IsNullOrWhiteSpace(Error);
}
