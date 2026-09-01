using GitBench.Localization;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// A server's state in words. One mapping for every surface that shows one, so the chip in the
/// Files pane and the row in the settings card can never describe the same server differently.
/// </summary>
internal static class ServerStateText
{
    public static string Of(ServerState state, Strings s) => state switch
    {
        ServerState.NotConfigured => s.LanguageServersNotConfigured,
        ServerState.Stopped => s.LanguageServersStatusStopped,
        ServerState.Starting => s.LanguageServersStatusStarting,
        ServerState.Indexing { PercentComplete: { } percent } =>
            s.LanguageServersStatusIndexingPercent(percent.ToString()),
        ServerState.Indexing => s.LanguageServersStatusIndexing,
        ServerState.Ready => s.LanguageServersStatusReady,
        ServerState.Restarting restarting => s.LanguageServersStatusRestarting(restarting.Attempt.ToString()),
        ServerState.Failed => s.LanguageServersStatusFailed,
        _ => throw new NotSupportedException($"unhandled server state {state.GetType().Name}"),
    };

    /// <summary>
    /// The longer form: what the state says, plus what a failure said about itself. A reason is the
    /// only part of this a user can act on, and it is too long for a chip.
    /// </summary>
    public static string Detailed(ServerState state, Strings s) =>
        state is ServerState.Failed failed
            ? $"{Of(state, s)} — {failed.Reason}"
            : Of(state, s);

    /// <summary>Whether a state is worth showing beside the file at all. A file no server claims is
    /// the ordinary case — most files in most repositories — and says nothing.</summary>
    public static bool WorthShowing(ServerState state) => state is not ServerState.NotConfigured;
}
