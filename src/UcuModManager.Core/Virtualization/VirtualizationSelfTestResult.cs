namespace UcuModManager.Core.Virtualization;

public sealed record VirtualizationSelfTestResult(
    bool IsSupported,
    string LinkMode,
    string Message);
