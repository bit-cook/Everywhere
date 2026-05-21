using System.Text;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration (OSC 633) markers are detected.
/// Captures text in parser order between C (CommandExecuted) and D (CommandFinished)
/// markers so terminal redraws cannot reorder multi-line command output.
/// Falls back to idle detection if markers are incomplete (e.g., command crashes).
/// </summary>
internal sealed class RichExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    public async Task<ExecuteResult> ExecuteAsync(
        IPtyConnection pty,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readBuffer = new byte[4096];
        var textDecoder = new PtyTextDecoder(readBuffer.Length);
        var vtBuffer = new VirtualTerminalBuffer(1024);

        // Track Shell Integration markers
        int? exitCode = null;
        var transcript = new StringBuilder();
        var commandCount = 0;
        var isCapturingTranscript = false;
        var hasCommandFinished = false;
        var hasFinalPrompt = false;
        var promptStartLine = -1; // Final A marker line (after all commands)

        var parser = new VtSequenceParser(
            vtBuffer,
            (in marker) =>
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
            },
            value =>
            {
                if (isCapturingTranscript)
                {
                    transcript.Append(value);
                }
            });

        // Send the command
        await SendCommandAsync(pty, script, shellType, cancellationToken);

        // Read output until we get the D+A sequence or timeout.
        // Uses Task.WhenAny pattern for efficient async polling.
        const int pollIntervalMs = 150;
        var consecutiveIdlePolls = 0;
        const int minIdlePolls = 4; // 4 × 150ms = 600ms idle
        const int maxFallbackIdlePolls = 14; // ~2.1s fallback

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        Task<int>? pendingReadTask = null;

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

                pendingReadTask ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var pollDelayTask = Task.Delay(pollIntervalMs, linkedToken);
                var completedTask = await Task.WhenAny(pendingReadTask, pollDelayTask);

                if (completedTask == pendingReadTask)
                {
                    // Received new data
                    var bytesRead = await pendingReadTask;
                    pendingReadTask = null;

                    if (bytesRead == 0)
                    {
                        parser.Feed(textDecoder.Flush());
                        break; // Stream closed
                    }

                    var text = textDecoder.Decode(readBuffer.AsSpan(0, bytesRead));
                    parser.Feed(text);

                    if (text.IndexOfAny('\n', '\r') >= 0)
                    {
                        logger.LogDebug("[PTY RAW] {EscapeForLog}", OutputCleaner.EscapeForLog(new string(text)));
                    }

                    consecutiveIdlePolls = 0;
                }
                else
                {
                    // Idle — no data within pollIntervalMs
                    if (commandCount == 0) continue;

                    consecutiveIdlePolls++;
                    if (consecutiveIdlePolls < minIdlePolls) continue;

                    // Check if the last line looks like a shell prompt
                    var lastLine = vtBuffer.GetLastLine();
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
            try { pty.Kill(); }
            catch
            { /* ignore */
            }
        }

        // Extract output from the chronological transcript when C/D markers bracketed it.
        string output;
        if (commandCount > 0 && hasCommandFinished)
        {
            output = transcript.ToString();
            logger.LogDebug(
                "[Rich] Transcript extraction: {CmdCount} C marker(s), total length={Length}",
                commandCount,
                output.Length);
        }
        else
        {
            // Fallback: use full buffer text with heuristic cleaning
            output = vtBuffer.GetText();
            logger.LogDebug("[Rich] Fallback to full buffer text, length={Length}", output.Length);
        }

        return new ExecuteResult(output, exitCode);
    }

    /// <summary>
    /// Send command using bracketed paste mode for multi-line commands.
    /// Requires Shell Integration + PSReadLine to correctly handle bracketed paste.
    /// </summary>
    private async static Task SendCommandAsync(
        IPtyConnection pty,
        string script,
        ShellType shellType,
        CancellationToken cancellationToken)
    {
        var isMultiline = OutputCleaner.IsMultilineCommand(script, shellType);
        if (isMultiline)
        {
            var normalizedScript = script.Replace("\r\n", "\n").Replace("\r", "\n");
            var scriptBytes = Encoding.UTF8.GetBytes(normalizedScript);

            // Bracketed paste: wrap entire script in \e[200~ ... \e[201~\r
            await pty.WriterStream.WriteAsync("\e[200~"u8.ToArray(), cancellationToken);
            await pty.WriterStream.WriteAsync(scriptBytes, cancellationToken);
            await pty.WriterStream.WriteAsync("\e[201~\r"u8.ToArray(), cancellationToken);
            await pty.WriterStream.FlushAsync(cancellationToken);
        }
        else
        {
            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                var lineBytes = Encoding.UTF8.GetBytes(trimmed);
                await pty.WriterStream.WriteAsync(lineBytes, cancellationToken);
                await pty.WriterStream.WriteAsync("\r"u8.ToArray(), cancellationToken);
                await pty.WriterStream.FlushAsync(cancellationToken);
            }
        }
    }
}
