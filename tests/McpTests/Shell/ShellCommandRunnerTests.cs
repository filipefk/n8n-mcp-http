using McpTests.Fakes;
using McpToolkit.Shell;

namespace McpTests.Shell;

public class ShellCommandRunnerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mcptests-shell-" + Guid.NewGuid());

    public ShellCommandRunnerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string EchoCommand = "echo hello";
    private static string ExitCommand(int code) => $"exit {code}";

    [Fact]
    public async Task RunAsync_Throws_WhenCommandIsEmpty()
    {
        var runner = TestFactories.BuildShellRunner();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("   ", ShellKind.Auto, null, null));

        Assert.Contains("command", ex.Message);
    }

    [Fact]
    public async Task RunAsync_ExecutesTrivialCommand_AndReturnsExitCodeZero()
    {
        var runner = TestFactories.BuildShellRunner();

        var result = await runner.RunAsync(EchoCommand, ShellKind.Auto, null, null);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StdOut);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_DoesNotThrow_WhenExitCodeIsNonZero()
    {
        var runner = TestFactories.BuildShellRunner();

        var result = await runner.RunAsync(ExitCommand(3), ShellKind.Auto, null, null);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_Throws_WhenWorkingDirectoryDoesNotExist()
    {
        var runner = TestFactories.BuildShellRunner();
        var missingDir = Path.Combine(_tempDir, "does-not-exist");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(EchoCommand, ShellKind.Auto, missingDir, null));

        Assert.Contains(missingDir, ex.Message);
    }

    [Fact]
    public async Task RunAsync_UsesWorkingDirectoryOverride()
    {
        var runner = TestFactories.BuildShellRunner();

        var result = await runner.RunAsync(EchoCommand, ShellKind.Auto, _tempDir, null);

        Assert.Equal(_tempDir, result.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_FallsBackToConfiguredWorkingDirectory_WhenOverrideIsNull()
    {
        var runner = TestFactories.BuildShellRunner(new ShellOptions { WorkingDirectory = _tempDir });

        var result = await runner.RunAsync(EchoCommand, ShellKind.Auto, null, null);

        Assert.Equal(_tempDir, result.WorkingDirectory);
    }

    [Fact]
    public async Task RunAsync_TimeoutOverride_IsCappedByConfiguredCeiling()
    {
        var runner = TestFactories.BuildShellRunner(new ShellOptions { TimeoutSeconds = 5 });

        var result = await runner.RunAsync(EchoCommand, ShellKind.Auto, null, timeoutSecondsOverride: 999);

        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_TruncatesOutput_WhenExceedingMaxOutputChars()
    {
        var runner = TestFactories.BuildShellRunner(new ShellOptions { MaxOutputChars = 3 });

        var result = await runner.RunAsync(EchoCommand, ShellKind.Auto, null, null);

        Assert.True(result.Truncated);
        Assert.Equal(3, result.StdOut.Length);
    }

    [Fact]
    public async Task RunAsync_TimesOut_AndReturnsExitCodeMinusOne()
    {
        var sleepCommand = OperatingSystem.IsWindows()
            ? "Start-Sleep -Seconds 10"
            : "sleep 10";
        var runner = TestFactories.BuildShellRunner(new ShellOptions { TimeoutSeconds = 60 });

        var result = await runner.RunAsync(sleepCommand, ShellKind.Auto, null, timeoutSecondsOverride: 1);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_Throws_WhenCommandIsBlocked()
    {
        const string blockedName = "whoami";
        var runner = TestFactories.BuildShellRunner(new ShellOptions { BlockedCommands = [blockedName] });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(blockedName, ShellKind.Auto, null, null));

        Assert.Contains(blockedName, ex.Message);
    }

    [Fact]
    public async Task RunAsync_Throws_WhenBlockedCommandAppearsAfterChainOperator()
    {
        const string blockedName = "whoami";
        var chained = $"echo ok && {blockedName}";
        var runner = TestFactories.BuildShellRunner(new ShellOptions { BlockedCommands = [blockedName] });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(chained, ShellKind.Auto, null, null));

        Assert.Contains(blockedName, ex.Message);
    }

    [Fact]
    public async Task RunAsync_Throws_WhenCommandNotInAllowList()
    {
        var runner = TestFactories.BuildShellRunner(new ShellOptions { AllowedCommands = ["git"] });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(EchoCommand, ShellKind.Auto, null, null));

        Assert.Contains("echo", ex.Message);
    }

    [Fact]
    public async Task RunAsync_Allows_WhenCommandInAllowList()
    {
        var runner = TestFactories.BuildShellRunner(new ShellOptions { AllowedCommands = ["echo"] });

        var result = await runner.RunAsync(EchoCommand, ShellKind.Auto, null, null);

        Assert.Equal(0, result.ExitCode);
    }
}
