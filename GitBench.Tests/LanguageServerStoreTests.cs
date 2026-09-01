using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Features.Repos;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using ZGF.Gui;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The app's side of running servers: one repository's at a time, started by the first file that
/// wants one, and dropped when the repository goes away. Nothing here starts a process — the
/// launcher is a fake — so what is tested is the wiring, which is the part that had the bugs.
/// </summary>
public sealed class LanguageServerStoreTests : IDisposable
{
    private const string ConfigJson =
        """
        {
          "servers": {
            "rust": { "command": "rust-analyzer", "extensions": [".rs"] },
            "go":   { "command": "gopls",         "extensions": [".go"] }
          }
        }
        """;

    private readonly TempDir _dir = new("gitbench-lsp-store-");
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeFileSystem _files = new();
    private readonly RepoRegistry _registry;
    private readonly string _configPath;
    private readonly Guid _first;
    private readonly Guid _second;

    private LanguageServerStore? _store;

    public LanguageServerStoreTests()
    {
        _configPath = Path.Combine(_dir.Path, "language-servers.json");
        _registry = new RepoRegistry(RepoStateStore.Load(Path.Combine(_dir.Path, "state.json")),
            Path.Combine(_dir.Path, "state.json"));
        _first = OpenRepo("first", "Cargo.toml");
        _second = OpenRepo("second", "go.mod");
    }

    public void Dispose()
    {
        _store?.Dispose();
        _registry.Dispose();
        _dir.Dispose();
    }

    private Guid OpenRepo(string name, string marker)
    {
        var path = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        Directory.CreateDirectory(Path.Combine(path, "src"));
        File.WriteAllText(Path.Combine(path, marker), "");
        File.WriteAllText(Path.Combine(path, "src", "main.rs"), "fn main() {}");
        _registry.Open(path);
        _files.With(Path.GetFullPath(path), marker, "src");
        return _registry.Active.Value!.Id;
    }

    private string File_(Guid repo, string relative) =>
        Path.Combine(Path.GetFullPath(_registry.Repos.Single(r => r.Id == repo).Path), relative);

    private LanguageServerStore Store(string? config = ConfigJson)
    {
        if (config is not null) File.WriteAllText(_configPath, config);
        var store = new LanguageServerStore(
            _registry,
            _files,
            _dispatcher,
            new LanguageServerLauncher(_launcher, TimeSpan.FromSeconds(5)),
            clock: null,
            configPath: _configPath);
        store.Start();
        _store = store;
        return store;
    }

    private static Task<HoverText?> Hover(LanguageServerStore store, string path) =>
        store.HoverAsync(
            Path.GetDirectoryName(path)!, path, new FileLine(1), new RawColumn(3), CancellationToken.None);

