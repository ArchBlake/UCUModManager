using System.Collections.Specialized;
using UcuModManager.Core.Nexus;

namespace UcuModManager.Core.Tests;

public sealed class NexusOAuthCallbackValidatorTests
{
    private static readonly Uri RedirectUri = new("http://127.0.0.1:17142/ucu-modmanager/oauth/callback");

    [Fact]
    public void Validate_AcceptsMatchingStateAndCode()
    {
        var query = Query(("state", "expected"), ("code", "authorization-code"));

        var result = NexusOAuthCallbackValidator.Validate(RedirectUri, query, RedirectUri, "expected");

        Assert.True(result.IsSuccess);
        Assert.Equal("authorization-code", result.AuthorizationCode);
    }

    [Fact]
    public void Validate_RejectsWrongStateBeforeAcceptingOAuthError()
    {
        var query = Query(("state", "wrong"), ("error", "access_denied"));

        var result = NexusOAuthCallbackValidator.Validate(RedirectUri, query, RedirectUri, "expected");

        Assert.Equal("invalid_state", result.Error);
    }

    [Fact]
    public void Validate_ReturnsOAuthErrorOnlyAfterStateMatches()
    {
        var query = Query(("state", "expected"), ("error", "access_denied"));

        var result = NexusOAuthCallbackValidator.Validate(RedirectUri, query, RedirectUri, "expected");

        Assert.Equal("access_denied", result.Error);
    }

    [Fact]
    public void Validate_RejectsDuplicateState()
    {
        var query = Query(("state", "expected"), ("state", "expected"), ("code", "code"));

        var result = NexusOAuthCallbackValidator.Validate(RedirectUri, query, RedirectUri, "expected");

        Assert.Equal("duplicate_parameter", result.Error);
    }

    [Fact]
    public void Validate_RejectsDifferentPathOrOrigin()
    {
        var query = Query(("state", "expected"), ("code", "code"));

        var wrongPath = NexusOAuthCallbackValidator.Validate(
            new Uri("http://127.0.0.1:17142/wrong"), query, RedirectUri, "expected");
        var wrongPort = NexusOAuthCallbackValidator.Validate(
            new Uri("http://127.0.0.1:17143/ucu-modmanager/oauth/callback"), query, RedirectUri, "expected");

        Assert.Equal("invalid_callback_target", wrongPath.Error);
        Assert.Equal("invalid_callback_target", wrongPort.Error);
    }

    private static NameValueCollection Query(params (string Key, string Value)[] values)
    {
        var query = new NameValueCollection();
        foreach (var (key, value) in values)
        {
            query.Add(key, value);
        }

        return query;
    }
}
