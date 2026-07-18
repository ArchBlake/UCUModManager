namespace UcuModManager.Core.Nexus;

public interface INexusOAuthCallbackListener : IDisposable
{
    void Start();

    Task<NexusOAuthCallbackResult> WaitForCallbackAsync(
        string expectedState,
        CancellationToken cancellationToken = default);
}