    [Fact]
    public void WithNoConfigFileNothingIsClaimedAndNothingRuns()
    {
        var store = Store(config: null);

        Assert.False(store.Handles(File_(_second, "src/main.rs")));
        Assert.False(store.Active.Value.ConfigFileExists);
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task AHoverOnAClaimedFileStartsExactlyOneServer()
    {
        var store = Store();
        _registry.SetActive(_first);

        var hover = await Hover(store, File_(_first, "src/main.rs"));

        Assert.Single(_launcher.Started);
        Assert.Equal("`fn main()`", hover!.Markdown);
    }

    [Fact]
    public async Task ASecondHoverReusesTheServerTheFirstStarted()
    {
        var store = Store();
        _registry.SetActive(_first);

        await Hover(store, File_(_first, "src/main.rs"));
        await Hover(store, File_(_first, "src/main.rs"));

        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task AFileNoServerClaimsStartsNothing()
    {
        var store = Store();
        _registry.SetActive(_first);
        File.WriteAllText(File_(_first, "README.md"), "# hi");

        Assert.Null(await Hover(store, File_(_first, "README.md")));
        Assert.Empty(_launcher.Started);
    }

    // "Active repository only" is the memory policy the whole feature rests on: a hover in a
    // repository the user is not looking at must not start a server for it.
    [Fact]
    public async Task AHoverInARepositoryThatIsNotActiveStartsNothing()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.Null(await Hover(store, File_(_second, "src/main.rs")));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task TheActiveRepositorysServersAreTheOnesReported()
    {
        var store = Store();
        _registry.SetActive(_first);
        await Hover(store, File_(_first, "src/main.rs"));

        Assert.NotEmpty(store.Active.Value.Servers);

        _registry.SetActive(_second);

        Assert.Empty(store.Active.Value.Servers);
        Assert.IsType<ServerState.Stopped>(store.Active.Value.StateFor(File_(_second, "src/main.rs")));
    }

    [Fact]
    public async Task ClosingARepositoryStopsItsServer()
    {
        var store = Store();
        _registry.SetActive(_first);
        await Hover(store, File_(_first, "src/main.rs"));
        var server = _launcher.Started[0];

        _registry.RemoveRepo(_first);

        Assert.True(server.ShutdownRequests > 0);
    }

    [Fact]
    public async Task AStoppedServerIsStartedAgainByTheNextFileThatWantsIt()
    {
        var store = Store();
        _registry.SetActive(_first);
        await Hover(store, File_(_first, "src/main.rs"));

        store.StopServer(LanguageId.Of("rust"));
        Assert.IsType<ServerState.Stopped>(store.Active.Value.StateFor(File_(_first, "src/main.rs")));

        await Hover(store, File_(_first, "src/main.rs"));

        Assert.Equal(2, _launcher.Started.Count);
    }

    [Fact]
    public void ReloadingPicksUpAConfigWrittenAfterStartup()
    {
        var store = Store(config: null);
        Assert.False(store.Handles(File_(_first, "src/main.rs")));

        File.WriteAllText(_configPath, ConfigJson);
        store.ReloadConfig();

        Assert.True(store.Handles(File_(_first, "src/main.rs")));
        Assert.True(store.Active.Value.ConfigFileExists);
    }

    [Fact]
    public void AConfigFileThatIsNotUnderstoodIsAProblemRatherThanACrash()
    {
        var store = Store(config: "{ this is not json");

        Assert.Empty(store.Active.Value.Config.Servers);
        Assert.Single(store.Active.Value.Problems);
    }

    [Fact]
    public void AStarterConfigIsWrittenForTheLanguagesTheRepositoryIsWrittenIn()
    {
        var store = Store(config: null);
        _registry.SetActive(_first);
        WaitForSuggestions(store);

        Assert.Equal(StarterConfigOutcome.Written, store.WriteStarterConfig());

        Assert.True(File.Exists(_configPath));
        Assert.Equal("rust", store.Active.Value.Config.Servers.Single().Language.Value);
    }

    // The config file is hand-written, comments and all. Offering to create one must never be a way
    // to overwrite one.
    [Fact]
    public void AConfigFileThatAlreadyExistsIsNeverRewritten()
    {
        var store = Store();
        _registry.SetActive(_first);

        Assert.Equal(StarterConfigOutcome.AlreadyExists, store.WriteStarterConfig());
        Assert.Equal(ConfigJson, File.ReadAllText(_configPath));
    }

    // The repository root is listed off the UI thread, so what a suggestion depends on lands when
    // the disk answers rather than when the test asks.
    private static void WaitForSuggestions(LanguageServerStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Active.Value.Suggestions.Count > 0) return;
            Thread.Sleep(10);
        }

        Assert.Fail("no suggestion ever arrived for the repository root");
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeFileSystem : IFileSystemReader
    {
        private readonly Dictionary<string, List<FileSystemEntry>> _directories = new(StringComparer.Ordinal);

        public void With(string directory, params string[] names) =>
            _directories[directory] = names.Select(n => new FileSystemEntry(n, false, false, false)).ToList();

        public DirectoryListing List(string absoluteDirectory, CancellationToken cancellation)
        {
            return _directories.TryGetValue(absoluteDirectory, out var entries)
                ? new DirectoryListing.Listed(entries)
                : DirectoryListing.Empty;
        }

        public string? ResolveLinkTarget(string absolutePath) => null;
    }

    /// <summary>Hands out servers that answer one hover and nothing else.</summary>
    private sealed class FakeProcessLauncher : ILanguageServerLauncher
    {
        public List<FakeSession> Started { get; } = [];

        public LaunchResult Launch(ServerLaunchRequest request)
        {
            var session = new FakeSession();
            Started.Add(session);
            return new LaunchResult.Started(session);
        }
    }

    private sealed class FakeSession : ILanguageServerSession
    {
        public event Action<ServerReadiness>? ReadinessChanged;
        public event Action<ServerExit>? Exited;

        public int ShutdownRequests { get; private set; }

        public Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel)
        {
            ReadinessChanged?.Invoke(new ServerReadiness.Handshaked());
            return Task.FromResult<string?>(null);
        }

        public Task OpenAsync(
            DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel) =>
            Task.CompletedTask;

        public Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel) =>
            Task.FromResult((LspResponse<T>)(object)new LspResponse<Hover>.Ok(
                new Hover.Text(MarkupKind.Markdown, "`fn main()`", null)));

        public void RequestShutdown()
        {
            ShutdownRequests++;
            Exited?.Invoke(new ServerExit(0));
        }

        public void Kill() => Exited?.Invoke(new ServerExit());

        public void Dispose() { }
    }
}
