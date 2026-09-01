using GitBench.App;
using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>What a language server did to a file the app asked about, for the surfaces that show
/// it.</summary>
internal enum StarterConfigOutcome
{
    Written,
    AlreadyExists,
    NotWritten,
}

/// <summary>
/// The one place language servers live: which repository's servers may run, what they are doing,
/// and what the user's config file says.
/// </summary>
internal interface ILanguageServerStore : IHoverSource
{
    /// <summary>The active repository's servers. Swaps on repo switch, so a surface binds to this
    /// and never asks which repository it is showing.</summary>
    IReadable<LanguageServerSnapshot> Active { get; }

    /// <summary>Where the config file is, whether or not it exists.</summary>
    string ConfigPath { get; }

    /// <summary>Re-reads the config file. Servers whose launch changed restart, servers that left
    /// it stop, and servers that only had a timeout edited keep running.</summary>
    void ReloadConfig();

    /// <summary>Starts a given-up server again. The only way one comes back, which is what makes
    /// giving up safe.</summary>
    void RetryServer(LanguageId language);

    /// <summary>Stops one server. It starts again the next time a file of its language is read.</summary>
    void StopServer(LanguageId language);

    /// <summary>Writes a config file for the languages this repository is written in. Refuses when
    /// a config file already exists: it is hand-written, comments and all, and not ours to
    /// rewrite.</summary>
    StarterConfigOutcome WriteStarterConfig();
}

/// <summary>
/// Owns every language server the app runs, keyed by repository, and answers the Files pane's
/// questions about the file it is showing.
/// </summary>
/// <remarks>
/// <para>
/// Does nothing at all until <c>language-servers.json</c> exists. No file means no configuration
/// means no process and no timer, which is what keeps this feature free for everyone who never
/// asks for it.
/// </para>
/// <para>
/// Mirrors <see cref="IFileBrowserStore"/>'s shape: per-repo state, an <see cref="Active"/>
/// projection that swaps on repo switch, entries dropped when a repository leaves the registry, and
/// a <see cref="Start"/> that wires the registry once the UI loop exists. The lifecycle rules
/// themselves — start on first file, restart with backoff, give up, idle shutdown, the concurrency
/// cap — belong to <see cref="LanguageServerSupervisor"/>; this holds the app's side of them.
/// </para>
/// </remarks>
internal sealed class LanguageServerStore : ILanguageServerStore, IHostedService, IDisposable
{
    public const string ConfigFileName = "language-servers.json";

    /// <summary>How often the supervisor is moved forward. Everything it does on a clock — a
    /// restart coming due, an idle server, a server that ignored a shutdown — happens at this
    /// granularity, and none of it is worth waking the app for more often.</summary>
    private static readonly TimeSpan PumpInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly IRepoRegistry _registry;
    private readonly IFileSystemReader _files;
    private readonly IUiDispatcher _dispatcher;
    private readonly LanguageServerSupervisor _supervisor;
    private readonly State<LanguageServerSnapshot> _active = new(LanguageServerSnapshot.Nothing);
    private readonly Dictionary<Guid, IReadOnlyList<StarterServer>> _suggestions = [];
    private readonly CancellationTokenSource _stopping = new();

    private LanguageServerConfig _config = LanguageServerConfig.Empty;
    private IReadOnlyList<ConfigProblem> _problems = [];
    private bool _configFileExists;
    private IDisposable? _activeSub;
    private IDisposable? _reposSub;
    private bool _started;
    private bool _pumping;
    private bool _disposed;

    public LanguageServerStore(
        IRepoRegistry registry,
        IFileSystemReader files,
        IUiDispatcher dispatcher,
        ILanguageServerLauncher? launcher = null,
        IClock? clock = null,
        string? configPath = null)
    {
        _registry = registry;
        _files = files;
        _dispatcher = dispatcher;
        ConfigPath = configPath ?? AppPaths.AppDataPath(ConfigFileName);
        _supervisor = new LanguageServerSupervisor(
            launcher ?? new LanguageServerLauncher(
                new ProcessLanguageServerLauncher(
                    new MapServerEnvironment(LoginShellEnvironment.ForChildProcess),
                    dispatcher.Post),
                HandshakeTimeout),
            clock ?? SystemClock.Instance);
        _supervisor.StatusChanged += _ => Publish();
        LoadConfig();
    }

