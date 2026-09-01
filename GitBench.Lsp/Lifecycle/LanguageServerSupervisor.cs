using GitBench.Lsp.Configuration;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// Owns every language server process the app runs: starts one when a file first needs it, keeps
/// at most the configured number alive, restarts what crashes until that stops being worth doing,
/// and stops what nothing is looking at.
/// </summary>
/// <remarks>
/// Nothing here waits, sleeps, or holds a timer. Time enters through <see cref="IClock"/> and
/// arrives through <see cref="Tick"/>, which the app pumps; that is what makes crash backoff, idle
/// shutdown, and the kill fallback testable without a real second passing.
/// </remarks>
public sealed class LanguageServerSupervisor : IDisposable
{
    readonly ILanguageServerLauncher _launcher;
    readonly IClock _clock;
    readonly SupervisorPolicy _policy;
    readonly Dictionary<(RepositoryId Repo, LanguageId Language), ServerRecord> _servers = [];

    // The file that last wanted a server, kept past the server's own record: stopping one discards
    // the record, and without this there is nothing left to say where to start it again.
    readonly Dictionary<(RepositoryId Repo, LanguageId Language), string> _lastTrigger = [];
    readonly List<Stopping> _stopping = [];

    LanguageServerConfig _config = LanguageServerConfig.Empty;
    Repository? _active;
    bool _disposed;

    public LanguageServerSupervisor(ILanguageServerLauncher launcher, IClock clock, SupervisorPolicy? policy = null)
    {
        _launcher = launcher;
        _clock = clock;
        _policy = policy ?? SupervisorPolicy.Default;
    }

    /// <summary>Raised whenever any server's state changes, including when it disappears.</summary>
    public event Action<ServerStatus>? StatusChanged;

    /// <summary>Every server the supervisor is tracking, running or not.</summary>
    public IReadOnlyList<ServerStatus> Status =>
        _servers.Values.Select(r => new ServerStatus(r.Repo, r.Language, r.State)).ToList();

    /// <summary>
    /// Takes a freshly read config. Servers whose launch no longer matches restart, servers that
    /// left the config stop, and servers that only had a timeout changed keep running.
    /// </summary>
    public void ApplyConfig(LanguageServerConfig config)
    {
        _config = config;

        foreach (var record in _servers.Values.ToList())
        {
            var entry = config.ServerFor(record.Language);
            if (entry is null)
            {
                Discard(record);
                continue;
            }

            if (record.State is ServerState.Failed)
            {
                // A config edit is the user's answer to a failure. Start clean rather than
                // keeping a verdict reached against a file that no longer exists.
                Discard(record);
                continue;
            }

            if (entry.SameLaunchAs(record.Entry))
            {
                record.Entry = entry;
                continue;
            }

            record.Entry = entry;
            Relaunch(record, resetAttempts: true);
        }

        EnforceCap(null);
    }

    /// <summary>
    /// Switches which repository may run servers. The one being left keeps its servers until they
    /// go idle, so flicking between two repositories does not throw away a warm index.
    /// </summary>
    public void SetActiveRepository(Repository? repository) => _active = repository;

    /// <summary>Stops everything belonging to a repository the app no longer has open.</summary>
    public void CloseRepository(RepositoryId repository)
    {
        if (_active?.Id == repository) _active = null;
        foreach (var record in _servers.Values.Where(r => r.Repo == repository).ToList())
            Discard(record);
    }

