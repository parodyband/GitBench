using GitBench.Features.Diff;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// Watches the pointer over a previewed file and asks the language server about whatever it rests
/// on.
/// </summary>
/// <remarks>
/// <para>
/// A question is asked only after the pointer has been still for <see cref="DwellMs"/>, and only
/// when it has moved to a different place in the file. Dragging across a line would otherwise ask a
/// question per pixel, and a language server answers a hover in single-digit milliseconds only once
/// it has finished indexing — before that, every one of them is work it does instead of indexing.
/// </para>
/// <para>
/// An unanswered question is not a failure: while a project is loading the server says "ask again",
/// and a reader who has already moved on should see nothing rather than an error.
/// </para>
/// </remarks>
internal sealed class HoverProbeController : KeyboardMouseController, IDisposable
{
    private const int DwellMs = 350;

    private readonly DiffContentView _surface;
    private readonly LanguageServerService _servers;
    private readonly HoverPopupService _popups;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<(string Root, string Path)?> _document;

    private CancellationTokenSource? _pending;
    private FilePositionHit? _asking;
    private FilePositionHit? _showing;

    public HoverProbeController(
        DiffContentView surface,
        LanguageServerService servers,
        HoverPopupService popups,
        IUiDispatcher dispatcher,
        Func<(string Root, string Path)?> document)
    {
        _surface = surface;
        _servers = servers;
        _popups = popups;
        _dispatcher = dispatcher;
        _document = document;
    }

    public override void OnMouseExit(ref MouseExitEvent e) => Dismiss();

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        // Whether the pointer is over the text is the hit test's answer, not a flag kept in step
        // with enter and exit events: a position that maps to no line is already "not here".
        var at = _surface.HitTestFilePosition(e.Mouse.Point);
        if (at is null)
        {
            Dismiss();
            return;
        }

        // Still in the same place: leave both the popup and the question in flight alone. The
        // position, not the popup, is what makes a move new — a pointer resting still produces a
        // stream of identical moves, and restarting on each of them cancels the wait every time it
        // is about to end, so the question is asked forever and never answered.
        if (_asking is { } asking && asking == at.Value) return;
        if (_showing is { } shown && shown == at.Value) return;

        Cancel();
        _showing = null;
        _popups.Hide(this);

        if (_document() is not { } document) return;
        if (!_servers.Handles(document.Path)) return;

        Ask(document.Root, document.Path, at.Value, e.Mouse.Point);
    }

    private void Ask(string repoRoot, string path, FilePositionHit at, PointF anchor)
    {
        var cancel = new CancellationTokenSource();
        _pending = cancel;
        _asking = at;
        var token = cancel.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DwellMs, token).ConfigureAwait(false);
                var hover = await _servers
                    .HoverAsync(repoRoot, path, at.Line, at.Column, token)
                    .ConfigureAwait(false);
                if (hover is null || token.IsCancellationRequested) return;

                _dispatcher.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _showing = at;
                    _popups.Show(this, hover, new RectF(anchor.X, anchor.Y, 1, 1));
                });
            }
            catch (OperationCanceledException)
            {
                // The pointer moved on. There was never an answer worth showing.
            }
            catch (Exception)
            {
                // A question about a symbol is not worth a crash dialog, and the thing that failed
                // is a subprocess the user did not ask to run. The popup simply stays away.
            }
        }, token);
    }

    private void Dismiss()
    {
        Cancel();
        _showing = null;
        _asking = null;
        _popups.Hide(this);
    }

    private void Cancel()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        _asking = null;
    }

    public void Dispose() => Dismiss();
}
