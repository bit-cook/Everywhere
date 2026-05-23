using System.Text;
using Microsoft.Extensions.Logging;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration is NOT available.
/// Falls back to idle detection + prompt heuristics + command echo stripping.
/// Uses Task.WhenAny pattern instead of CancellationToken-based polling to avoid
/// deadlocks when an earlier phase has a pending read.
/// </summary>
public sealed class NoneExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    private bool _isCapturingTranscript;

    public async Task<ExecuteResult> ExecuteAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var rawOutput = await ExecuteWithRawOutputAsync(session, script, shellType, timeout, cancellationToken);
        logger.LogDebug("[None] Raw output: {EscapeForLog}", OutputCleaner.EscapeForLog(rawOutput));
        var output = OutputCleaner.StripCommandEchoAndPrompt(rawOutput, script);

        logger.LogDebug("[None] Output length={Length}", output.Length);
        return new ExecuteResult(output, ExitCode: null);
    }

    private async ValueTask<string> ExecuteWithRawOutputAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();

        void HandleText(char value)
        {
            if (_isCapturingTranscript)
            {
                transcript.Append(value);
            }
        }

        session.Parser.TerminalTextReceived += HandleText;

        try
        {
            // Wait for terminal to become idle (quiet 200ms = idle, max wait 5s)
            logger.LogDebug("[None] Waiting for initial idle...");
            await WaitForIdleAsync(
                session,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(200),
                cancellationToken);

            logger.LogDebug("[None] Sending Ctrl+C to cancel residual input");
            await session.WriteInputAsync("\x03", cancellationToken);

            // Brief wait for Ctrl+C echo to settle (quiet 200ms = idle, max wait 500ms)
            await WaitForIdleAsync(
                session,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(200),
                cancellationToken);

            // Shared buffer is intentionally preserved. The command baseline prevents
            // startup prompts and detection output from polluting fallback output.
            transcript.Clear();
            var startLine = session.Buffer.CursorY;

            logger.LogDebug("[None] Sending command (shellType={ShellType})", shellType);
            _isCapturingTranscript = true;
            await SendCommandAsync(session, script, shellType, cancellationToken);

            logger.LogDebug("[None] Waiting for command output to start from line {StartLine}", startLine);
            await WaitForCommandOutputStartAsync(
                session,
                startLine,
                TimeSpan.FromSeconds(3),
                cancellationToken);

            logger.LogDebug("[None] Waiting for idle with prompt heuristics");
            var hasReceivedData = transcript.Length > 0;
            var hasSeenCommandEcho = ContainsCommandEcho(transcript, script);
            var consecutiveIdlePolls = 0;
            const int pollIntervalMs = 150;
            const int minIdlePolls = 4; // 4 x 150ms = 600ms idle
            const int maxFallbackIdlePolls = 14; // ~2.1s - force exit if prompt regex never matches

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var linkedToken = linkedCts.Token;

            try
            {
                while (!linkedToken.IsCancellationRequested)
                {
                    var readTask = session.BeginReadAsync(linkedToken);
                    var completedTask = await Task.WhenAny(
                        readTask,
                        Task.Delay(pollIntervalMs, linkedToken));

                    if (completedTask == readTask)
                    {
                        // Received new data
                        var bytesRead = await session.CompleteReadAsync(cancellationToken);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        consecutiveIdlePolls = 0;
                        hasReceivedData = true;
                        hasSeenCommandEcho |= ContainsCommandEcho(transcript, script);

                        if (hasSeenCommandEcho && CheckForPrompt(session.Buffer))
                        {
                            logger.LogDebug("[None] Prompt detected after receiving data");
                            break;
                        }
                    }
                    else
                    {
                        // Idle - no data within pollIntervalMs
                        if (!hasReceivedData) continue;

                        consecutiveIdlePolls++;
                        if (consecutiveIdlePolls >= minIdlePolls)
                        {
                            if (hasSeenCommandEcho && CheckForPrompt(session.Buffer))
                            {
                                logger.LogDebug("[None] Idle detected with prompt");
                                break;
                            }

                            // Fallback: if terminal has been silent for ~2s without strict prompt match,
                            // assume command is complete to prevent hanging.
                            if (consecutiveIdlePolls >= maxFallbackIdlePolls)
                            {
                                logger.LogWarning("[None] Maximum idle threshold reached without strict prompt match. Assuming complete.");
                                break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                logger.LogWarning("[None] timeout reached");
                try
                {
                    session.Pty.Kill();
                }
                catch
                {
                    // ignore
                }
            }

            _isCapturingTranscript = false;
            return transcript.Length > 0
                ? NormalizeCapturedText(transcript.ToString())
                : session.GetTextFromLine(startLine);
        }
        finally
        {
            session.Parser.TerminalTextReceived -= HandleText;
        }
    }

    /// <summary>
    /// Check if the current cursor line (or the line above) looks like a shell prompt.
    /// Also checks the previous line because PSReadLine may shift the cursor.
    /// </summary>
    private static bool CheckForPrompt(VirtualTerminalBuffer vtBuffer)
    {
        var cursorLine = vtBuffer.GetCursorLine();
        if (OutputCleaner.IsShellPrompt(cursorLine)) return true;

        var prevLine = vtBuffer.GetLineText(Math.Max(0, vtBuffer.CursorY - 1));
        return !string.IsNullOrWhiteSpace(prevLine) && OutputCleaner.IsShellPrompt(prevLine);
    }

    /// <summary>
    /// Send command line by line for multi-line commands.
    /// Does not use bracketed paste mode - each line is sent as a separate Enter keypress.
    /// This is safe without Shell Integration or PSReadLine.
    /// </summary>
    public async static Task SendCommandAsync(
        TerminalSession session,
        string script,
        ShellType shellType,
        CancellationToken cancellationToken)
    {
        var isMultiline = OutputCleaner.IsMultilineCommand(script, shellType);
        if (isMultiline)
        {
            // Multi-line: split and send line by line
            var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                await session.WriteInputAsync($"{trimmed}\r", cancellationToken);
                // Brief pause between lines to let the shell process each one
                await Task.Delay(100, cancellationToken);
            }
        }
        else
        {
            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                await session.WriteInputAsync($"{NormalizeCommandNewlines(trimmed)}\r", cancellationToken);
            }
        }
    }

    private static string NormalizeCommandNewlines(string script)
    {
        return script.Replace("\r\n", "\r").Replace("\n", "\r");
    }

    private static string NormalizeCapturedText(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static bool ContainsCommandEcho(StringBuilder transcript, string script)
    {
        if (transcript.Length == 0) return false;
        var normalized = NormalizeCapturedText(transcript.ToString());
        return OutputCleaner.FindCommandEcho(normalized, script, allowSuffixMatch: true).HasValue;
    }

    /// <summary>
    /// Wait for the PTY to become idle (no data for a short period).
    /// Separates max wait timeout from quiet period: if no data arrives within
    /// <paramref name="quietPeriod"/>, the terminal is considered idle.
    /// Uses Task.WhenAny to avoid CancellationToken-based exception overhead.
    /// </summary>
    private static async Task WaitForIdleAsync(
        TerminalSession session,
        TimeSpan maxWait,
        TimeSpan quietPeriod,
        CancellationToken cancellationToken)
    {
        var quietMs = (int)quietPeriod.TotalMilliseconds;
        using var timeoutCts = new CancellationTokenSource(maxWait);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                var readTask = session.BeginReadAsync(linkedToken);
                var quietDelayTask = Task.Delay(quietMs, linkedToken);

                var completedTask = await Task.WhenAny(readTask, quietDelayTask);

                if (completedTask == readTask)
                {
                    // Received data - reset quiet period
                    var bytesRead = await session.CompleteReadAsync(cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                }
                else
                {
                    // No data arrived within quietPeriod - terminal is idle
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // maxWait reached or external cancellation
        }
    }

    /// <summary>
    /// Wait for command output to begin.
    /// Stores the pending read task in the session so it can carry over to the next phase.
    /// </summary>
    private static async Task WaitForCommandOutputStartAsync(
        TerminalSession session,
        int startLine,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                if (session.Buffer.CursorY > startLine) return;

                var readTask = session.BeginReadAsync(linkedToken);
                var pollDelayTask = Task.Delay(100, linkedToken);

                var completedTask = await Task.WhenAny(readTask, pollDelayTask);
                if (completedTask == readTask)
                {
                    var bytesRead = await session.CompleteReadAsync(cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation
        }
    }
}
