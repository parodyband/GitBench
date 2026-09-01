using GitBench.Lsp.Lifecycle;
using GitBench.Lsp.Tests.Fakes;
using Xunit;

namespace GitBench.Lsp.Tests;

/// <summary>
/// What happens after a server dies. A client that restarts forever burns a core on a server that
/// will never work; one that never restarts leaves the pane dead until the app does.
/// </summary>
public class ServerRestartTests
{
    static readonly SupervisorPolicy Backoff = new()
    {
        MaxRestartAttempts = 3,
        FirstRestartDelay = TimeSpan.FromSeconds(1),
        RestartDelayGrowth = 2,
        StableRunTime = TimeSpan.FromMinutes(5),
        ReadySilence = TimeSpan.FromMinutes(10),
    };

    static readonly SupervisorPolicy OneAttempt = Backoff with { MaxRestartAttempts = 1 };

    const string RustFile = "src/main.rs";

    [Fact]
    public void AServerThatCrashes_ComesBackByItself()
    {
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.BecomeReady();

        harness.Launcher.Last.Crash();
        var restarting = Assert.IsType<ServerState.Restarting>(harness.Servers.StateFor(harness.File(RustFile)));
        harness.Advance(restarting.Delay);

        Assert.Equal(2, harness.Launcher.Started.Count);
        Assert.IsType<ServerState.Starting>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    [Fact]
    public void AServerThatCrashed_WaitsForItsDelayBeforeComingBack()
    {
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.Crash();

        var restarting = Assert.IsType<ServerState.Restarting>(harness.Servers.StateFor(harness.File(RustFile)));
        harness.Advance(restarting.Delay - TimeSpan.FromMilliseconds(1));

        Assert.Single(harness.Launcher.Started);
    }

    [Fact]
    public void EachCrashInARow_WaitsLongerThanTheLast()
    {
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.Last.Crash();
        var first = Assert.IsType<ServerState.Restarting>(harness.Servers.StateFor(harness.File(RustFile)));
        harness.Advance(first.Delay);

        harness.Launcher.Last.Crash();
        var second = Assert.IsType<ServerState.Restarting>(harness.Servers.StateFor(harness.File(RustFile)));

        Assert.True(second.Delay > first.Delay, $"Backoff did not grow: {first.Delay} then {second.Delay}.");
    }

    [Fact]
    public void AServerThatKeepsCrashing_IsGivenUpOnInsteadOfLoopingForever()
    {
        using var harness = new SupervisorHarness(policy: OneAttempt);
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.Last.Crash();
        harness.Advance(TimeSpan.FromSeconds(5));
        harness.Launcher.Last.Crash();

        Assert.IsType<ServerState.Failed>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    [Fact]
    public void AServerGivenUpOn_IsNotStartedAgainByTimePassing()
    {
        using var harness = new SupervisorHarness(policy: OneAttempt);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.Crash();
        harness.Advance(TimeSpan.FromSeconds(5));
        harness.Launcher.Last.Crash();

        harness.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(2, harness.Launcher.Started.Count);
    }

    [Fact]
    public void AServerGivenUpOn_IsNotStartedAgainByOpeningAnotherFile()
    {
        using var harness = new SupervisorHarness(policy: OneAttempt);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.Crash();
        harness.Advance(TimeSpan.FromSeconds(5));
        harness.Launcher.Last.Crash();

        var state = harness.Servers.OpenFile(harness.File("src/other.rs"));

        Assert.IsType<ServerState.Failed>(state);
        Assert.Equal(2, harness.Launcher.Started.Count);
    }

    [Fact]
    public void AServerThatRanWellForALongTime_GetsAFreshBudgetWhenItCrashes()
    {
        // Otherwise a server that dies once a day is permanently dead by the end of the week.
        using var harness = new SupervisorHarness(policy: OneAttempt);
        harness.Servers.OpenFile(harness.File(RustFile));
        harness.Launcher.Last.BecomeReady();
        harness.Launcher.Last.Crash();
        harness.Advance(TimeSpan.FromSeconds(5));
        harness.Launcher.Last.BecomeReady();

        harness.Advance(OneAttempt.StableRunTime + TimeSpan.FromMinutes(1));
        harness.Launcher.Last.Crash();

        var restarting = Assert.IsType<ServerState.Restarting>(harness.Servers.StateFor(harness.File(RustFile)));
        Assert.Equal(1, restarting.Attempt);
    }

    [Fact]
    public void AServerThatCrashesBeforeItEverAnswered_IsRestartedLikeAnyOther()
    {
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.Last.Crash();

        Assert.IsType<ServerState.Restarting>(harness.Servers.StateFor(harness.File(RustFile)));
    }

    [Fact]
    public void ACommandThatIsNotInstalled_FailsAtOnceWithoutRetrying()
    {
        // Backoff is for a server that crashed. A binary that is not on PATH will not appear in
        // two seconds, and retrying it just delays telling the user what is wrong.
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");

        var state = harness.Servers.OpenFile(harness.File(RustFile));
        harness.Advance(TimeSpan.FromMinutes(10));

        Assert.IsType<ServerState.Failed>(state);
        Assert.Single(harness.Launcher.Requests);
    }

    [Fact]
    public void AFailure_CarriesSomethingToShowTheUser()
    {
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");

        var failed = Assert.IsType<ServerState.Failed>(harness.Servers.OpenFile(harness.File(RustFile)));

        Assert.NotEmpty(failed.Reason);
    }

    [Fact]
    public void AskingAgainAfterAFailure_StartsTheServerFresh()
    {
        // Giving up is only safe because the user can say "try it again" once they have fixed
        // whatever it was.
        using var harness = new SupervisorHarness(policy: Backoff);
        harness.Launcher.FailEveryLaunch("rust-analyzer: not found");
        harness.Servers.OpenFile(harness.File(RustFile));

        harness.Launcher.StopFailing();
        var state = harness.Servers.Retry(harness.File(RustFile));

        Assert.IsType<ServerState.Starting>(state);
        Assert.Single(harness.Launcher.Started);
    }
}