    public IReadable<LanguageServerSnapshot> Active => _active;

    public string ConfigPath { get; }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _activeSub = _registry.Active.Subscribe(_ => OnActiveChanged());
        _reposSub = _registry.Repos.Subscribe(_ => DropClosedRepos());
    }

    public bool Handles(string absolutePath) => _config.ServerFor(absolutePath) is not null;

    public async Task<HoverText?> HoverAsync(
        string repoRoot, string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await ConnectionFor(absolutePath).ConfigureAwait(false) is not { } connection) return null;
        return await connection.HoverAsync(absolutePath, line, column, cancel).ConfigureAwait(false);
    }

    public void ReloadConfig()
    {
        if (_disposed) return;
        LoadConfig();
        _suggestions.Clear();
        Publish();
        if (_registry.Active.Value is { } repo) RefreshSuggestions(repo);
    }

    public void RetryServer(LanguageId language)
    {
        if (_disposed || _registry.Active.Value is not { } repo) return;
        _supervisor.RestartServer(new RepositoryId(repo.Id), language);
        EnsurePump();
        Publish();
    }

    public void StopServer(LanguageId language)
    {
        if (_disposed || _registry.Active.Value is not { } repo) return;
        _supervisor.StopServer(new RepositoryId(repo.Id), language);
        Publish();
    }

    public StarterConfigOutcome WriteStarterConfig()
    {
        if (_disposed) return StarterConfigOutcome.NotWritten;

        var path = ConfigPath;
        if (File.Exists(path)) return StarterConfigOutcome.AlreadyExists;

        var suggestions = _active.Value.Suggestions;
        if (suggestions.Count == 0) return StarterConfigOutcome.NotWritten;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StarterServers.ConfigText(suggestions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StarterConfigOutcome.NotWritten;
        }

        ReloadConfig();
        return StarterConfigOutcome.Written;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel();
        _activeSub?.Dispose();
        _reposSub?.Dispose();
        _supervisor.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// The server for a file, started if this is the first file of its language. Every supervisor
    /// call is made on the UI thread, which is the thread it says it is only ever entered from; the
    /// question that follows is asked off it.
    /// </summary>
    private Task<LanguageServerConnection?> ConnectionFor(string absolutePath)
    {
        var answer = new TaskCompletionSource<LanguageServerConnection?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _dispatcher.Post(() =>
        {
            try
            {
                answer.SetResult(Start(absolutePath));
            }
            catch (Exception ex)
            {
                answer.SetException(ex);
            }
        });

        return answer.Task;

        LanguageServerConnection? Start(string path)
        {
            if (_disposed) return null;
            if (_config.ServerFor(path) is not { } entry) return null;
            if (_registry.Active.Value is not { } repo) return null;

            _supervisor.OpenFile(path);
            EnsurePump();
            Publish();
            return _supervisor.ProcessFor(new RepositoryId(repo.Id), entry.Language) as LanguageServerConnection;
        }
    }

    private void OnActiveChanged()
    {
        if (_disposed) return;

        var repo = _registry.Active.Value;
        _supervisor.SetActiveRepository(
            repo is null ? null : new Repository(new RepositoryId(repo.Id), repo.Path));
        Publish();
        if (repo is not null) RefreshSuggestions(repo);
    }

    private void DropClosedRepos()
    {
        if (_disposed) return;

        var open = _registry.Repos.Select(r => r.Id).ToHashSet();
        foreach (var status in _supervisor.Status)
            if (!open.Contains(status.Repository.Value))
                _supervisor.CloseRepository(status.Repository);

        foreach (var id in _suggestions.Keys.Where(id => !open.Contains(id)).ToArray())
            _suggestions.Remove(id);
    }

    /// <summary>
    /// Which languages this repository is written in, from the names at its root. Off the UI thread
    /// because it is a directory read, and kept per repository because switching back to one must
    /// not read the disk again.
    /// </summary>
    private void RefreshSuggestions(Repo repo)
    {
        if (_suggestions.ContainsKey(repo.Id))
        {
            Publish();
            return;
        }

        var (id, path) = (repo.Id, repo.Path);
        var token = _stopping.Token;
        Task.Run(
            () =>
            {
                var names = _files.List(path, token) is DirectoryListing.Listed listed
                    ? listed.Entries.Select(entry => entry.Name).ToArray()
                    : [];

                _dispatcher.Post(() =>
                {
                    if (_disposed) return;
                    _suggestions[id] = StarterServers.SuggestFor(names, _config);
                    Publish();
                });
            },
            token);
    }

    private void LoadConfig()
    {
        var path = ConfigPath;
        _configFileExists = File.Exists(path);
        if (!_configFileExists)
        {
            _config = LanguageServerConfig.Empty;
            _problems = [];
            _supervisor.ApplyConfig(_config);
            Publish();
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _config = LanguageServerConfig.Empty;
            _problems = [new ConfigProblem(ConfigFileName, ex.Message)];
            _supervisor.ApplyConfig(_config);
            Publish();
            return;
        }

        switch (LanguageServerConfig.Parse(text))
        {
            case ConfigParse.Loaded(var config, var problems):
                _config = config;
                _problems = problems;
                break;
            case ConfigParse.NotUnderstood(var error):
                _config = LanguageServerConfig.Empty;
                _problems = [new ConfigProblem(ConfigFileName, error.Line is { } line
                    ? $"line {line}: {error.Message}"
                    : error.Message)];
                break;
            case ConfigParse.Unsupported(var fileVersion, var supported):
                _config = LanguageServerConfig.Empty;
                _problems = [new ConfigProblem(
                    ConfigFileName,
                    $"written for version {fileVersion}; this build understands {supported}.")];
                break;
        }

        _supervisor.ApplyConfig(_config);
        Publish();
    }

    private void Publish()
    {
        if (_disposed) return;

        var repo = _registry.Active.Value;
        _active.Value = repo is null
            ? LanguageServerSnapshot.Nothing with { Config = _config, Problems = _problems, ConfigFileExists = _configFileExists }
            : new LanguageServerSnapshot(
                _config,
                _supervisor.Status.Where(status => status.Repository.Value == repo.Id).ToArray(),
                _problems,
                _suggestions.TryGetValue(repo.Id, out var suggestions) ? suggestions : [],
                _configFileExists);
    }

    // Started the first time anything is running, so a user with no config file never has a timer:
    // idle shutdown and restart backoff only exist once there is a server to apply them to.
    private void EnsurePump()
    {
        if (_pumping || _disposed) return;
        _pumping = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(PumpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                _dispatcher.Post(Tick);
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private void Tick()
    {
        if (_disposed) return;
        _supervisor.Tick();
    }
}

/// <summary>Time as it actually passes, for the supervisor that only ever reads it.</summary>
internal sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    private SystemClock() { }

    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// <summary>
/// Wraps every server the process launcher starts in the connection that speaks to it, so what the
/// supervisor supervises and what the pane asks questions of are the same object.
/// </summary>
internal sealed class LanguageServerLauncher(ILanguageServerLauncher processes, TimeSpan handshakeTimeout)
    : ILanguageServerLauncher
{
    public LaunchResult Launch(ServerLaunchRequest request)
    {
        var launched = processes.Launch(request);
        return launched is LaunchResult.Started { Process: ILanguageServerSession session }
            ? new LaunchResult.Started(new LanguageServerConnection(session, request.Entry, handshakeTimeout))
            : launched;
    }
}
