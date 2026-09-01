using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Lsp;
using GitBench.Lsp.Configuration;
using GitBench.Lsp.Documents;
using GitBench.Lsp.Lifecycle;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// One running server as the app talks to it: it finishes the opening exchange, remembers which
/// files it has been told about, and asks it questions.
/// </summary>
/// <remarks>
/// <para>
/// It is a <see cref="ILanguageServerProcess"/> as far as the supervisor is concerned, so the
/// supervisor keeps deciding what runs and this keeps deciding what is asked. A handshake that
/// fails ends the process here, with what went wrong as the exit's detail — a server the app cannot
/// speak to is not a server, and the supervisor's restart and give-up rules are the right ones to
/// apply to it.
/// </para>
/// <para>
/// A question the server refuses because it is still indexing is asked again rather than reported
/// as no answer. Every server measured refuses for as long as its first load takes, so the first
/// hover of a session is exactly the one that lands inside that window.
/// </para>
/// </remarks>
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

    /// <summary>
    /// The server ending, delivered even to whoever subscribed too late to hear it. A handshake can
    /// fail before the supervisor has finished attaching to the process it just launched, and an
    /// ending nobody was listening for would leave that server drawn as starting forever.
    /// </summary>
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

    /// <summary>
    /// What the server says about the symbol at a position, or null when it has nothing to say,
    /// never became usable, or the reader moved on before it answered.
    /// </summary>
    public async Task<HoverText?> HoverAsync(
        string absolutePath, FileLine line, RawColumn column, CancellationToken cancel)
    {
        if (await Handshaked().ConfigureAwait(false) is not null) return null;

        var uri = DocumentUri.OfFile(absolutePath);
        if (!await EnsureOpenAsync(uri, absolutePath, cancel).ConfigureAwait(false)) return null;

        // The file counts lines from one and the protocol counts them from zero. This is the only
        // place in the app that crossing happens.
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

    /// <summary>Why the opening exchange failed, or null when it worked. Completes once.</summary>
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

    /// <summary>
    /// Tells the server about a file once. The text comes from disk because disk is the only
    /// writer — nothing here edits — so the copy the server reads and the copy on screen cannot
    /// drift apart. A file too large for the preview is never sent: the preview truncates it, and
    /// answers about a file that was cut short describe a file that does not exist.
    /// </summary>
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

    private void OnReadinessChanged(ServerReadiness readiness) => ReadinessChanged?.Invoke(readiness);

    private void OnExited(ServerExit exit) => End(exit);

    // One end per connection: a handshake failure ends it, and the process ending afterwards is the
    // same ending seen a second time.
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
