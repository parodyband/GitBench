using GitBench.Lsp.Configuration;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// The seam in front of process creation. Everything below this line is a real subprocess, a real
/// pipe, and a real protocol client; everything above it is a state machine that can be tested.
/// </summary>
public interface ILanguageServerLauncher
{
    /// <summary>
    /// Spawns the server and starts its handshake. Returns as soon as the process exists —
    /// "started" is not "handshaked" and neither is "ready", which is why they are three things.
    /// </summary>
    LaunchResult Launch(ServerLaunchRequest request);
}

public sealed record ServerLaunchRequest(LanguageServerEntry Entry, string ProjectRoot);

public abstract record LaunchResult
{
    LaunchResult() { }

    public sealed record Started(ILanguageServerProcess Process) : LaunchResult;

    /// <summary>The process never existed — no such command on PATH, no permission, bad root.</summary>
    public sealed record Failed(string Reason) : LaunchResult;
}

/// <summary>
/// A running language server, seen only as something that reports progress, ends, and can be asked
/// or made to stop.
/// </summary>
/// <remarks>
/// Both events are raised on the thread that owns the supervisor. The adapter over a real process
/// marshals them; the supervisor holds no lock and assumes it is never re-entered from elsewhere.
/// </remarks>
public interface ILanguageServerProcess : IDisposable
{
    event Action<ServerReadiness>? ReadinessChanged;

    event Action<ServerExit>? Exited;

    /// <summary>Asks the server to shut down: the LSP exchange, then closing its input.</summary>
    void RequestShutdown();

    /// <summary>Ends the process without asking. For a server that ignored the asking.</summary>
    void Kill();
}

/// <summary>Time, injected, so nothing here needs a real timer or a real wait.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

/// <summary>The timing rules the supervisor applies, all of them measured on the injected clock.</summary>
public sealed record SupervisorPolicy
{
    public static readonly SupervisorPolicy Default = new();

    /// <summary>Consecutive crashes tolerated before the server is given up on.</summary>
    public int MaxRestartAttempts { get; init; } = 3;

    /// <summary>How long after the first crash the server comes back.</summary>
    public TimeSpan FirstRestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Multiplier applied to the delay on each further crash.</summary>
    public double RestartDelayGrowth { get; init; } = 2.0;

    /// <summary>Ceiling on the growing delay.</summary>
    public TimeSpan MaxRestartDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a server must run before a crash counts as a fresh problem rather than another one.
    /// Without it, a server that works for an hour a day burns its whole restart budget in a week.
    /// </summary>
    public TimeSpan StableRunTime { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a server gets to exit politely before it is killed.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a starting server may say nothing at all before it is treated as hung. Any progress
    /// report resets it, so rust-analyzer's 32-second cold start is fine and a wedged server is not.
    /// </summary>
    public TimeSpan ReadySilence { get; init; } = TimeSpan.FromMinutes(2);
}
