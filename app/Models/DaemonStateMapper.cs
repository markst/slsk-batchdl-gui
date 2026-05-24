using Sldl.Api;

namespace SldlWeb.Models;

/// <summary>
/// Maps Sldl.Api daemon state values to the app's internal string conventions.
/// </summary>
public static class DaemonStateMapper
{
    /// <summary>
    /// Maps a daemon ServerJobState to the string stored in TrackInfo.State.
    /// "Pending" maps to "Initial" for backward compatibility with persisted jobs.
    /// All other states map directly to their enum name.
    /// </summary>
    public static string ToAppState(ServerJobState state) => state switch
    {
        ServerJobState.Pending => "Initial",
        _ => state.ToString()
    };

    /// <summary>Maps a daemon failure reason to a display string, or null if none.</summary>
    public static string? ToAppFailureReason(ServerFailureReason? reason)
        => reason is null or ServerFailureReason.None ? null : reason.Value.ToString();

    public static bool IsTerminal(ServerJobState state) => state is
        ServerJobState.Done or ServerJobState.Failed or ServerJobState.AlreadyExists or
        ServerJobState.NotFoundLastTime or ServerJobState.Skipped or ServerJobState.AwaitingSelection;
}
