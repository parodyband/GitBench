using GitBench.Features.Notifications;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Lsp.Configuration;
using GitBench.Lsp;
using GitBench.Lsp.Lifecycle;
using GitBench.Messages;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

internal sealed class LanguageServersViewModel : IDialogViewModel
{
    private readonly ILanguageServerStore _store;
    private readonly IMessageBus? _bus;
    private readonly ILocalizationService _loc;
    private readonly IClipboard? _clipboard;
    private const int ReloadSettleMs = 500;

    private readonly State<string?> _reloadResult = new(null);
    private readonly IDisposable _watch;
    private LanguageId? _awaiting;

    public LanguageServersViewModel(
        ILanguageServerStore store,
        ILocalizationService loc,
        IUiDispatcher dispatcher,
        IMessageBus? bus = null,
        IClipboard? clipboard = null)
    {
        _store = store;
        _loc = loc;
        _bus = bus;
        _clipboard = clipboard;

        ReloadCommand = new AsyncCommand(dispatcher, ReloadWork, Reload);
        _watch = store.Active.Subscribe(_ => ReportFailure());

        Servers = new Derived<IReadOnlyList<ConfiguredServer>>(() => store.Active.Value.Configured);
        Suggestions = new Derived<IReadOnlyList<StarterServer>>(() => store.Active.Value.Suggestions);
        Problems = new Derived<IReadOnlyList<ConfigProblem>>(() => store.Active.Value.Problems);
        HasConfigFile = new Derived<bool>(() => store.Active.Value.ConfigFileExists);
        CanCreateConfig = new Derived<bool>(() =>
            !store.Active.Value.ConfigFileExists && store.Active.Value.Suggestions.Count > 0);
    }

    public event Action? CloseRequested;

    public IReadable<string?> ReloadResult => _reloadResult;

    public AsyncCommand ReloadCommand { get; }

    public IReadable<IReadOnlyList<ConfiguredServer>> Servers { get; }

    public IReadable<IReadOnlyList<StarterServer>> Suggestions { get; }

    public IReadable<IReadOnlyList<ConfigProblem>> Problems { get; }

    public IReadable<bool> HasConfigFile { get; }

    public IReadable<bool> CanCreateConfig { get; }

    public string ConfigPath => Normalized(_store.ConfigPath);

    private static string Normalized(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public string Describe(ServerState state) => ServerStateText.Of(state, _loc.Strings.Value);

    private string? ReloadWork()
    {
        Thread.Sleep(ReloadSettleMs);
        return null;
    }

    public void Reload()
    {
        _store.ReloadConfig();

        var s = _loc.Strings.Value;
        var clean = Problems.Value.Count == 0;
        _reloadResult.Value = clean
            ? s.LanguageServersReloadedCount(Servers.Value.Count.ToString())
            : s.LanguageServersReloadedWithProblems;

        Toast(clean
            ? ToastIntent.Success(s.LanguageServersReloaded)
            : ToastIntent.Warning(s.LanguageServersReloadedWithProblems));
    }

    public void Stop(ConfiguredServer server) => _store.StopServer(server.Entry.Language);

    public void Restart(ConfiguredServer server)
    {
        _awaiting = server.Entry.Language;
        _store.RetryServer(server.Entry.Language);
        ReportFailure();
    }

    private void ReportFailure()
    {
        if (_awaiting is not { } language) return;
        if (FailureFor(language) is not { } reason) return;

        _awaiting = null;
        _bus?.Broadcast(new ShowOperationErrorMessage(
            _loc.Strings.Value.LanguageServersTitle, reason, null));
    }

    private string? FailureFor(LanguageId language) =>
        Servers.Value.FirstOrDefault(s => s.Entry.Language.Equals(language))?.State is ServerState.Failed failed
            ? failed.Reason
            : null;

    public void CreateConfig()
    {
        var s = _loc.Strings.Value;
        switch (_store.WriteStarterConfig())
        {
            case StarterConfigOutcome.Written:
                Toast(ToastIntent.Success(s.LanguageServersConfigWritten));
                break;
            case StarterConfigOutcome.AlreadyExists:
                Toast(ToastIntent.Info(s.LanguageServersConfigExists));
                break;
        }
    }

    public void CopyEntry(StarterServer server)
    {
        if (_clipboard is null) return;
        _clipboard.SetText(StarterServers.EntryText(server));
        Toast(ToastIntent.Success(_loc.Strings.Value.LanguageServersSnippetCopied));
    }

    public void Dispose()
    {
        _watch.Dispose();
        CloseRequested = null;
    }

    private void Toast(ToastIntent intent) => _bus?.Broadcast(new ShowToastMessage(intent));
}