    /// <summary>
    /// The Files pane previewing a file. Starts the server for that language if this is the first
    /// such file, and reports where that server stands.
    /// </summary>
    public ServerState OpenFile(string filePath)
    {
        var entry = _config.ServerFor(filePath);
        if (entry is null) return new ServerState.NotConfigured();
        if (_active is not { } active) return new ServerState.Stopped();

        if (_servers.TryGetValue((active.Id, entry.Language), out var existing))
        {
            existing.LastTouched = _clock.Now;
            return existing.State;
        }

        // A file outside the repository — a jump into the standard library — is answered by a
        // server that is already running, and is never a reason to start one: there is no project
        // root out there to start it in.
        if (ProjectRoot.Find(active.RootPath, filePath, entry.RootMarkers) is not { } root)
            return new ServerState.Stopped();

        // Outlives the server it started, so stopping one leaves something to start again from.
        _lastTrigger[(active.Id, entry.Language)] = filePath;

        return Start(active, entry, root, filePath).State;
    }

    /// <summary>Where a file's server stands, without starting anything.</summary>
    public ServerState StateFor(string filePath)
    {
        var entry = _config.ServerFor(filePath);
        if (entry is null) return new ServerState.NotConfigured();
        if (_active is not { } active) return new ServerState.Stopped();
        return StateFor(active.Id, entry.Language);
    }

    /// <summary>
    /// The process currently attached to a server, or null when nothing is running for it — it is
    /// stopped, waiting to be restarted, or given up on. Null is the whole point: a caller cannot
    /// hold a handle to a server the supervisor has since replaced.
    /// </summary>
    public ILanguageServerProcess? ProcessFor(RepositoryId repository, LanguageId language) =>
        _servers.TryGetValue((repository, language), out var record) ? record.Link?.Process : null;

    public ServerState StateFor(RepositoryId repository, LanguageId language) =>
        _servers.TryGetValue((repository, language), out var record)
            ? record.State
            : new ServerState.Stopped();

    /// <summary>
    /// The user asking again after a failure. The only way a given-up server comes back, which is
    /// what makes giving up safe.
    /// </summary>
    public ServerState Retry(string filePath)
    {
        if (_config.ServerFor(filePath) is { } entry &&
            _active is { } active &&
            _servers.TryGetValue((active.Id, entry.Language), out var record) &&
            record.State is ServerState.Failed)
            Discard(record);

        return OpenFile(filePath);
    }

    /// <summary>
    /// Stops one server on request. It is not banned: the next file of its language starts it
    /// again, the same as if it had never run.
    /// </summary>
    public void StopServer(RepositoryId repository, LanguageId language)
    {
        if (_servers.TryGetValue((repository, language), out var record)) Discard(record);
    }

    /// <summary>
    /// Starts a server again from where it was last wanted — the user's answer to one that has
    /// failed, or that they stopped. Nothing comes back on its own after being given up on.
    /// </summary>
    /// <remarks>
    /// The file that started it is remembered past the server itself. Stopping one discards its
    /// record, so without that memory there is nothing left to say which project to start in, and
    /// asking to start it again does nothing at all.
    /// </remarks>
    public ServerState RestartServer(RepositoryId repository, LanguageId language)
    {
        if (_servers.TryGetValue((repository, language), out var record))
        {
            var running = record.TriggerFile;
            Discard(record);
            return _active?.Id == repository ? OpenFile(running) : new ServerState.Stopped();
        }

        if (!_lastTrigger.TryGetValue((repository, language), out var trigger))
            return new ServerState.Stopped();

        return _active?.Id == repository ? OpenFile(trigger) : new ServerState.Stopped();
    }

    /// <summary>
    /// Moves time forward: restarts what is due, stops what has gone idle, kills what ignored a
    /// shutdown, and gives up on what has gone silent.
    /// </summary>
    public void Tick()
    {
        var now = _clock.Now;

        foreach (var record in _servers.Values.ToList())
        {
            if (record.State is ServerState.Restarting && record.RetryAt <= now)
            {
                Relaunch(record, resetAttempts: false);
                continue;
            }

            if (IsIdle(record, now))
            {
                Discard(record);
                continue;
            }

            if (record.Link is not null &&
                record.State is ServerState.Starting or ServerState.Indexing &&
                now - record.LastSignal >= _policy.ReadySilence)
            {
                Detach(record);
                SetState(record, new ServerState.Failed(
                    $"{record.Entry.Command} started but never became usable."));
            }
        }

        foreach (var stopping in _stopping.ToList())
        {
            if (stopping.Deadline > now) continue;
            stopping.Process.Kill();
            Forget(stopping);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var record in _servers.Values.ToList())
            Discard(record);

        foreach (var stopping in _stopping.ToList())
            Forget(stopping);
    }

