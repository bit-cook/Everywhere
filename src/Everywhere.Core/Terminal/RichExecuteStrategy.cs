using System.Text;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration (OSC 633) markers are detected.
/// Tracks every C (CommandExecuted) marker to build multi-segment output ranges,
/// correctly handling both atomic multi-line paste execution and burst multi-command
/// execution. Falls back to idle detection if markers are incomplete (e.g., command crashes).
/// </summary>
internal sealed class RichExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    public async Task<ExecuteResult> ExecuteAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readBuffer = new byte[4096];
        var vtBuffer = new VirtualTerminalBuffer(1024);

        // Track Shell Integration markers
        int? exitCode = null;
        var commandStartLines = new List<int>(); // C marker line for each command
        var commandCount = 0;
        var finishedCount = 0;
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
                        commandStartLines.Add(marker.Line);
                        logger.LogDebug("[Rich] C (CommandExecuted) # {Count} at line {Line}", commandCount, marker.Line);
                        break;
                    case ShellIntegrationMarkerType.CommandFinished:
                        finishedCount++;
                        exitCode = marker.ExitCode;
                        logger.LogDebug(
                            "[Rich] D (CommandFinished) # {Count} exitCode={ExitCode} at line {Line}",
                            finishedCount,
                            marker.ExitCode,
                            marker.Line);
                        break;
                    case ShellIntegrationMarkerType.PromptStart:
                        // A after commands have started means a prompt has arrived
                        if (commandCount > 0)
                        {
                            promptStartLine = marker.Line;
                            logger.LogDebug("[Rich] A (PromptStart) at line {Line}", marker.Line);
                        }
                        break;
                }
            });

        // Send the command
        await SendCommandAsync(pty, script, isMultiline, cancellationToken);

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
                // Check if all commands are finished and the final prompt has arrived
                if (commandCount > 0 && finishedCount >= commandCount && promptStartLine >= 0)
                {
                    logger.LogDebug("[Rich] All {Count} command(s) finished, final prompt at line {Line}", commandCount, promptStartLine);
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

                    if (bytesRead == 0) break; // Stream closed

                    var text = Encoding.UTF8.GetString(readBuffer, 0, bytesRead);
                    parser.Feed(text);

                    if (text.Contains('\n') || text.Contains('\r'))
                    {
                        logger.LogDebug("[PTY RAW] {EscapeForLog}", OutputCleaner.EscapeForLog(text));
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

        // Extract output — multi-segment for multi-command bursts
        string output;
        if (commandStartLines.Count > 0 && promptStartLine >= 0)
        {
            var segments = new List<string>(commandStartLines.Count);

            // Segments between consecutive C markers
            for (var i = 0; i < commandStartLines.Count - 1; i++)
            {
                var seg = vtBuffer.GetTextBetween(
                    commandStartLines[i],
                    commandStartLines[i + 1] - 1);
                if (!string.IsNullOrEmpty(seg))
                    segments.Add(seg);
            }

            // Final segment: last C to final A
            var lastSeg = vtBuffer.GetTextBetween(
                commandStartLines[^1],
                promptStartLine - 1);
            if (!string.IsNullOrEmpty(lastSeg))
                segments.Add(lastSeg);

            output = string.Join('\n', segments);
            logger.LogDebug(
                "[Rich] Multi-segment extraction: {CmdCount} command(s), {SegCount} non-empty segment(s), total length={Length}",
                commandStartLines.Count,
                segments.Count,
                output.Length);
        }
        else
        {
            // Fallback: use full buffer text with heuristic cleaning
            output = vtBuffer.GetText();
            logger.LogDebug("[Rich] Fallback to full buffer text, length={Length}", output.Length);
        }

        // Defensive cleaning (strip any residual echo/prompt)
        output = OutputCleaner.CleanOutput(output, script);

        return new ExecuteResult(output, exitCode);
    }

    /// <summary>
    /// Send command using bracketed paste mode for multi-line commands.
    /// Requires Shell Integration + PSReadLine to correctly handle bracketed paste.
    /// </summary>
    public async Task SendCommandAsync(
        IPtyConnection pty,
        string script,
        bool isMultiline,
        CancellationToken cancellationToken)
    {
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