using McpTests.Fakes;
using McpToolkit.Shell;
using System.Text.Json.Nodes;

namespace McpTests.Shell;

public class ShellToolsTests
{
    [Fact]
    public async Task execute_shell_command_AcceptsShellNameCaseInsensitive()
    {
        var tools = TestFactories.BuildShellTools();

        var json = await tools.execute_shell_command("echo hello", shell: "AUTO");
        var result = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(0, result["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public async Task execute_shell_command_Throws_WhenShellNameInvalid()
    {
        var tools = TestFactories.BuildShellTools();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.execute_shell_command("echo hello", shell: "powiershell"));

        Assert.Contains("shell", ex.Message);
    }

    [Fact]
    public async Task execute_shell_command_ReturnsExpectedJsonShape()
    {
        var tools = TestFactories.BuildShellTools();

        var json = await tools.execute_shell_command("echo hello");
        var result = JsonNode.Parse(json)!.AsObject();

        Assert.True(result.ContainsKey("exitCode"));
        Assert.True(result.ContainsKey("stdOut"));
        Assert.True(result.ContainsKey("stdErr"));
        Assert.True(result.ContainsKey("timedOut"));
        Assert.True(result.ContainsKey("truncated"));
        Assert.True(result.ContainsKey("durationMs"));
        Assert.True(result.ContainsKey("shell"));
        Assert.True(result.ContainsKey("workingDirectory"));
    }

    [Fact]
    public async Task execute_shell_command_Throws_WhenPasswordMissing_AndConfigured()
    {
        var tools = TestFactories.BuildShellTools(new ShellOptions { Password = "secret" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.execute_shell_command("echo hello"));

        Assert.Contains("Senha", ex.Message);
    }

    [Fact]
    public async Task execute_shell_command_Throws_WhenPasswordWrong()
    {
        var tools = TestFactories.BuildShellTools(new ShellOptions { Password = "secret" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.execute_shell_command("echo hello", password: "wrong"));
    }

    [Fact]
    public async Task execute_shell_command_Succeeds_WhenPasswordCorrect()
    {
        var tools = TestFactories.BuildShellTools(new ShellOptions { Password = "secret" });

        var json = await tools.execute_shell_command("echo hello", password: "secret");
        var result = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(0, result["exitCode"]!.GetValue<int>());
    }

    [Fact]
    public void get_shell_info_ReportsRequiresPasswordFalse_ByDefault()
    {
        var tools = TestFactories.BuildShellTools();

        var json = tools.get_shell_info();
        var result = JsonNode.Parse(json)!.AsObject();

        Assert.False(result["requiresPassword"]!.GetValue<bool>());
    }

    [Fact]
    public void get_shell_info_ReportsRequiresPasswordTrue_WhenConfigured()
    {
        var tools = TestFactories.BuildShellTools(new ShellOptions { Password = "secret" });

        var json = tools.get_shell_info();
        var result = JsonNode.Parse(json)!.AsObject();

        Assert.True(result["requiresPassword"]!.GetValue<bool>());
    }

    [Fact]
    public void get_shell_info_NeverRequiresPassword_ToCall()
    {
        var tools = TestFactories.BuildShellTools(new ShellOptions { Password = "secret" });

        var json = tools.get_shell_info();

        Assert.False(string.IsNullOrWhiteSpace(json));
    }

    [Fact]
    public void get_shell_info_ReturnsExpectedJsonShape()
    {
        var tools = TestFactories.BuildShellTools();

        var json = tools.get_shell_info();
        var result = JsonNode.Parse(json)!.AsObject();

        Assert.True(result.ContainsKey("operatingSystem"));
        Assert.True(result.ContainsKey("defaultShell"));
        Assert.True(result.ContainsKey("availableShells"));
        Assert.True(result.ContainsKey("workingDirectory"));
        Assert.True(result.ContainsKey("timeoutSeconds"));
        Assert.True(result.ContainsKey("maxOutputChars"));
        Assert.True(result.ContainsKey("requiresPassword"));
    }
}