    bool IsIdle(ServerRecord record, DateTimeOffset now) =>
        record.Repo != _active?.Id &&
        record.State is not ServerState.Failed &&
        now - record.LastTouched >= record.Entry.IdleShutdown;

    ServerRecord Start(Repository repository, LanguageServerEntry entry, string root, string triggerFile)
    {
        var record = new ServerRecord
        {
            Repo = repository.Id,
            RepoRoot = repository.RootPath,
            Language = entry.Language,
            Entry = entry,
            Root = root,
            TriggerFile = triggerFile,
            LastTouched = _clock.Now,
            State = new ServerState.Stopped(),
        };
        _servers[(repository.Id, entry.Language)] = record;

        EnforceCap(record);
        Launch(record);
        return record;
    }

    void Relaunch(ServerRecord record, bool resetAttempts)
    {
        Detach(record);
        if (resetAttempts) record.Attempts = 0;
        record.RetryAt = null;
        record.Root = ProjectRoot.Find(record.RepoRoot, record.TriggerFile, record.Entry.RootMarkers) ?? record.Root;
        Launch(record);
    }

    void Launch(ServerRecord record)
    {
        record.LastSignal = _clock.Now;
        record.StartedAt = _clock.Now;

        switch (_launcher.Launch(new ServerLaunchRequest(record.Entry, record.Root)))
        {
            case LaunchResult.Started started:
                record.Link = Attach(record, started.Process);
                SetState(record, new ServerState.Starting());
                break;

            // A command that is not there will not be there in two seconds either. Backoff is for
            // a server that crashed, not for one that was never installed.
            case LaunchResult.Failed failed:
                SetState(record, new ServerState.Failed(failed.Reason));
                break;
        }
    }

    Attachment Attach(ServerRecord record, ILanguageServerProcess process)
    {
        var link = new Attachment { Process = process };

        link.OnReadiness = readiness =>
        {
            if (!ReferenceEquals(record.Link?.Process, process)) return;
            record.LastSignal = _clock.Now;
            SetState(record, readiness switch
            {
                ServerReadiness.Ready => new ServerState.Ready(),
                ServerReadiness.Indexing indexing => new ServerState.Indexing(indexing.PercentComplete),
                // A finished handshake is progress and nothing more. rust-analyzer finishes its
                // handshake half a minute before it can answer anything.
                _ => record.State is ServerState.Ready or ServerState.Indexing
                    ? record.State
                    : new ServerState.Starting(),
            });
        };

        link.OnExited = exit =>
        {
            if (!ReferenceEquals(record.Link?.Process, process)) return;
            record.Link = null;
            process.Dispose();
            OnCrashed(record, exit);
        };

        process.ReadinessChanged += link.OnReadiness;
        process.Exited += link.OnExited;
        return link;
    }

    void OnCrashed(ServerRecord record, ServerExit exit)
    {
        if (record.StartedAt is { } started && _clock.Now - started >= _policy.StableRunTime)
            record.Attempts = 0;

        record.Attempts++;
        if (record.Attempts > _policy.MaxRestartAttempts)
        {
            SetState(record, new ServerState.Failed(
                $"{record.Entry.Command} stopped {record.Attempts} times in a row{Code(exit)}.{Detail(exit)}"));
            return;
        }

        var delay = RestartDelay(record.Attempts);
        record.RetryAt = _clock.Now + delay;
        SetState(record, new ServerState.Restarting(record.Attempts, delay));
    }

