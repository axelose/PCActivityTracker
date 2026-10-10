namespace PCActivityTracker.Browser;

public sealed record BrowserBridgeDiagnostics(
    string ListenerStatus,
    string ConnectionState,
    DateTimeOffset? LastReceivedAt,
    string? LastBrowser,
    string? LastDomain,
    string? LastCreator,
    string? ExtensionVersion,
    string ExpectedExtensionVersion,
    long AcceptedCount,
    long RejectedCount,
    int ActiveClients,
    string LastResult)
{
    public bool IsExpectedExtension => string.Equals(ExtensionVersion, ExpectedExtensionVersion, StringComparison.Ordinal);
}
