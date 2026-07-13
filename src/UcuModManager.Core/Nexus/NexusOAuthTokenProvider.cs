namespace UcuModManager.Core.Nexus;

public sealed class NexusOAuthTokenProvider
{
    private readonly INexusOAuthTokenStore _tokenStore;
    private readonly NexusOAuthClient _oauthClient;
    private readonly NexusOAuthTokenValidator _tokenValidator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NexusOAuthAccessContext? _cachedContext;

    public NexusOAuthTokenProvider(
        INexusOAuthTokenStore tokenStore,
        NexusOAuthClient oauthClient,
        NexusOAuthTokenValidator tokenValidator)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _oauthClient = oauthClient ?? throw new ArgumentNullException(nameof(oauthClient));
        _tokenValidator = tokenValidator ?? throw new ArgumentNullException(nameof(tokenValidator));
    }

    public bool HasStoredTokens => _tokenStore.HasTokens;

    public async Task<NexusOAuthAccessContext> StoreAuthorizedTokensAsync(
        NexusOAuthTokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = await _tokenValidator.ValidateAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            var context = new NexusOAuthAccessContext(tokens, identity);
            _tokenStore.SaveTokens(tokens);
            _cachedContext = context;
            return context;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NexusOAuthAccessContext> GetAccessContextAsync(
        NexusOAuthOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedContext is not null && !_cachedContext.Tokens.IsExpired())
            {
                return _cachedContext;
            }

            var tokens = _cachedContext?.Tokens ?? _tokenStore.LoadTokens();
            if (tokens is null)
            {
                throw new NexusOAuthAuthenticationRequiredException("Connect a Nexus account before using Nexus API features.");
            }

            if (tokens.IsExpired())
            {
                return await RefreshAndValidateAsync(options, tokens, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var identity = await _tokenValidator.ValidateAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
                _cachedContext = new NexusOAuthAccessContext(tokens, identity);
                return _cachedContext;
            }
            catch (NexusOAuthTokenValidationException exception) when (exception.IsExpired)
            {
                return await RefreshAndValidateAsync(options, tokens, cancellationToken).ConfigureAwait(false);
            }
            catch (NexusOAuthTokenValidationException exception)
            {
                ClearTokens();
                throw new NexusOAuthAuthenticationRequiredException(
                    "The saved Nexus authorization is invalid. Connect the Nexus account again.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Disconnect()
    {
        ClearTokens();
    }

    private async Task<NexusOAuthAccessContext> RefreshAndValidateAsync(
        NexusOAuthOptions options,
        NexusOAuthTokenSet currentTokens,
        CancellationToken cancellationToken)
    {
        NexusOAuthTokenSet refreshedTokens;
        try
        {
            refreshedTokens = await _oauthClient
                .RefreshAsync(options, currentTokens.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NexusOAuthException exception) when (exception.RequiresReauthentication)
        {
            ClearTokens();
            throw new NexusOAuthAuthenticationRequiredException(
                "Nexus authorization was revoked or expired. Connect the Nexus account again.",
                exception);
        }

        try
        {
            var identity = await _tokenValidator
                .ValidateAsync(refreshedTokens.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            var context = new NexusOAuthAccessContext(refreshedTokens, identity);
            _tokenStore.SaveTokens(refreshedTokens);
            _cachedContext = context;
            return context;
        }
        catch (NexusOAuthTokenValidationException exception)
        {
            ClearTokens();
            throw new NexusOAuthAuthenticationRequiredException(
                "Nexus returned an invalid refreshed authorization. Connect the Nexus account again.",
                exception);
        }
    }

    private void ClearTokens()
    {
        _cachedContext = null;
        _tokenStore.ClearTokens();
    }
}
