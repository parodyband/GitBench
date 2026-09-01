using System.Diagnostics;
using System.Text.Json;

namespace GitBench.Lsp.Lifecycle;

/// <summary>
/// Starts language servers as real child processes.
/// </summary>
/// <remarks>
/// <para>
/// The process holds the only handle to the server's input. That is load-bearing: every server
/// measured exits on its input closing, which is what stops a crash of this app leaving a
/// multi-gigabyte indexer behind. Nothing may be interposed that keeps the pipe open.
/// </para>
/// <para>
/// Events reach the supervisor through <paramref name="post"/>, because a process exits and a
/// server reports progress on whatever thread the runtime chooses, and the supervisor holds no lock.
/// </para>
/// </remarks>
public sealed class ProcessLanguageServerLauncher(
    IServerEnvironment environment,
    Action<Action> post,
    TimeProvider? time = null) : ILanguageServerLauncher
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public LaunchResult Launch(ServerLaunchRequest request)
    {
        var entry = request.Entry;
        if (environment.ResolveCommand(entry.Command) is not { } executable)
            return new LaunchResult.Failed($"'{entry.Command}' was not found.");

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.ProjectRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // A list, never a joined string: an argument containing a space or a quote is an argument,
        // not a chance to run something else.
        foreach (var argument in entry.Args) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment.Variables) start.Environment[key] = value;
        foreach (var (key, value) in entry.Environment) start.Environment[key] = value;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new LaunchResult.Failed($"'{entry.Command}' could not be started: {ex.Message}");
        }

        return new LaunchResult.Started(new ProcessLanguageServer(process, request, post, _time));
    }
}

/// <summary>One running server: its process, its connection, and how far along it is.</summary>
public sealed class ProcessLanguageServer : ILanguageServerProcess, ILspServerMessages
{
    private readonly Process _process;
    private readonly Action<Action> _post;
    private readonly LspConnection _connection;
    private readonly CancellationTokenSource _closing = new();

    private ServerReadiness _readiness = new ServerReadiness.Handshaked();
    private int _disposed;

    internal ProcessLanguageServer(
        Process process, ServerLaunchRequest request, Action<Action> post, TimeProvider time)
    {
        _process = process;
        _post = post;
        Request = request;

        _connection = LspConnection.Start(
            new LspChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream),
            this,
            time);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Raise(() => Exited?.Invoke(
            new ServerExit(SafeExitCode(), Detail: null)));
    }

    public event Action<ServerReadiness>? ReadinessChanged;

    public event Action<ServerExit>? Exited;

    public ServerLaunchRequest Request { get; }

    /// <summary>What the server said it can do, once the opening exchange has happened.</summary>
    public ServerCapabilities? Capabilities { get; private set; }

    /// <summary>
    /// Runs the opening exchange. A server that counts positions differently from this client is
    /// refused here rather than allowed to answer with offsets nothing can use.
    /// </summary>
    public async Task<string?> HandshakeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var rootUri = DocumentUri.OfFile(Request.ProjectRoot);

        // Held open across the send: the request writes its params when it goes out, not now.
        using var options = Parse(Request.Entry.InitializationOptionsJson);
        var response = await _connection
            .Send(
                LspHandshake.Initialize(rootUri, System.Environment.ProcessId, options?.RootElement),
                timeout,
                ct)
            .ConfigureAwait(false);

        switch (response)
        {
            case LspResponse<ServerCapabilities>.Ok(var capabilities):
                if (!capabilities.CountsPositionsAsWeDo)
                    return $"server counts positions as {capabilities.PositionEncoding}, which this client cannot address.";
                Capabilities = capabilities;
                await _connection.Notify(LspHandshake.Initialized(), ct).ConfigureAwait(false);
                Advance(new ServerReadiness.Handshaked());
                return null;

            case LspResponse<ServerCapabilities>.TimedOut(var after):
                return $"no answer to the opening request within {after.TotalSeconds:0}s.";

            case LspResponse<ServerCapabilities>.Failed(_, var message):
                return $"the opening request was refused: {message}";

            case LspResponse<ServerCapabilities>.Disconnected(var reason):
                return $"the server ended during startup: {reason}";

            default:
                return "the server did not complete the opening request.";
        }
    }

    /// <summary>
    /// Asks the server something, and promotes it to ready on the first real answer. Readiness is
    /// reported from an answer rather than from the handshake because a server can complete the
    /// handshake in milliseconds and still be half a minute from knowing anything.
    /// </summary>
    public async Task<LspResponse<T>> AskAsync<T>(LspRequest<T> request, TimeSpan timeout, CancellationToken ct)
    {
        var response = await _connection.Send(request, timeout, ct).ConfigureAwait(false);
        if (response is LspResponse<T>.Ok) Advance(new ServerReadiness.Ready());
        return response;
    }

    public void RequestShutdown()
    {
        _ = ShutdownAsync();

        async Task ShutdownAsync()
        {
            try
            {
                await _connection.Send(LspHandshake.Shutdown(), TimeSpan.FromSeconds(5), _closing.Token)
                    .ConfigureAwait(false);
                await _connection.Notify(LspHandshake.Exit(), _closing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // A server that will not be told is killed by the supervisor's grace timer.
            }
            finally
            {
                // The close is what actually ends most servers; the exchange above is the polite form.
                try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }

    void ILspServerMessages.OnNotification(ServerNotification notification)
    {
        if (notification is not ServerNotification.Other(var method, var payload)) return;
        if (method != LspMethod.Progress) return;
        if (ReadPercent(payload) is { } percent) Advance(new ServerReadiness.Indexing(percent));
    }

    Task<InboundReply> ILspServerMessages.OnRequest(ServerRequest request, CancellationToken ct) =>
        Task.FromResult<InboundReply>(new InboundReply.NotHandled());

    void ILspServerMessages.OnFault(LspFault fault) { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closing.Cancel();
        _ = _connection.DisposeAsync();
        Kill();
        _closing.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Readiness only moves forward. Progress reports arrive interleaved and out of order, and a
    /// server that has answered once must not be redrawn as still indexing.
    /// </summary>
    private void Advance(ServerReadiness next)
    {
        if (Rank(next) < Rank(_readiness)) return;
        if (next == _readiness) return;
        _readiness = next;
        Raise(() => ReadinessChanged?.Invoke(next));
    }

    private static int Rank(ServerReadiness readiness) => readiness switch
    {
        ServerReadiness.Handshaked => 0,
        ServerReadiness.Indexing => 1,
        ServerReadiness.Ready => 2,
        _ => 0,
    };

    /// <summary>
    /// The server's own options, straight from the config file. Unreadable text is dropped rather
    /// than failing the launch: it is the one field this client never interprets, so the server is
    /// the only thing that could have judged it anyway.
    /// </summary>
    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static int? ReadPercent(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        return value.TryGetProperty("percentage", out var percentage) && percentage.TryGetInt32(out var percent)
            ? percent
            : null;
    }

    private int? SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private void Raise(Action action) => _post(action);
}
