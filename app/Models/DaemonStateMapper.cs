using Sockseek.Api;

namespace SldlWeb.Models;

/// <summary>
/// Maps Sockseek.Api daemon state values to the app's internal string conventions.
/// </summary>
public static class DaemonStateMapper
{
    /// <summary>
    /// Maps split daemon job state fields to the string stored in TrackInfo.State.
    /// "Pending" maps to "Initial" for backward compatibility with persisted jobs.
    /// Terminal outcomes keep the historical vocabulary (Done, AlreadyExists, …).
    /// </summary>
    public static string ToAppState(
        ServerJobLifecycleState lifecycle,
        ServerJobActivityPhase activity,
        ServerJobTerminalOutcome outcome,
        ServerJobSkipReason skipReason)
    {
        if (lifecycle == ServerJobLifecycleState.Terminal)
            return ToAppTerminalState(outcome, skipReason);

        if (lifecycle == ServerJobLifecycleState.AwaitingSelection)
            return "AwaitingSelection";

        if (lifecycle == ServerJobLifecycleState.Pending)
            return "Initial";

        return activity switch
        {
            ServerJobActivityPhase.WaitingForSearchConcurrency
                or ServerJobActivityPhase.SearchRateLimited
                or ServerJobActivityPhase.Searching
                or ServerJobActivityPhase.ProcessingSearchResults => "Searching",
            ServerJobActivityPhase.Extracting => "Extracting",
            ServerJobActivityPhase.Downloading => "Downloading",
            _ => "Running",
        };
    }

    public static string ToAppState(SongStateChangedEventDto payload)
        => ToAppState(payload.LifecycleState, payload.ActivityPhase, payload.TerminalOutcome, payload.SkipReason);

    public static string ToAppState(JobSummaryDto summary)
        => ToAppState(summary.LifecycleState, summary.ActivityPhase, summary.TerminalOutcome, summary.SkipReason);

    /// <summary>Maps a daemon failure reason to a display string, or null if none.</summary>
    public static string? ToAppFailureReason(ServerJobFailureReason? reason)
        => reason is null or ServerJobFailureReason.None ? null : reason.Value.ToString();

    public static bool IsTerminal(ServerJobLifecycleState lifecycle)
        => lifecycle == ServerJobLifecycleState.Terminal;

    public static bool IsTerminal(JobSummaryDto summary)
        => IsTerminal(summary.LifecycleState);

    public static bool IsTerminal(SongStateChangedEventDto payload)
        => IsTerminal(payload.LifecycleState);

    private static string ToAppTerminalState(ServerJobTerminalOutcome outcome, ServerJobSkipReason skipReason)
        => outcome switch
        {
            ServerJobTerminalOutcome.Succeeded => "Done",
            ServerJobTerminalOutcome.PartialSuccess => "Done",
            ServerJobTerminalOutcome.Failed => "Failed",
            ServerJobTerminalOutcome.Cancelled => "Cancelled",
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.AlreadyExists => "AlreadyExists",
            ServerJobTerminalOutcome.Skipped when skipReason == ServerJobSkipReason.NotFoundLastTime => "NotFoundLastTime",
            ServerJobTerminalOutcome.Skipped => "Skipped",
            _ => "Failed",
        };
}
