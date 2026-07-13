namespace UcuModManager.Core.Nexus;

public interface INexusOAuthTokenStore
{
    bool HasTokens { get; }

    NexusOAuthTokenSet? LoadTokens();

    void SaveTokens(NexusOAuthTokenSet tokens);

    void ClearTokens();
}
