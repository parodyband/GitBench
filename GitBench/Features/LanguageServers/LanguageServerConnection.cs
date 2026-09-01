using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

internal sealed class LanguageServerConnection : ILanguageServerProcess
{
    private readonly ILanguageServerSession _server;
    private readonly LanguageServerEntry _entry;
    private readonly AskAgainPolicy _retry;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task<string?> _handshake;
    private readonly object _gate = new();

    private Action<ServerExit>? _exited;
    private ServerExit? _ending;
    private int _disposed;

    public LanguageServerConnection(
        ILanguageServerSession server,
        LanguageServerEntry entry,
        TimeSpan handshakeTimeout,
        AskAgainPolicy? retry = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _server = server;
        _entry = entry;
        _retry = retry ?? AskAgainPolicy.Default;
        _wait = wait ?? Task.Delay;

        server.ReadinessChanged += OnReadinessChanged;
        server.Exited += OnExited;
        _handshake = HandshakeAsync(handshakeTimeout);
    }

    public event Action<ServerReadiness>? ReadinessChanged;

    public event Action<ServerExit>? Exited
    {
        add
        {
            ServerExit? already;
            lock (_gate)
            {
                already = _ending;
                if (already is null) _exited += value;
            }

            if (already is { } exit) value?.Invoke(exit);
        }
        remove
        {
            lock (_gate) _exited -= value;
        }
    }

    public async Task<HoverText?> HoverAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return null;

        var uri = DocumentUri.OfFile(absolutePath);
        if (!await EnsureOpenAsync(uri, absolutePath, cancel).ConfigureAwait(false)) return null;

        var at = new LspPosition(LspLine.FromOneBased(line.Value), new LspCharacter(column.Value));
        var response = await AskAgain
            .AskAsync(
                token => _server.AskAsync(LspRequests.Hover(uri, at), _entry.RequestTimeout, token),
                _retry,
                _wait,
                cancel)
            .ConfigureAwait(false);

        return response is LspResponse<Hover>.Ok(var hover) ? HoverText.Of(hover) : null;
    }

    public async Task PrepareAsync(string absolutePath, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return;

        var uri = DocumentUri.OfFile(absolutePath);
        if (!await EnsureOpenAsync(uri, absolutePath, cancel).ConfigureAwait(false)) return;
        Probe(uri);
    }

    public void RequestShutdown() => _server.RequestShutdown();

    public void Kill() => _server.Kill();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _server.ReadinessChanged -= OnReadinessChanged;
        _server.Exited -= OnExited;
        lock (_gate) _exited = null;
        _closing.Cancel();
        _server.Dispose();
        _closing.Dispose();
    }

    private Task<string?> Handshaked() => _handshake;

    private async Task<string?> HandshakeAsync(TimeSpan timeout)
    {
        string? failure;
        try
        {
            failure = await _server.HandshakeAsync(timeout, _closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "the connection was closed during startup.";
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure is null) return null;

        End(new ServerExit(ExitCode: null, Detail: failure));
        _server.RequestShutdown();
        return failure;
    }

    private async Task<bool> EnsureOpenAsync(DocumentUri uri, string absolutePath, CancellationToken cancel)
    {
        if (_open.Contains(uri.Value)) return true;

        string text;
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length > FileContentLoader.MaxTextBytes) return false;
            text = await File.ReadAllTextAsync(absolutePath, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        await _server
            .OpenAsync(uri, _entry.Language, new DocumentVersion(1), text, cancel)
            .ConfigureAwait(false);
        _open.Add(uri.Value);
        return true;
    }

    private void Probe(DocumentUri uri)
    {
        _ = AskAgain.AskAsync(
            ct => _server.AskAsync(
                LspRequests.Hover(uri, LspPosition.At(0, 0)), _entry.RequestTimeout, ct),
            ReadinessProbe,
            Task.Delay,
            _closing.Token);
    }

    private static readonly AskAgainPolicy ReadinessProbe = new()
    {
        MaxAttempts = 12,
        FirstDelay = TimeSpan.FromMilliseconds(500),
        MaxDelay = TimeSpan.FromSeconds(8),
    };

    private void OnReadinessChanged(ServerReadiness readiness) => ReadinessChanged?.Invoke(readiness);

    private void OnExited(ServerExit exit) => End(exit);

    private void End(ServerExit exit)
    {
        Action<ServerExit>? listeners;
        lock (_gate)
        {
            if (_ending is not null) return;
            _ending = exit;
            listeners = _exited;
            _exited = null;
        }

        listeners?.Invoke(exit);
    }
}
