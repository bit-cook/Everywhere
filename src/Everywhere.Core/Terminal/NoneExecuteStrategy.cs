using System.Text;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Everywhere.Terminal;

/// <summary>
/// Execute strategy used when Shell Integration is NOT available.
/// Falls back to idle detection + prompt heuristics + command echo stripping.
/// Uses Task.WhenAny pattern instead of CancellationToken-based polling to avoid
/// deadlocks when Phase 5 drains the read buffer before Phase 6 starts.
/// </summary>
internal sealed class NoneExecuteStrategy(ILogger logger) : IExecuteStrategy
{
    private bool _isCapturingTranscript;

    public async Task<ExecuteResult> ExecuteAsync(
        IPtyConnection pty,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var rawOutput = await ExecuteWithRawOutputAsync(pty, script, shellType, timeout, cancellationToken);
        logger.LogDebug("[None] Raw output: {EscapeForLog}", OutputCleaner.EscapeForLog(rawOutput));
        var output = OutputCleaner.CleanOutput(rawOutput, script);

        logger.LogDebug("[None] Output length={Length}", output.Length);
        return new ExecuteResult(output, ExitCode: null);
    }

    private async ValueTask<string> ExecuteWithRawOutputAsync(
        IPtyConnection pty,
        string script,
        ShellType shellType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var readBuffer = new byte[4096];
        var textDecoder = new PtyTextDecoder(readBuffer.Length);
        var vtBuffer = new VirtualTerminalBuffer(1024);
        var transcript = new StringBuilder();
        var parser = new VtSequenceParser(
            vtBuffer,
            terminalTextHandler: value =>
            {
                if (_isCapturingTranscript)
                {
                    transcript.Append(value);
                }
            });

        // Shared read task holder — carries the pending ReadAsync across all phases
        // so we never lose data that arrives between phases.
        var readTaskHolder = new ReadTaskHolder();

        // Wait for terminal to become idle (quiet 200ms = idle, max wait 5s)
        logger.LogDebug("[None] Waiting for initial idle...");
        await WaitForIdleAsync(
            pty,
            readBuffer,
            parser,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(200),
            readTaskHolder,
            textDecoder,
            cancellationToken);

        logger.LogDebug("[None] Sending Ctrl+C to cancel residual input");
        await pty.WriterStream.WriteAsync("\x03"u8.ToArray(), cancellationToken);
        await pty.WriterStream.FlushAsync(cancellationToken);

        // Brief wait for Ctrl+C echo to settle (quiet 100ms = idle, max wait 500ms)
        await WaitForIdleAsync(
            pty,
            readBuffer,
            parser,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(200),
            readTaskHolder,
            textDecoder,
            cancellationToken);

        // Drop startup prompt/control output so completion checks only see data
        // produced after this command is sent.
        vtBuffer.Reset();
        parser.Reset();
        transcript.Clear();

        // Record the start position (cursor line before command)
        var startLine = vtBuffer.CursorY;

        logger.LogDebug("[None] Sending command (shellType={ShellType})", shellType);
        _isCapturingTranscript = true;
        await SendCommandAsync(pty, script, shellType, cancellationToken);

        logger.LogDebug("[None] Waiting for command output to start from line {StartLine}", startLine);
        await WaitForCommandOutputStartAsync(pty, readBuffer, parser, startLine, TimeSpan.FromSeconds(3), readTaskHolder, textDecoder, cancellationToken);

        logger.LogDebug("[None] Waiting for idle with prompt heuristics");
        var hasReceivedData = transcript.Length > 0;
        var hasSeenCommandEcho = ContainsCommandEcho(transcript, script);
        var consecutiveIdlePolls = 0;
        const int pollIntervalMs = 150;
        const int minIdlePolls = 4; // 4 × 150ms = 600ms idle
        const int maxFallbackIdlePolls = 14; // ~2.1s — force exit if prompt regex never matches

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                readTaskHolder.Task ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var completedTask = await Task.WhenAny(
                    readTaskHolder.Task,
                    Task.Delay(pollIntervalMs, linkedToken));

                if (completedTask == readTaskHolder.Task)
                {
                    // Received new data
                    var bytesRead = await readTaskHolder.Task;
                    readTaskHolder.Task = null;
                    if (bytesRead == 0)
                    {
                        parser.Feed(textDecoder.Flush());
                        break;
                    }

                    var text = textDecoder.Decode(readBuffer.AsSpan(0, bytesRead));
                    parser.Feed(text);

                    if (text.IndexOfAny('\n', '\r') >= 0)
                    {
                        logger.LogDebug("[PTY RAW] {EscapeForLog}", OutputCleaner.EscapeForLog(new string(text)));
                    }

                    consecutiveIdlePolls = 0;
                    hasReceivedData = true;
                    hasSeenCommandEcho |= ContainsCommandEcho(transcript, script);

                    if (hasSeenCommandEcho && CheckForPrompt(vtBuffer))
                    {
                        logger.LogDebug("[None] Prompt detected after receiving data");
                        break;
                    }
                }
                else
                {
                    // Idle — no data within pollIntervalMs
                    if (!hasReceivedData) continue;

                    consecutiveIdlePolls++;
                    if (consecutiveIdlePolls >= minIdlePolls)
                    {
                        if (hasSeenCommandEcho && CheckForPrompt(vtBuffer))
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
                pty.Kill();
            }
            catch
            {
                // ignore
            }
        }

        _isCapturingTranscript = false;
        return transcript.Length > 0 ? NormalizeCapturedText(transcript.ToString()) : vtBuffer.GetText();
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
    /// Does not use bracketed paste mode — each line is sent as a separate Enter keypress.
    /// This is safe without Shell Integration or PSReadLine.
    /// </summary>
    public async static Task SendCommandAsync(
        IPtyConnection pty,
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

                await pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(trimmed), cancellationToken);
                await pty.WriterStream.WriteAsync("\r"u8.ToArray(), cancellationToken);
                await pty.WriterStream.FlushAsync(cancellationToken);
                // Brief pause between lines to let the shell process each one
                await Task.Delay(100, cancellationToken);
            }
        }
        else
        {
            var trimmed = script.Trim();
            if (trimmed.Length > 0)
            {
                await pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(trimmed), cancellationToken);
                await pty.WriterStream.WriteAsync("\r"u8.ToArray(), cancellationToken);
                await pty.WriterStream.FlushAsync(cancellationToken);
            }
        }
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
        IPtyConnection pty,
        byte[] readBuffer,
        VtSequenceParser parser,
        TimeSpan maxWait,
        TimeSpan quietPeriod,
        ReadTaskHolder readTaskHolder,
        PtyTextDecoder textDecoder,
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
                readTaskHolder.Task ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var quietDelayTask = Task.Delay(quietMs, linkedToken);

                var completedTask = await Task.WhenAny(readTaskHolder.Task, quietDelayTask);

                if (completedTask == readTaskHolder.Task)
                {
                    // Received data — reset quiet period
                    var bytesRead = await readTaskHolder.Task;
                    readTaskHolder.Task = null;
                    if (bytesRead == 0)
                    {
                        parser.Feed(textDecoder.Flush());
                        break;
                    }

                    parser.Feed(textDecoder.Decode(readBuffer.AsSpan(0, bytesRead)));
                }
                else
                {
                    // No data arrived within quietPeriod — terminal is idle
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
    /// Stores the pending read task in the holder so it can carry over to the next phase.
    /// </summary>
    private static async Task WaitForCommandOutputStartAsync(
        IPtyConnection pty,
        byte[] readBuffer,
        VtSequenceParser parser,
        int startLine,
        TimeSpan timeout,
        ReadTaskHolder readTaskHolder,
        PtyTextDecoder textDecoder,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            while (!linkedToken.IsCancellationRequested)
            {
                if (parser.Buffer.CursorY > startLine) return;

                readTaskHolder.Task ??= pty.ReaderStream.ReadAsync(readBuffer, linkedToken).AsTask();
                var pollDelayTask = Task.Delay(100, linkedToken);

                var completedTask = await Task.WhenAny(readTaskHolder.Task, pollDelayTask);
                if (completedTask == readTaskHolder.Task)
                {
                    var bytesRead = await readTaskHolder.Task;
                    readTaskHolder.Task = null;
                    if (bytesRead == 0)
                    {
                        parser.Feed(textDecoder.Flush());
                        break;
                    }

                    parser.Feed(textDecoder.Decode(readBuffer.AsSpan(0, bytesRead)));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation
        }
    }

    /// <summary>
    /// Simple holder to pass a mutable Task reference between async methods.
    /// </summary>
    private sealed class ReadTaskHolder
    {
        public Task<int>? Task { get; set; }
    }
}
