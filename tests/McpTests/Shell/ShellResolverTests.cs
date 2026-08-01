using McpToolkit.Shell;

namespace McpTests.Shell;

public class ShellResolverTests
{
    [Fact]
    public void ResolveDefaultKind_ReturnsPowerShell_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(ShellKind.PowerShell, ShellResolver.ResolveDefaultKind());
    }

    [Fact]
    public void Resolve_UsesConfiguredDefault_WhenRequestedIsAuto()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (fileName, arguments, effective) = ShellResolver.Resolve(ShellKind.Auto, ShellKind.Cmd, "echo hi");

        Assert.Equal(ShellKind.Cmd, effective);
        Assert.Equal("cmd.exe", fileName);
        Assert.Contains(arguments, a => a.Contains("echo hi"));
    }

    [Fact]
    public void Resolve_ExplicitRequestWinsOverConfiguredDefault()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (_, _, effective) = ShellResolver.Resolve(ShellKind.PowerShell, ShellKind.Cmd, "echo hi");

        Assert.Equal(ShellKind.PowerShell, effective);
    }

    [Fact]
    public void Resolve_FallsBackToOsDefault_WhenRequestedAndConfiguredAreAuto()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (_, _, effective) = ShellResolver.Resolve(ShellKind.Auto, ShellKind.Auto, "echo hi");

        Assert.Equal(ShellResolver.ResolveDefaultKind(), effective);
    }

    [Fact]
    public void Resolve_PowerShell_IncludesUtf8Prefix()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (_, arguments, _) = ShellResolver.Resolve(ShellKind.PowerShell, ShellKind.Auto, "echo hi");

        Assert.Contains(arguments, a => a.Contains("OutputEncoding") && a.Contains("echo hi"));
    }

    [Fact]
    public void Resolve_Cmd_IncludesUtf8CodePagePrefix()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (_, arguments, _) = ShellResolver.Resolve(ShellKind.Cmd, ShellKind.Auto, "echo hi");

        Assert.Contains(arguments, a => a.Contains("chcp 65001") && a.Contains("echo hi"));
    }

    [Fact]
    public void Resolve_Throws_WhenBashRequestedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ex = Assert.Throws<InvalidOperationException>(() => ShellResolver.Resolve(ShellKind.Bash, ShellKind.Auto, "echo hi"));
        Assert.Contains("Bash", ex.Message);
    }

    [Fact]
    public void Resolve_Throws_WhenPowerShellRequestedOnLinux()
    {
        if (OperatingSystem.IsWindows())
            return;

        var ex = Assert.Throws<InvalidOperationException>(() => ShellResolver.Resolve(ShellKind.PowerShell, ShellKind.Auto, "echo hi"));
        Assert.Contains("PowerShell", ex.Message);
    }

    [Fact]
    public void GetAvailableShells_ReturnsPowerShellAndCmd_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var shells = ShellResolver.GetAvailableShells();

        Assert.Contains(shells, s => s.Kind == ShellKind.PowerShell && s.Available);
        Assert.Contains(shells, s => s.Kind == ShellKind.Cmd && s.Available);
    }
}
