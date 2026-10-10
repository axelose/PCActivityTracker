using PCActivityTracker.Domain;

namespace PCActivityTracker.Integrations;

/// <summary>
/// Boundary for future opt-in integrations such as Google Calendar.
/// Implementations receive already-recorded activities; tracking never depends on an integration.
/// </summary>
public interface IActivityExportSink
{
    Task ExportAsync(IReadOnlyList<ActivitySession> activities, CancellationToken cancellationToken);
}
