using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Lifecycle;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// The app's end of one running server: the opening exchange, the file it is told about once, and
/// the question asked again while it says it is still indexing. Driven by a fake server, so none of
/// this needs a subprocess.
/// </summary>
public sealed class LanguageServerConnectionTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-lsp-connection-");
    private readonly FakeSession _server = new();
    private readonly List<TimeSpan> _waited = [];
    private readonly string _file;

    public LanguageServerConnectionTests()
    {
        _file = Path.Combine(_dir.Path, "main.rs");
        File.WriteAllText(_file, "fn main() {}");
    }

    public void Dispose() => _dir.Dispose();

    private LanguageServerConnection Connect() => new(
        _server,
        Entry(),
        TimeSpan.FromSeconds(5),
        AskAgainPolicy.Default with { MaxAttempts = 3 },
        (delay, _) =>
        {
            _waited.Add(delay);
            return Task.CompletedTask;
        });

    private static LanguageServerEntry Entry() => new(
        LanguageId.Of("rust"),
        "rust-analyzer",
        Args: [],
        Extensions: [],
        RootMarkers: [],
        Environment: new Dictionary<string, string>(),
        InitializationOptionsJson: null,
        SettingsJson: null,
        RequestTimeout: TimeSpan.FromSeconds(5),
        IdleShutdown: TimeSpan.FromMinutes(5));

    private Task<HoverText?> Hover(LanguageServerConnection connection, string? path = null) =>
        connection.HoverAsync(path ?? _file, new FileLine(1), new RawColumn(3), CancellationToken.None);

    private static LspResponse<Hover> Answer(string markdown) =>
        new LspResponse<Hover>.Ok(new Hover.Text(MarkupKind.Markdown, markdown, null));

    private static LspResponse<Hover> NotReady() =>
        new LspResponse<Hover>.Retryable(LspErrorCode.ServerNotInitialized, "still indexing");

    [Fact]
    public async Task AHoverIsAnsweredOnceTheServerIsSpokenTo()
    {
        _server.Answers.Enqueue(Answer("`fn main()`"));
        using var connection = Connect();

        var hover = await Hover(connection);

        Assert.Equal("`fn main()`", hover!.Markdown);
        Assert.Equal(1, _server.Handshakes);
    }

    [Fact]
    public async Task TheFileIsOpenedOnceHoweverManyQuestionsAreAsked()
    {
        _server.Answers.Enqueue(Answer("one"));
        _server.Answers.Enqueue(Answer("two"));
        using var connection = Connect();

        await Hover(connection);
        await Hover(connection);

        Assert.Single(_server.Opened);
        Assert.Equal(2, _server.Asks);
    }

    [Fact]
    public async Task ASecondFileIsOpenedInItsOwnRight()
    {
        var other = Path.Combine(_dir.Path, "lib.rs");
        File.WriteAllText(other, "pub fn lib() {}");
        _server.Answers.Enqueue(Answer("one"));
        _server.Answers.Enqueue(Answer("two"));
        using var connection = Connect();

        await Hover(connection);
        await Hover(connection, other);

        Assert.Equal(2, _server.Opened.Count);
    }

    // The bug seen live: the first hover of a session lands while the project is still loading, and
    // one question asked once produces nothing at all.
    [Fact]
    public async Task AServerStillIndexingIsAskedAgain()
    {
        _server.Answers.Enqueue(NotReady());
        _server.Answers.Enqueue(Answer("`fn main()`"));
        using var connection = Connect();

        var hover = await Hover(connection);

        Assert.Equal("`fn main()`", hover!.Markdown);
        Assert.Equal(2, _server.Asks);
        Assert.Single(_waited);
    }

    [Fact]
    public async Task AServerStillIndexingAfterTheLastAttemptSaysNothing()
    {
        for (var i = 0; i < 5; i++) _server.Answers.Enqueue(NotReady());
        using var connection = Connect();

        Assert.Null(await Hover(connection));
        Assert.Equal(3, _server.Asks);
    }

    [Fact]
    public async Task AFailedHandshakeEndsTheConnectionWithWhatWentWrong()
    {
        _server.HandshakeFailure = "server counts positions as utf-8, which this client cannot address.";
        using var connection = Connect();

        Assert.Null(await Hover(connection));
        Assert.Equal(0, _server.Asks);
        Assert.Equal(1, _server.ShutdownRequests);
    }

    [Fact]
    public async Task AConnectionThatFailedItsHandshakeReportsTheReasonAsItsExit()
    {
        var ended = new TaskCompletionSource<ServerExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.HandshakeFailure = "no answer to the opening request within 30s.";
        using var connection = Connect();
        connection.Exited += exit => ended.TrySetResult(exit);

        await Hover(connection);

        var exit = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("no answer to the opening request within 30s.", exit.Detail);
    }

    // A file the preview cut short is not the file the server would read, so it is never sent and
    // never asked about.
    [Fact]
    public async Task AFileTooLargeForThePreviewIsNeverSent()
    {
        var huge = Path.Combine(_dir.Path, "huge.rs");
        File.WriteAllBytes(huge, new byte[FileContentLoader.MaxTextBytes + 1]);
        using var connection = Connect();

        Assert.Null(await Hover(connection, huge));
        Assert.Empty(_server.Opened);
        Assert.Equal(0, _server.Asks);
    }

    [Fact]
    public async Task AFileThatIsNotThereIsNeverSent()
    {
        using var connection = Connect();

        Assert.Null(await Hover(connection, Path.Combine(_dir.Path, "gone.rs")));
        Assert.Empty(_server.Opened);
    }

    [Fact]
    public async Task AnEmptyAnswerIsNoAnswerRatherThanAnEmptyPopup()
    {
        _server.Answers.Enqueue(new LspResponse<Hover>.Ok(new Hover.None()));
        using var connection = Connect();

        Assert.Null(await Hover(connection));
    }

    [Fact]
    public void ShutdownAndKillReachTheServerItself()
    {
        using var connection = Connect();

        connection.RequestShutdown();
        connection.Kill();

        Assert.Equal(1, _server.ShutdownRequests);
        Assert.True(_server.WasKilled);
    }

    [Fact]
    public void ReadinessTheServerReportsIsPassedOn()
    {
        using var connection = Connect();
        ServerReadiness? seen = null;
        connection.ReadinessChanged += readiness => seen = readiness;

        _server.Report(new ServerReadiness.Indexing(70));

        Assert.Equal(70, Assert.IsType<ServerReadiness.Indexing>(seen).PercentComplete);
    }

    // One ending per connection: a server that exits after its handshake already ended it must not
    // report a second crash for the same server.
    [Fact]
    public async Task AServerThatExitsAfterAFailedHandshakeEndsOnlyOnce()
    {
        _server.HandshakeFailure = "the server ended during startup: pipe closed";
        var exits = 0;
        using var connection = Connect();
        connection.Exited += _ => exits++;

        await Hover(connection);
        _server.End(new ServerExit(1));

        Assert.Equal(1, exits);
    }

    private sealed class FakeSession : ILanguageServerSession
    {
        public readonly Queue<LspResponse<Hover>> Answers = new();
        public readonly List<DocumentUri> Opened = [];

        public event Action<ServerReadiness>? ReadinessChanged;
        public event Action<ServerExit>? Exited;

        public string? HandshakeFailure { get; set; }
        public int Handshakes { get; private set; }
        public int Asks { get; private set; }
        public int ShutdownRequests { get; private set; }
        public bool WasKilled { get; private set; }

        public Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken cancel)
        {
            Handshakes++;
            return Task.FromResult(HandshakeFailure);
        }

        public Task OpenAsync(
            DocumentUri uri, LanguageId language, DocumentVersion version, string text, CancellationToken cancel)
        {
            Opened.Add(uri);
            return Task.CompletedTask;
        }

        public Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken cancel)
        {
            Asks++;
            var answer = Answers.Count > 0
                ? Answers.Dequeue()
                : new LspResponse<Hover>.Ok(new Hover.None());
            return Task.FromResult((LspResponse<T>)(object)answer);
        }

        public void Report(ServerReadiness readiness) => ReadinessChanged?.Invoke(readiness);

        public void End(ServerExit exit) => Exited?.Invoke(exit);

        public void RequestShutdown() => ShutdownRequests++;

        public void Kill() => WasKilled = true;

        public void Dispose() { }
    }
}
