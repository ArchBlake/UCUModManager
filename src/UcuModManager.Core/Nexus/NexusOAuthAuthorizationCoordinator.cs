namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthAuthorizationCoordinator
{
    private readonly NexusOAuthClient _oauthClient;
    private readonly NexusOAuthTokenProvider _tokenProvider;
    private readonly Func<Uri, INexusOAuthCallbackListener> _callbackListenerFactory;

    public NexusOAuthAuthorizationCoordinator(
        NexusOAuthClient oauthClient,
        NexusOAuthTokenProvider tokenProvider,
        Func<Uri, INexusOAuthCallbackListener>? callbackListenerFactory = null)
    {
        _oauthClient = oauthClient ?? throw new ArgumentNullException(nameof(oauthClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _callbackListenerFactory = callbackListenerFactory
            ?? (redirectUri => new NexusOAuthLoopbackCallbackListener(redirectUri));
    }

    public async Task<NexusOAuthAccessContext> ConnectAsync(
        NexusOAuthOptions options,
        Func<Uri, CancellationToken, Task> openAuthorizationUriAsync,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openAuthorizationUriAsync);

        progress?.Report("Preparing secure sign-in...");
        var authorizationRequest = _oauthClient.BuildAuthorizationRequest(options);
        using var callbackListener = _callbackListenerFactory(authorizationRequest.RedirectUri);
        using var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        callbackListener.Start();
        var callbackTask = callbackListener.WaitForCallbackAsync(
            authorizationRequest.State,
            callbackCancellation.Token);

        try
        {
            progress?.Report("Waiting for authorization in your browser...");
            await openAuthorizationUriAsync(authorizationRequest.AuthorizationUri, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            callbackCancellation.Cancel();
            await ObserveCancellationAsync(callbackTask).ConfigureAwait(false);
            throw;
        }

        var callback = await callbackTask.ConfigureAwait(false);
        if (!callback.IsSuccess)
        {
            throw CreateCallbackException(callback);
        }

        progress?.Report("Verifying Nexus authorization...");
        var tokens = await _oauthClient.ExchangeAuthorizationCodeAsync(
                options,
                callback.AuthorizationCode!,
                authorizationRequest.CodeVerifier,
                cancellationToken)
            .ConfigureAwait(false);

        return await _tokenProvider.StoreAuthorizedTokensAsync(tokens, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static NexusOAuthException CreateCallbackException(NexusOAuthCallbackResult callback)
    {
        var errorCode = string.IsNullOrWhiteSpace(callback.Error)
            ? "authorization_failed"
            : callback.Error;
        var message = errorCode.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
            ? "Nexus sign-in was cancelled."
            : "Nexus sign-in was not completed. Please try again.";
        return new NexusOAuthException(message, errorCode: errorCode);
    }

    private static async Task ObserveCancellationAsync(Task callbackTask)
    {
        try
        {
            await callbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            // A callback failure is secondary when the browser could not be opened.
        }
    }
}
