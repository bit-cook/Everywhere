using System.Text;
using Microsoft.Extensions.Logging;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration (OSC 633) markers are detected.
/// Captures text in parser order between C (CommandExecuted) and D (CommandFinished)
/// markers so terminal redraws cannot reorder multi-line command output.
/// Falls back to idle detection if markers are incomplete (e.g., command crashes).
/// </summary>
public sealed class RichExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    public async Task<ExecuteResult> ExecuteAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Track Shell Integration markers
        int? exitCode = null;
        var transcript = new StringBuilder();
        var commandCount = 0;
        var isCapturingTranscript = false;
        var hasCommandFinished = false;
        var hasFinalPrompt = false;
        var promptStartLine = -1; // Final A marker line (after all commands)
        var commandStartLine = session.Buffer.CursorY;

        void HandleMarker(in ShellIntegrationMarker marker)
        {
            switch (marker.Type)
            {
                case ShellIntegrationMarkerType.CommandReady:
                    logger.LogDebug("[Rich] B (CommandReady) at line {Line}", marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandExecuted:
                    commandCount++;
                    if (!isCapturingTranscript)
                    {
                        hasCommandFinished = false;
                        hasFinalPrompt = false;
                        isCapturingTranscript = true;
                    }
                    logger.LogDebug("[Rich] C (CommandExecuted) # {Count} at line {Line}", commandCount, marker.Line);
                    break;
                case ShellIntegrationMarkerType.CommandFinished:
                    if (commandCount > 0)
                    {
                        hasCommandFinished = true;
                        isCapturingTranscript = false;
                        exitCode = marker.ExitCode;
                    }
                    logger.LogDebug(
                        "[Rich] D (CommandFinished) exitCode={ExitCode} at line {Line}",
                        marker.ExitCode,
                        marker.Line);
                    break;
                case ShellIntegrationMarkerType.PromptStart:
                    // A after commands have started means a prompt has arrived
                    if (commandCount > 0 && hasCommandFinished)
                    {
                        hasFinalPrompt = true;
                        promptStartLine = marker.Line;
                        logger.LogDebug("[Rich] A (PromptStart) at line {Line}", marker.Line);
                    }
                    break;
            }
        }

        void HandleText(char value)
        {
            if (isCapturingTranscript)
            {
                transcript.Append(value);
            }
        }

        session.Parser.ShellIntegrationMarkerReceived += HandleMarker;
        session.Parser.TerminalTextReceived += HandleText;

        try
        {
            // Send the command
            await SendCommandAsync(
                session,
                script,
                shellType,
                logger,
                cancellationToken);

            // Read output until we get the D+A sequence or timeout.
            // Uses Task.WhenAny pattern for efficient async polling.
            const int pollIntervalMs = 150;
            var consecutiveIdlePolls = 0;
            const int minIdlePolls = 4; // 4 x 150ms = 600ms idle
            const int maxFallbackIdlePolls = 14; // ~2.1s fallback

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    // Check if command output has ended and the final prompt has arrived
                    if (commandCount > 0 && hasCommandFinished && hasFinalPrompt)
                    {
                        logger.LogDebug(
                            "[Rich] Command output finished after {Count} C marker(s), final prompt at line {Line}",
                            commandCount,
                            promptStartLine);
                        break;
                    }

                    var readTask = session.BeginReadAsync(linkedToken);
                    var pollDelayTask = Task.Delay(pollIntervalMs, linkedToken);
                    var completedTask = await Task.WhenAny(readTask, pollDelayTask);

                    if (completedTask == readTask)
                    {
                        // Received new data
                        var bytesRead = await session.CompleteReadAsync(cancellationToken);

                        if (bytesRead == 0)
                        {
                            break; // Stream closed
                        }

                        consecutiveIdlePolls = 0;
                    }
                    else
                    {
                        // Idle - no data within pollIntervalMs
                        if (commandCount == 0) continue;

                        consecutiveIdlePolls++;
                        if (consecutiveIdlePolls < minIdlePolls) continue;

                        // Check if the last line looks like a shell prompt
                        var lastLine = session.Buffer.GetLastLine();
                        if (OutputCleaner.IsShellPrompt(lastLine))
                        {
                            logger.LogDebug("[Rich] Idle detected with prompt, last line: {LastLine}", lastLine);
                            break;
                        }

                        // Fallback: force exit if terminal has been silent for ~2s
                        if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                        {
                            logger.LogWarning("[Rich] Maximum idle threshold reached without strict prompt match. Assuming complete.");
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                logger.LogWarning("[Rich] Read timed out");
                try { session.Pty.Kill(); }
                catch
                {
                    // ignore
                }
            }

            // Extract output from the chronological transcript when C/D markers bracketed it.
            string output;
            if (commandCount > 0 && hasCommandFinished)
            {
                output = TrimTrailingLineBreaks(transcript.ToString());
                logger.LogDebug(
                    "[Rich] Transcript extraction: {CmdCount} C marker(s), total length={Length}",
                    commandCount,
                    output.Length);
            }
            else
            {
                // Fallback: use buffer text since command start, then clean the command echo/prompt.
                var rawOutput = session.GetTextFromLine(commandStartLine);
                output = OutputCleaner.StripCommandEchoAndPrompt(rawOutput, script);
                logger.LogDebug(
                    "[Rich] Fallback to buffer text from line {StartLine}, raw length={Length}, cleaned length={CleanedLength}",
                    commandStartLine,
                    rawOutput.Length,
                    output.Length);
            }

            return new ExecuteResult(output, exitCode);
        }
        finally
        {
            session.Parser.ShellIntegrationMarkerReceived -= HandleMarker;
            session.Parser.TerminalTextReceived -= HandleText;
        }
    }

    /// <summary>
    /// Send command using bracketed paste mode for multi-line commands when the
    /// terminal has enabled it dynamically.
    /// </summary>
    private async static Task SendCommandAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var isMultiline = OutputCleaner.IsMultilineCommand(script, shellType);
        if (isMultiline)
        {
            if (!session.Parser.IsBracketedPasteModeEnabled)
            {
                logger.LogWarning(
                    "[Rich] Bracketed paste mode is disabled in parser state; sending multi-line command as Enter-separated lines (shellType={ShellType})",
                    shellType);

                await SendLineByLineAsync(session, script, cancellationToken);
                return;
            }

            logger.LogDebug(
                "[Rich] Sending multi-line command using bracketed paste (shellType={ShellType}, dimensions={Dimensions})",
                shellType,
                session.Dimensions);

            var normalizedScript = script.Replace("\r\n", "\n").Replace("\r", "\n");
            await session.WriteInputAsync($"\e[200~{normalizedScript}\e[201~\r", cancellationToken);
        }
        else
        {
            logger.LogDebug("[Rich] Sending single-line command (shellType={ShellType})", shellType);

            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                await session.WriteInputAsync($"{NormalizeCommandNewlines(trimmed)}\r", cancellationToken);
            }
        }
    }

    private async static Task SendLineByLineAsync(
        TerminalSession session,
        string script,
        CancellationToken cancellationToken)
    {
        var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            await session.WriteInputAsync($"{trimmed}\r", cancellationToken);
            await Task.Delay(100, cancellationToken);
        }
    }

    private static string NormalizeCommandNewlines(string script)
    {
        return script.Replace("\r\n", "\r").Replace("\n", "\r");
    }

    private static string TrimTrailingLineBreaks(string output)
    {
        return output.TrimEnd('\r', '\n');
    }
}
