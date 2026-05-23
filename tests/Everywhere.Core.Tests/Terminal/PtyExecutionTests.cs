using System.Text;
using Everywhere.Terminal;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Porta.Pty;
using Serilog;
using Serilog.Core;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Everywhere.Core.Tests.Terminal;

/// <summary>
/// Integration tests for PTY execution strategies (Rich and None) and real shell command execution.
/// Uses real PTY processes for NoneExecuteStrategy tests and simulated PTY output for RichExecuteStrategy tests.
/// </summary>
[TestFixture]
public class PtyExecutionTests
{
    public enum ShellExecutionScenario
    {
        SingleLine,
        MultiLine,
        EscapedLogicalSingleLine,
    }

    private ILogger _logger = null!;
    private Logger _serilogLogger = null!;

    [OneTimeSetUp]
    public void SetupLogger()
    {
        _serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}][{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSerilog(_serilogLogger);
        });

        _logger = loggerFactory.CreateLogger<PtyExecutionTests>();
    }

    [OneTimeTearDown]
    public void TearDownLogger()
    {
        _serilogLogger.Dispose();
    }

    #region Shell Detection Helpers

    /// <summary>
    /// Detect the default shell for the current platform.
    /// </summary>
    private static (string ShellPath, ShellType Type) DetectPlatformShell()
    {
        if (OperatingSystem.IsWindows())
        {
            // Try pwsh first, fall back to PowerShell
            var pwsh = FindInPath("pwsh.exe") ?? FindInPath("pwsh");
            if (pwsh is not null) return (pwsh, ShellType.PowerShell);

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            return File.Exists(powershell) ? (powershell, ShellType.PowerShell) : ("powershell", ShellType.PowerShell);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ("/bin/zsh", ShellType.Zsh);
        }

        return ("/bin/bash", ShellType.Bash);
    }

    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullPath = Path.Combine(path.Trim(), fileName);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch
            {
                // Ignore
            }
        }
        return null;
    }

    /// <summary>
    /// Build minimal PTY options for testing (without shell integration scripts).
    /// </summary>
    private static PtyOptions BuildTestPtyOptions(string shellPath, ShellType shellType, string? cwd = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NO_COLOR"] = "1",
            ["TERM"] = "xterm-256color",
        };

        // On Windows, ensure PATH is set
        if (OperatingSystem.IsWindows())
        {
            var machinePath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            var processPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
            var combined = string.Join(
                Path.PathSeparator,
                new[] { machinePath, userPath, processPath }.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(combined))
                environment["PATH"] = combined;
        }

        string[] args;
        switch (shellType)
        {
            case ShellType.PowerShell:
                args = ["-NoProfile", "-NoLogo"];
                break;
            case ShellType.Zsh:
                args = [];
                environment["HISTFILE"] = "/dev/null";
                break;
            case ShellType.Bash:
                args = ["--norc", "--noprofile"];
                environment["HISTFILE"] = "/dev/null";
                break;
            default:
                args = [];
                break;
        }

        return new PtyOptions
        {
            Name = "Everywhere-Test",
            Cols = TerminalDimensions.Default.Columns,
            Rows = TerminalDimensions.Default.Rows,
            Cwd = cwd ?? Directory.GetCurrentDirectory(),
            App = shellPath,
            CommandLine = args,
            VerbatimCommandLine = true,
            Environment = environment,
        };
    }

    /// <summary>
    /// Build PTY options that load the real OSC 633 shell integration script.
    /// </summary>
    private static PtyOptions BuildShellIntegrationPtyOptions(string shellPath, ShellType shellType, string? cwd = null)
    {
        var shellArgs = ShellIntegrationScript.BuildShellArgs(shellType);
        if (shellArgs is null)
        {
            Assert.Ignore($"Shell integration script is not available for {shellType}");
        }

        var nonce = Guid.NewGuid().ToString("N")[..16];
        var environment = ShellIntegrationScript.BuildEnvironmentVariables(shellType, nonce);
        var processPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(processPath))
        {
            environment["PATH"] = processPath;
        }

        return new PtyOptions
        {
            Name = "Everywhere-Rich-Test",
            Cols = TerminalDimensions.Default.Columns,
            Rows = TerminalDimensions.Default.Rows,
            Cwd = cwd ?? Directory.GetCurrentDirectory(),
            App = shellPath,
            CommandLine = shellArgs,
            VerbatimCommandLine = true,
            Environment = environment,
        };
    }

    private static IEnumerable<TestCaseData> ShellExecutionScenarios()
    {
        yield return new TestCaseData(ShellExecutionScenario.SingleLine);
        yield return new TestCaseData(ShellExecutionScenario.MultiLine);
        yield return new TestCaseData(ShellExecutionScenario.EscapedLogicalSingleLine);
    }

    private static (string Script, bool IsMultiline, string[] ExpectedOutput) BuildScenarioScript(
        ShellType shellType,
        ShellExecutionScenario scenario,
        string prefix)
    {
        return (shellType, scenario) switch
        {
            (ShellType.PowerShell, ShellExecutionScenario.SingleLine) => (
                $"Write-Host \"{prefix}_SINGLE\"",
                false,
                [$"{prefix}_SINGLE"]),

            (ShellType.PowerShell, ShellExecutionScenario.MultiLine) => (
                $"Write-Host \"{prefix}_MULTI_A\"\nWrite-Host \"{prefix}_MULTI_B\"",
                true,
                [$"{prefix}_MULTI_A", $"{prefix}_MULTI_B"]),

            (ShellType.PowerShell, ShellExecutionScenario.EscapedLogicalSingleLine) => (
                $"Write-Host \"{prefix}_CONT_A\" `\n\"{prefix}_CONT_B\"",
                OutputCleaner.IsMultilineCommand($"Write-Host \"{prefix}_CONT_A\" `\n\"{prefix}_CONT_B\"", shellType),
                [$"{prefix}_CONT_A", $"{prefix}_CONT_B"]),

            (_, ShellExecutionScenario.SingleLine) => (
                $"printf '%s\\n' '{prefix}_SINGLE'",
                false,
                [$"{prefix}_SINGLE"]),

            (_, ShellExecutionScenario.MultiLine) => (
                $"printf '%s\\n' '{prefix}_MULTI_A'\nprintf '%s\\n' '{prefix}_MULTI_B'",
                true,
                [$"{prefix}_MULTI_A", $"{prefix}_MULTI_B"]),

            (_, ShellExecutionScenario.EscapedLogicalSingleLine) => (
                $"printf '%s %s\\n' '{prefix}_CONT_A' \\\n'{prefix}_CONT_B'",
                OutputCleaner.IsMultilineCommand($"printf '%s %s\\n' '{prefix}_CONT_A' \\\n'{prefix}_CONT_B'", shellType),
                [$"{prefix}_CONT_A", $"{prefix}_CONT_B"]),

            _ => throw new ArgumentOutOfRangeException(nameof(scenario), $"Unhandled scenario {scenario} for shell {shellType}")
        };
    }

    private static void AssertContainsExpectedOutput(string output, IReadOnlyList<string> expectedOutput)
    {
        foreach (var expected in expectedOutput)
        {
            Assert.That(
                output,
                Does.Contain(expected),
                $"Expected output to contain '{expected}'. Actual output:\n{output}");
        }
    }

    #endregion

    #region NoneExecuteStrategy — Real PTY Tests

    [Test]
    public async Task NoneStrategy_SingleLineCommand_Echo()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing single-line command on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var script = shellType == ShellType.PowerShell ? "Write-Host \"HELLO_PTY_TEST\"" : "echo \"HELLO_PTY_TEST\"";

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);
        _logger.LogInformation("ExitCode: {ExitCode}", result.ExitCode);

        Assert.That(
            result.Output,
            Does.Contain("HELLO_PTY_TEST"),
            $"Expected output to contain 'HELLO_PTY_TEST'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_MultiLineCommand()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing multi-line command on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        string script;
        if (shellType == ShellType.PowerShell)
        {
            script = "Write-Host \"LINE_A\"\nWrite-Host \"LINE_B\"";
        }
        else
        {
            script = "echo \"LINE_A\"\necho \"LINE_B\"";
        }

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        Assert.That(
            result.Output,
            Does.Contain("LINE_A"),
            $"Expected output to contain 'LINE_A'. Actual output:\n{result.Output}");
        Assert.That(
            result.Output,
            Does.Contain("LINE_B"),
            $"Expected output to contain 'LINE_B'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_EmptyOutputCommand()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing empty output command on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        // Use a command that genuinely produces no output
        // $null works in PowerShell but can be corrupted by stray control chars;
        // [void]0 is more robust as it's a pure expression with no side effects.
        var script = shellType == ShellType.PowerShell ? "[void]0" : "true";

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output length: {Length}, Output: '{Output}'", result.Output.Length, result.Output);

        // Output should be empty or only whitespace
        Assert.That(
            result.Output.Trim(),
            Is.EqualTo(string.Empty).Or.Length.LessThanOrEqualTo(5),
            $"Expected empty or near-empty output. Actual output:\n'{result.Output}'");
    }

    [Test]
    public async Task NoneStrategy_SpecialCharacters()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing special characters on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        // Use a unique marker to identify our output
        var script = shellType == ShellType.PowerShell ? "Write-Host \"SPECIAL_$HOME_TEST\"" : "echo \"SPECIAL_$HOME_TEST\"";

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        // The $HOME part may or may not be expanded depending on quoting, but our marker should be there
        Assert.That(
            result.Output,
            Does.Contain("SPECIAL_"),
            $"Expected output to contain 'SPECIAL_'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_Timeout_GracefulHandling()
    {
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing timeout on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var script = shellType == ShellType.PowerShell ? "Start-Sleep -Seconds 60" : "sleep 60";

        // Use a very short timeout
        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(3),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output after timeout: '{Output}'", result.Output);

        // Should not throw — just return whatever was captured
        Assert.Pass("Timeout handled gracefully");
    }

    [Test]
    public async Task NoneStrategy_MultilineHeredoc_Bash()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Heredoc test is only applicable on Unix shells");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing heredoc on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var script = "cat <<'EOF'\nHEREDOC_LINE_1\nHEREDOC_LINE_2\nEOF";

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        Assert.That(
            result.Output,
            Does.Contain("HEREDOC_LINE_1"),
            $"Expected output to contain 'HEREDOC_LINE_1'. Actual output:\n{result.Output}");
        Assert.That(
            result.Output,
            Does.Contain("HEREDOC_LINE_2"),
            $"Expected output to contain 'HEREDOC_LINE_2'. Actual output:\n{result.Output}");
    }

    [TestCaseSource(nameof(ShellExecutionScenarios))]
    public async Task NoneStrategy_CurrentPlatform_ExecutesScenario(ShellExecutionScenario scenario)
    {
        var (shellPath, shellType) = DetectPlatformShell();
        var (script, isMultiline, expectedOutput) = BuildScenarioScript(shellType, scenario, "NONE_REAL");

        _logger.LogInformation(
            "Testing None scenario {Scenario} on {Shell} ({ShellType}), isMultiline={IsMultiline}",
            scenario,
            shellPath,
            shellType,
            isMultiline);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(session, script, shellType, TimeSpan.FromSeconds(20), CancellationToken.None);

        _logger.LogInformation("[None {Scenario}] Output: {Output}", scenario, result.Output);
        AssertContainsExpectedOutput(result.Output, expectedOutput);
    }

    #endregion

    #region RichExecuteStrategy — Real Shell Integration Tests

    [TestCaseSource(nameof(ShellExecutionScenarios))]
    public async Task RichStrategy_CurrentPlatform_WithShellIntegration_ExecutesScenario(ShellExecutionScenario scenario)
    {
        var (shellPath, shellType) = DetectPlatformShell();
        var (script, isMultiline, expectedOutput) = BuildScenarioScript(shellType, scenario, "RICH_REAL");

        _logger.LogInformation(
            "Testing Rich scenario {Scenario} on {Shell} ({ShellType}), isMultiline={IsMultiline}",
            scenario,
            shellPath,
            shellType,
            isMultiline);

        var options = BuildShellIntegrationPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = await IExecuteStrategy.DetectStrategyAsync(session, shellType, _logger, CancellationToken.None);
        Assert.That(
            strategy,
            Is.TypeOf<RichExecuteStrategy>(),
            $"Expected shell integration to be detected for {shellType}");

        var result = await strategy.ExecuteAsync(session, script, shellType, TimeSpan.FromSeconds(20), CancellationToken.None);

        _logger.LogInformation("[Rich {Scenario}] Output: {Output}", scenario, result.Output);
        AssertContainsExpectedOutput(result.Output, expectedOutput);
    }

    #endregion

    #region RichExecuteStrategy — Simulated PTY Output Tests

    /// <summary>
    /// Create a mock IPtyConnection with a pre-built ReaderStream containing the given content.
    /// The WriterStream is a MemoryStream that captures written data.
    /// </summary>
    private static (TerminalSession session, MemoryStream writerStream) CreateMockPty(byte[] readerContent)
    {
        var readerStream = new MemoryStream(readerContent);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);
        pty.Pid.Returns(12345);
        return (new TerminalSession(pty, TerminalDimensions.Default), writerStream);
    }

    [Test]
    public async Task DetectStrategy_ReusesSessionParserAndBuffer()
    {
        var (session, _) = CreateMockPty(BuildRichSequence("prompt ready"));
        var parser = session.Parser;
        var buffer = session.Buffer;

        var strategy = await IExecuteStrategy.DetectStrategyAsync(
            session,
            ShellType.PowerShell,
            _logger,
            CancellationToken.None);

        Assert.That(strategy, Is.TypeOf<RichExecuteStrategy>());
        Assert.That(session.Parser, Is.SameAs(parser));
        Assert.That(session.Buffer, Is.SameAs(buffer));
        Assert.That(session.Parser.HasDetectedShellIntegration, Is.True);
    }

    [Test]
    public async Task TerminalSession_WritesTerminalQueryResponses_UsingDimensions()
    {
        var readerStream = new MemoryStream();
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);
        var session = new TerminalSession(pty, new TerminalDimensions(77, 33));

        session.Feed("\e[18t\e[6n");
        await session.FlushTerminalResponsesAsync(CancellationToken.None);

        var written = Encoding.ASCII.GetString(writerStream.ToArray());
        Assert.That(written, Does.Contain("\e[8;33;77t"));
        Assert.That(written, Does.Contain("\e[1;1R"));
    }

    /// <summary>
    /// Build a simulated PTY output sequence with Shell Integration markers (OSC 633).
    /// Sequence: A(PromptStart) → B(CommandReady) → E(CommandLine) → C(CommandExecuted) → [output] → D(CommandFinished;exitCode) → A(PromptStart)
    /// </summary>
    private static byte[] BuildRichSequence(string outputText, int exitCode = 0)
    {
        var sb = new StringBuilder();

        // A — PromptStart
        sb.Append("\e]633;A\a");
        // B — CommandReady (prompt is ready, at end of prompt line)
        sb.Append("\e]633;B\a");
        // \r\n — simulate pressing Enter (cursor moves to next line, output starts here)
        sb.Append("\r\n");
        // E — CommandLine
        sb.Append("\e]633;E;mock command\a");
        // C — CommandExecuted
        sb.Append("\e]633;C\a");
        // Command output
        sb.Append(outputText);
        sb.Append("\r\n");
        // D — CommandFinished with exit code
        sb.Append($"\e]633;D;{exitCode}\a");
        // A — PromptStart (next prompt)
        sb.Append("\e]633;A\a");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Test]
    public async Task RichStrategy_SingleLineCommand_ExtractsOutput()
    {
        var expectedOutput = "HELLO_RICH_TEST";
        var ptyData = BuildRichSequence(expectedOutput);
        var (session, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo test",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(
            result.Output,
            Does.Contain(expectedOutput),
            $"Expected output to contain '{expectedOutput}'. Actual output:\n{result.Output}");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RichStrategy_MultiLineCommand_ExtractsOutput()
    {
        var expectedOutput = "LINE_A\r\nLINE_B";
        var ptyData = BuildRichSequence(expectedOutput);
        var (session, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo LINE_A\necho LINE_B",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(
            result.Output,
            Does.Contain("LINE_A"),
            $"Expected output to contain 'LINE_A'. Actual output:\n{result.Output}");
        Assert.That(
            result.Output,
            Does.Contain("LINE_B"),
            $"Expected output to contain 'LINE_B'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task RichStrategy_MultiLineCommand_PreservesTranscriptOrderAcrossTerminalRedraw()
    {
        var sb = new StringBuilder();
        sb.Append("\e]633;A\a");
        sb.Append("PS C:\\test> ");
        sb.Append("\e]633;B\a");

        // Simulate PSReadLine repainting a pasted multi-line command before execution.
        // This mutates the virtual screen but must not enter the captured transcript.
        sb.Append("Write-Host 'FIRST'\r\nWrite-Host 'SECOND'\r\nWrite-Host 'THIRD'");
        sb.Append("\e[2A\e[2KWrite-Host 'THIRD'\r\n\e[2KWrite-Host 'SECOND'\r\n\e[2KWrite-Host 'FIRST'\r\n");

        sb.Append("\e]633;E;mock command\a");
        sb.Append("\e]633;C\a");
        sb.Append("FIRST\r\nSECOND\r\nTHIRD\r\n");
        sb.Append("\e]633;D;0\a");
        sb.Append("\e]633;A\a");

        var (session, _) = CreateMockPty(Encoding.UTF8.GetBytes(sb.ToString()));
        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "Write-Host 'FIRST'\nWrite-Host 'SECOND'\nWrite-Host 'THIRD'",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich redraw output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.Output.Replace("\r\n", "\n"), Is.EqualTo("FIRST\nSECOND\nTHIRD"));
    }

    [Test]
    public async Task RichStrategy_MultipleCommandExecutedMarkers_SingleFinishMarker_ExtractsChronologicalOutput()
    {
        var sb = new StringBuilder();
        sb.Append("\e]633;A\a");
        sb.Append("\e]633;B\a");
        sb.Append("\r\n");
        sb.Append("\e]633;E;first command\a");
        sb.Append("\e]633;C\a");
        sb.Append("FIRST\r\n");
        sb.Append("\e]633;E;second command\a");
        sb.Append("\e]633;C\a");
        sb.Append("SECOND\r\n");
        sb.Append("\e]633;D;0\a");
        sb.Append("\e]633;A\a");

        var (session, _) = CreateMockPty(Encoding.UTF8.GetBytes(sb.ToString()));
        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo FIRST\necho SECOND",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich multi-C output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.Output.Replace("\r\n", "\n"), Is.EqualTo("FIRST\nSECOND"));
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RichStrategy_NonZeroExitCode()
    {
        var ptyData = BuildRichSequence("error output", exitCode: 42);
        var (session, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "exit 42",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.ExitCode, Is.EqualTo(42));
    }

    [Test]
    public async Task RichStrategy_EmptyOutput()
    {
        var ptyData = BuildRichSequence("", exitCode: 0);
        var (session, _) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "true",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich output: '{Output}', ExitCode: {ExitCode}", result.Output, result.ExitCode);

        Assert.That(result.ExitCode, Is.EqualTo(0));
        // Output should be empty or near-empty
        Assert.That(result.Output.Trim(), Is.EqualTo(string.Empty).Or.Length.LessThanOrEqualTo(5));
    }

    [Test]
    public async Task RichStrategy_FallbackOutput_UsesCommandStartBaseline()
    {
        var ptyData = Encoding.UTF8.GetBytes("\r\nWrite-Host 'RESULT'\r\nRESULT\r\nPS C:\\test> ");
        var (session, _) = CreateMockPty(ptyData);
        session.Feed("PS C:\\detect> ");

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "Write-Host 'RESULT'",
            ShellType.PowerShell,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        Assert.That(result.Output, Does.Contain("RESULT"));
        Assert.That(result.Output, Does.Not.Contain("detect"));
    }

    [Test]
    public async Task RichStrategy_SendsCommandCorrectly_SingleLine()
    {
        var ptyData = BuildRichSequence("ok");
        var (session, writerStream) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        await strategy.ExecuteAsync(
            session,
            script: "echo hello",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        // Single line: should contain "echo hello\r"
        Assert.That(written, Does.Contain("echo hello"));
        Assert.That(written, Does.Contain("\r"));
        // Should NOT contain bracketed paste markers
        Assert.That(written, Does.Not.Contain("\e[200~"));
    }

    [Test]
    public async Task RichStrategy_SendsLogicalSingleLineNewlinesAsEnterKeys()
    {
        var ptyData = BuildRichSequence("ok");
        var (session, writerStream) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        await strategy.ExecuteAsync(
            session,
            script: "echo a \\\necho b",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written logical single-line to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        Assert.That(written, Does.Contain("echo a \\\recho b\r"));
        Assert.That(written, Does.Not.Contain("\e[200~"));
    }

    [Test]
    public async Task RichStrategy_SendsCommandCorrectly_MultiLine()
    {
        var ptyData = BuildRichSequence("ok");
        var (session, writerStream) = CreateMockPty(ptyData);
        session.Parser.Feed("\e[?2004h");

        var strategy = new RichExecuteStrategy(_logger);
        await strategy.ExecuteAsync(
            session,
            script: "echo a\necho b",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        // Multi-line: should use bracketed paste
        Assert.That(written, Does.Contain("\e[200~"), "Should start bracketed paste");
        Assert.That(written, Does.Contain("\e[201~"), "Should end bracketed paste");
        Assert.That(written, Does.Contain("echo a"));
        Assert.That(written, Does.Contain("echo b"));
    }

    [Test]
    public async Task RichStrategy_MultiLine_WithoutBracketedPasteMode_SendsLineByLine()
    {
        var ptyData = BuildRichSequence("ok");
        var (session, writerStream) = CreateMockPty(ptyData);

        var strategy = new RichExecuteStrategy(_logger);
        await strategy.ExecuteAsync(
            session,
            script: "echo a\necho b",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written without bracketed paste: {Escaped}", OutputCleaner.EscapeForLog(written));

        Assert.That(written, Is.EqualTo("echo a\recho b\r"));
        Assert.That(written, Does.Not.Contain("\e[200~"));
        Assert.That(written, Does.Not.Contain("\e[201~"));
    }

    [Test]
    public async Task RichStrategy_BracketedPaste_SuppressesExecution()
    {
        // Verify that \r inside bracketed paste does NOT trigger execution.
        // In real shell, bracketed paste wraps the entire multi-line script so the shell
        // treats it as a single paste block, not individual commands.
        var script = "Write-Host 'LINE_A'\rWrite-Host 'LINE_B'";
        var ptyData = BuildRichSequence("LINE_A\r\nLINE_B");
        var (session, writerStream) = CreateMockPty(ptyData);
        session.Parser.Feed("\e[?2004h");

        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        // Verify bracketed paste markers are present
        Assert.That(written, Does.Contain("\e[200~"), "Should start bracketed paste");
        Assert.That(written, Does.Contain("\e[201~"), "Should end bracketed paste");
        // Verify output contains both lines
        Assert.That(
            result.Output,
            Does.Contain("LINE_A"),
            $"Expected output to contain 'LINE_A'. Actual output:\n{result.Output}");
        Assert.That(
            result.Output,
            Does.Contain("LINE_B"),
            $"Expected output to contain 'LINE_B'. Actual output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_SendsLogicalSingleLineNewlinesAsEnterKeys()
    {
        var (session, writerStream) = CreateMockPty([]);
        await NoneExecuteStrategy.SendCommandAsync(session, "echo a \\\necho b", ShellType.Unknown, CancellationToken.None);

        var written = Encoding.UTF8.GetString(writerStream.ToArray());
        _logger.LogInformation("Written logical single-line to PTY: {Escaped}", OutputCleaner.EscapeForLog(written));

        Assert.That(written, Is.EqualTo("echo a \\\recho b\r"));
    }

    [Test]
    public async Task NoneStrategy_MultiLine_SendsLineByLine()
    {
        // Verify that None strategy sends multi-line commands line by line
        // (not as bracketed paste), and correctly collects output from each line.
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Testing line-by-line sending on {Shell} ({ShellType})", shellPath, shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        string script;
        if (shellType == ShellType.PowerShell)
        {
            script = "Write-Host 'SPLIT_A'\nWrite-Host 'SPLIT_B'";
        }
        else
        {
            script = "echo 'SPLIT_A'\necho 'SPLIT_B'";
        }

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Output: {Output}", result.Output);

        Assert.That(
            result.Output,
            Does.Contain("SPLIT_A"),
            $"Expected output to contain 'SPLIT_A'. Actual output:\n{result.Output}");
        Assert.That(
            result.Output,
            Does.Contain("SPLIT_B"),
            $"Expected output to contain 'SPLIT_B'. Actual output:\n{result.Output}");
    }

    #endregion

    #region IExecuteStrategy.DetectStrategyAsync

    [Test]
    public async Task DetectStrategy_WithShellIntegration_ReturnsRich()
    {
        // Build output that contains shell integration markers
        var output = BuildRichSequence("prompt ready");
        var readerStream = new MemoryStream(output);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);
        var session = new TerminalSession(pty, TerminalDimensions.Default);

        var strategy = await IExecuteStrategy.DetectStrategyAsync(session, ShellType.PowerShell, _logger, CancellationToken.None);

        Assert.That(
            strategy,
            Is.TypeOf<RichExecuteStrategy>(),
            "Should detect Shell Integration and return RichExecuteStrategy");
    }

    [Test]
    public async Task DetectStrategy_WithBracketedPasteMode_PassesModeToRichStrategy()
    {
        var output = Encoding.UTF8.GetBytes("\e[?2004h" + Encoding.UTF8.GetString(BuildRichSequence("prompt ready")));
        var readerStream = new MemoryStream(output);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);
        var session = new TerminalSession(pty, TerminalDimensions.Default);

        var strategy = await IExecuteStrategy.DetectStrategyAsync(session, ShellType.PowerShell, _logger, CancellationToken.None);

        Assert.That(strategy, Is.TypeOf<RichExecuteStrategy>());
        Assert.That(session.Parser.IsBracketedPasteModeEnabled, Is.True);
    }

    [Test]
    public async Task DetectStrategy_WithoutShellIntegration_ReturnsNone()
    {
        // Build output without any shell integration markers — just a plain prompt
        var output = "user@host:~$ "u8.ToArray();
        var readerStream = new MemoryStream(output);
        var writerStream = new MemoryStream();
        var pty = Substitute.For<IPtyConnection>();
        pty.ReaderStream.Returns(readerStream);
        pty.WriterStream.Returns(writerStream);
        var session = new TerminalSession(pty, TerminalDimensions.Default);

        var strategy = await IExecuteStrategy.DetectStrategyAsync(session, ShellType.Bash, _logger, CancellationToken.None);

        Assert.That(
            strategy,
            Is.TypeOf<NoneExecuteStrategy>(),
            "Should not detect Shell Integration and return NoneExecuteStrategy");
    }

    #endregion

    #region Cross-Platform Shell Execution Matrix

    [Test]
    public async Task Matrix_PowerShell_SingleLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("PowerShell test only runs on Windows");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation("Detected shell: {ShellPath} (type: {ShellType})", shellPath, shellType);
        Assert.That(shellType, Is.EqualTo(ShellType.PowerShell));

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "Write-Host \"MATRIX_PS_TEST\"",
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[PowerShell] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("MATRIX_PS_TEST"));
    }

    [Test]
    public async Task Matrix_PowerShell_MultiLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("PowerShell test only runs on Windows");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "Write-Host \"PS_LINE1\"\nWrite-Host \"PS_LINE2\"",
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[PowerShell MultiLine] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("PS_LINE1"));
        Assert.That(result.Output, Does.Contain("PS_LINE2"));
    }

    [Test]
    public async Task Matrix_Bash_SingleLine()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Bash test only runs on Unix");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo \"MATRIX_BASH_TEST\"",
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[Bash] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("MATRIX_BASH_TEST"));
    }

    [Test]
    public async Task Matrix_Bash_MultiLine()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Bash test only runs on Unix");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo \"BASH_L1\"\necho \"BASH_L2\"",
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[Bash MultiLine] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("BASH_L1"));
        Assert.That(result.Output, Does.Contain("BASH_L2"));
    }

    [Test]
    public async Task Matrix_Zsh_SingleLine()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Zsh test only runs on macOS");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        Assert.That(shellType, Is.EqualTo(ShellType.Zsh));

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo \"MATRIX_ZSH_TEST\"",
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[Zsh] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("MATRIX_ZSH_TEST"));
    }

    [Test]
    public async Task Matrix_Zsh_MultiLine()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("Zsh test only runs on macOS");
            return;
        }

        var (shellPath, shellType) = DetectPlatformShell();
        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "echo \"ZSH_L1\"\necho \"ZSH_L2\"",
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[Zsh MultiLine] Output: {Output}", result.Output);
        Assert.That(result.Output, Does.Contain("ZSH_L1"));
        Assert.That(result.Output, Does.Contain("ZSH_L2"));
    }

    #endregion

    #region Output Order Regression Tests (regression: multi-line output reversal)

    [Test]
    public async Task RichStrategy_MultiLine_Simulated_OutputPreservesOrder()
    {
        // Regression: multi-line output must preserve line order (1,2,3, not 3,2,1).
        var sb = new StringBuilder();
        sb.Append("\e]633;A\a");
        sb.Append("\e]633;B\a");
        sb.Append("\r\n");
        sb.Append("\e]633;E;seq 1 5\a");
        sb.Append("\e]633;C\a");
        // Output lines MUST be captured in the order they arrive
        sb.Append("1\r\n2\r\n3\r\n4\r\n5\r\n");
        sb.Append("\e]633;D;0\a");
        sb.Append("\e]633;A\a");

        var (session, _) = CreateMockPty(Encoding.UTF8.GetBytes(sb.ToString()));
        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "seq 1 5",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich order regression output: '{Output}'", result.Output);

        var normalized = result.Output.Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.That(
            normalized,
            Does.Contain("1"),
            "Output must contain the first line");
        Assert.That(
            normalized,
            Does.Contain("5"),
            "Output must contain the last line");

        // Verify 1 appears before 2, 2 before 3, etc.
        var idx1 = normalized.IndexOf('1', StringComparison.Ordinal);
        var idx2 = normalized.IndexOf('2', StringComparison.Ordinal);
        var idx3 = normalized.IndexOf('3', StringComparison.Ordinal);
        var idx4 = normalized.IndexOf('4', StringComparison.Ordinal);
        var idx5 = normalized.IndexOf('5', StringComparison.Ordinal);

        Assert.That(idx1, Is.LessThan(idx2), "'1' must appear before '2'");
        Assert.That(idx2, Is.LessThan(idx3), "'2' must appear before '3'");
        Assert.That(idx3, Is.LessThan(idx4), "'3' must appear before '4'");
        Assert.That(idx4, Is.LessThan(idx5), "'4' must appear before '5'");
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RichStrategy_MultiLine_MultiC_Simulated_OutputPreservesOrder()
    {
        // Regression: multiple C (CommandExecuted) markers from multi-line paste
        // must not cause output reversal.
        var sb = new StringBuilder();
        sb.Append("\e]633;A\a");
        sb.Append("\e]633;B\a");
        sb.Append("\r\n");
        // First command
        sb.Append("\e]633;E;Write-Host 'A'\a");
        sb.Append("\e]633;C\a");
        sb.Append("A\r\n");
        // Second command
        sb.Append("\e]633;E;Write-Host 'B'\a");
        sb.Append("\e]633;C\a");
        sb.Append("B\r\n");
        // Third command
        sb.Append("\e]633;E;Write-Host 'C'\a");
        sb.Append("\e]633;C\a");
        sb.Append("C\r\n");
        sb.Append("\e]633;D;0\a");
        sb.Append("\e]633;A\a");

        var (session, _) = CreateMockPty(Encoding.UTF8.GetBytes(sb.ToString()));
        var strategy = new RichExecuteStrategy(_logger);
        var result = await strategy.ExecuteAsync(
            session,
            script: "Write-Host 'A'\nWrite-Host 'B'\nWrite-Host 'C'",
            ShellType.Unknown,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Rich multi-C order output: '{Output}'", result.Output);

        var normalized = result.Output.Replace("\r\n", "\n").Replace("\r", "\n");
        var idxA = normalized.IndexOf('A', StringComparison.Ordinal);
        var idxB = normalized.IndexOf('B', StringComparison.Ordinal);
        var idxC = normalized.IndexOf('C', StringComparison.Ordinal);

        Assert.That(idxA, Is.GreaterThanOrEqualTo(0), "Must contain 'A'");
        Assert.That(idxB, Is.GreaterThanOrEqualTo(0), "Must contain 'B'");
        Assert.That(idxC, Is.GreaterThanOrEqualTo(0), "Must contain 'C'");
        Assert.That(idxA, Is.LessThan(idxB), "'A' must appear before 'B' (multi-C marker order)");
        Assert.That(idxB, Is.LessThan(idxC), "'B' must appear before 'C' (multi-C marker order)");
    }

    [Test]
    public async Task NoneStrategy_MultiLine_OutputPreservesOrder()
    {
        // Integration test: verify that multi-line output from a real shell
        // preserves chronological order.
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation(
            "Testing multi-line output order on {Shell} ({ShellType})",
            shellPath,
            shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        string script;
        if (shellType == ShellType.PowerShell)
        {
            // Generate ordered output: 1, 2, 3, 4, 5
            script = "1..5 | ForEach-Object { Write-Host $_ }";
        }
        else
        {
            script = "seq 1 5";
        }

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(15),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[None Order] Output: {Output}", result.Output);

        var normalized = result.Output.Replace("\r\n", "\n").Replace("\r", "\n");

        // Verify all numbers present
        Assert.That(normalized, Does.Contain("1"), "Must contain '1'");
        Assert.That(normalized, Does.Contain("2"), "Must contain '2'");
        Assert.That(normalized, Does.Contain("3"), "Must contain '3'");
        Assert.That(normalized, Does.Contain("4"), "Must contain '4'");
        Assert.That(normalized, Does.Contain("5"), "Must contain '5'");

        // Verify order: 1 before 2, 2 before 3, etc.
        var idx1 = normalized.IndexOf('1', StringComparison.Ordinal);
        var idx2 = normalized.IndexOf('2', StringComparison.Ordinal);
        var idx3 = normalized.IndexOf('3', StringComparison.Ordinal);
        var idx4 = normalized.IndexOf('4', StringComparison.Ordinal);
        var idx5 = normalized.IndexOf('5', StringComparison.Ordinal);

        Assert.That(
            idx1,
            Is.LessThan(idx2),
            $"Output order violated: '1' (at {idx1}) should be before '2' (at {idx2}). Output:\n{result.Output}");
        Assert.That(
            idx2,
            Is.LessThan(idx3),
            $"Output order violated: '2' (at {idx2}) should be before '3' (at {idx3}). Output:\n{result.Output}");
        Assert.That(
            idx3,
            Is.LessThan(idx4),
            $"Output order violated: '3' (at {idx3}) should be before '4' (at {idx4}). Output:\n{result.Output}");
        Assert.That(
            idx4,
            Is.LessThan(idx5),
            $"Output order violated: '4' (at {idx4}) should be before '5' (at {idx5}). Output:\n{result.Output}");
    }

    [Test]
    public async Task NoneStrategy_MultiLineCommand_OutputPreservesOrder()
    {
        // Regression: when sending multi-line commands (line by line via NoneStrategy),
        // the output from each line must arrive in chronological order.
        var (shellPath, shellType) = DetectPlatformShell();
        _logger.LogInformation(
            "Testing multi-line command output order on {Shell} ({ShellType})",
            shellPath,
            shellType);

        var options = BuildTestPtyOptions(shellPath, shellType);
        using var pty = await PtyProvider.SpawnAsync(options, CancellationToken.None);
        var session = TerminalSession.FromPtyOptions(pty, options);

        var strategy = new NoneExecuteStrategy(_logger);
        string script;
        if (shellType == ShellType.PowerShell)
        {
            script = "Write-Host 'FIRST_OUT'\nWrite-Host 'SECOND_OUT'\nWrite-Host 'THIRD_OUT'";
        }
        else
        {
            script = "echo 'FIRST_OUT'\necho 'SECOND_OUT'\necho 'THIRD_OUT'";
        }

        var result = await strategy.ExecuteAsync(
            session,
            script: script,
            shellType,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("[None MultiCmd Order] Output: {Output}", result.Output);

        var normalized = result.Output.Replace("\r\n", "\n").Replace("\r", "\n");

        var idxFirst = normalized.IndexOf("FIRST_OUT", StringComparison.Ordinal);
        var idxSecond = normalized.IndexOf("SECOND_OUT", StringComparison.Ordinal);
        var idxThird = normalized.IndexOf("THIRD_OUT", StringComparison.Ordinal);

        Assert.That(idxFirst, Is.GreaterThanOrEqualTo(0), "Must contain FIRST_OUT");
        Assert.That(idxSecond, Is.GreaterThanOrEqualTo(0), "Must contain SECOND_OUT");
        Assert.That(idxThird, Is.GreaterThanOrEqualTo(0), "Must contain THIRD_OUT");
        Assert.That(
            idxFirst,
            Is.LessThan(idxSecond),
            $"FIRST_OUT (at {idxFirst}) must appear before SECOND_OUT (at {idxSecond})");
        Assert.That(
            idxSecond,
            Is.LessThan(idxThird),
            $"SECOND_OUT (at {idxSecond}) must appear before THIRD_OUT (at {idxThird})");
    }

    #endregion

}