    static string Code(ServerExit exit) => exit.ExitCode is { } code ? $" (exit code {code})" : string.Empty;

    // Whatever ended it said about itself — a refused handshake names something the user can fix,
    // and "stopped three times" on its own names nothing.
    static string Detail(ServerExit exit) =>
        exit.Detail is { Length: > 0 } detail ? $" {detail}" : string.Empty;

    TimeSpan RestartDelay(int attempt)
    {
        var delay = _policy.FirstRestartDelay * Math.Pow(_policy.RestartDelayGrowth, attempt - 1);
        return delay > _policy.MaxRestartDelay ? _policy.MaxRestartDelay : delay;
    }

    // Frees slots until one more server fits. A server for a repository the user is not looking at
    // goes first; after that, whichever has been untouched longest.
    void EnforceCap(ServerRecord? incoming)
    {
        var max = Math.Max(1, _config.MaxConcurrentServers);
        while (true)
        {
            var live = _servers.Values.Where(IsLive).ToList();
            if (live.Count + (incoming is not null && !IsLive(incoming) ? 1 : 0) <= max) return;

            var victim = live
                .Where(r => !ReferenceEquals(r, incoming))
                .OrderBy(r => r.Repo == _active?.Id)
                .ThenBy(r => r.LastTouched)
                .FirstOrDefault();

            if (victim is null) return;
            Discard(victim);
        }
    }

    static bool IsLive(ServerRecord record) => record.Link is not null || record.State is ServerState.Restarting;

    void SetState(ServerRecord record, ServerState state)
    {
        if (record.State == state) return;
        record.State = state;
        StatusChanged?.Invoke(new ServerStatus(record.Repo, record.Language, state));
    }

    /// <summary>Stops a server's process, if any, and forgets the server itself.</summary>
    void Discard(ServerRecord record)
    {
        Detach(record);
        if (_servers.Remove((record.Repo, record.Language)))
            StatusChanged?.Invoke(new ServerStatus(record.Repo, record.Language, new ServerState.Stopped()));
    }

    /// <summary>Hands the process to the polite-shutdown queue and leaves the record behind.</summary>
    void Detach(ServerRecord record)
    {
        if (record.Link is not { } link) return;
        record.Link = null;
        link.Detach();
        BeginShutdown(link.Process);
    }

    void BeginShutdown(ILanguageServerProcess process)
    {
        var stopping = new Stopping
        {
            Process = process,
            Deadline = _clock.Now + _policy.ShutdownGrace,
        };
        stopping.OnExited = _ => Forget(stopping);
        process.Exited += stopping.OnExited;
        _stopping.Add(stopping);

        process.RequestShutdown();
    }

    void Forget(Stopping stopping)
    {
        if (!_stopping.Remove(stopping)) return;
        stopping.Process.Exited -= stopping.OnExited;
        stopping.Process.Dispose();
    }

    sealed class ServerRecord
    {
        public RepositoryId Repo;
        public LanguageId Language;
        public LanguageServerEntry Entry = null!;
        public string RepoRoot = string.Empty;
        public string Root = string.Empty;
        public string TriggerFile = string.Empty;
        public Attachment? Link;
        public ServerState State = new ServerState.Stopped();
        public DateTimeOffset LastTouched;
        public DateTimeOffset LastSignal;
        public DateTimeOffset? StartedAt;
        public int Attempts;
        public DateTimeOffset? RetryAt;
    }

    sealed class Attachment
    {
        public ILanguageServerProcess Process = null!;
        public Action<ServerReadiness> OnReadiness = null!;
        public Action<ServerExit> OnExited = null!;

        public void Detach()
        {
            Process.ReadinessChanged -= OnReadiness;
            Process.Exited -= OnExited;
        }
    }

    sealed class Stopping
    {
        public ILanguageServerProcess Process = null!;
        public Action<ServerExit> OnExited = null!;
        public DateTimeOffset Deadline;
    }
}
