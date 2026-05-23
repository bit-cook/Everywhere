using System.Text;
using Everywhere.Common;
using Porta.Pty;

namespace Everywhere.Terminal;

/// <summary>
/// Shared state and IO helpers for a single PTY-backed terminal session.
/// </summary>
public sealed class TerminalSession
{
    public IPtyConnection Pty { get; }

    public TerminalDimensions Dimensions { get; private set; }

    public VirtualTerminalBuffer Buffer { get; }

    public VtSequenceParser Parser { get; }

    public PtyTextDecoder TextDecoder { get; }

    public TerminalResponseWriter ResponseWriter { get; }

    public byte[] ReadBuffer { get; } = new byte[ReadBufferSize];

    public event Action<TerminalSession>? BufferChanged;

    private const int ReadBufferSize = 4096;

    private Task<int>? _pendingReadTask;

    public TerminalSession(IPtyConnection pty, TerminalDimensions dimensions)
    {
        Pty = pty;
        Dimensions = dimensions;
        Buffer = new VirtualTerminalBuffer(dimensions.Columns);
        TextDecoder = new PtyTextDecoder(ReadBufferSize);
        ResponseWriter = new TerminalResponseWriter(pty);
        Parser = new VtSequenceParser(
            Buffer,
            terminalResponseHandler: ResponseWriter.Queue,
            dimensions: dimensions);
    }

    public static TerminalSession FromPtyOptions(IPtyConnection pty, PtyOptions options)
    {
        return new TerminalSession(pty, TerminalDimensions.FromPtyOptions(options));
    }

    public Task<int> BeginReadAsync(CancellationToken cancellationToken)
    {
        return _pendingReadTask ??= Pty.ReaderStream.ReadAsync(ReadBuffer, cancellationToken).AsTask();
    }

    public async ValueTask<int> CompleteReadAsync(CancellationToken cancellationToken)
    {
        if (_pendingReadTask is not { } readTask)
        {
            throw new InvalidOperationException("No pending PTY read exists.");
        }

        try
        {
            var bytesRead = await readTask;
            if (bytesRead == 0)
            {
                Feed(TextDecoder.Flush());
            }
            else
            {
                Feed(TextDecoder.Decode(ReadBuffer.AsSpan(0, bytesRead)));
            }

            await FlushTerminalResponsesAsync(cancellationToken);
            return bytesRead;
        }
        finally
        {
            if (ReferenceEquals(_pendingReadTask, readTask))
            {
                _pendingReadTask = null;
            }
        }
    }

    public async ValueTask<int> ReadAsync(CancellationToken cancellationToken)
    {
        BeginReadAsync(cancellationToken).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        return await CompleteReadAsync(cancellationToken);
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return;

        Parser.Feed(text);
        BufferChanged?.Invoke(this);
    }

    public ValueTask FlushTerminalResponsesAsync(CancellationToken cancellationToken)
    {
        return ResponseWriter.FlushAsync(cancellationToken);
    }

    public async ValueTask WriteInputAsync(string input, CancellationToken cancellationToken = default)
    {
        if (input.Length == 0) return;

        await Pty.WriterStream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellationToken);
        await Pty.WriterStream.FlushAsync(cancellationToken);
    }

    public async ValueTask WritePasteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (text.Length == 0) return;

        if (Parser.IsBracketedPasteModeEnabled)
        {
            await WriteInputAsync("\e[200~", cancellationToken);
            await WriteInputAsync(text.Replace("\r\n", "\n").Replace('\r', '\n'), cancellationToken);
            await WriteInputAsync("\e[201~", cancellationToken);
            return;
        }

        await WriteInputAsync(text.Replace("\r\n", "\r").Replace('\n', '\r'), cancellationToken);
    }

    public async ValueTask ResizeAsync(TerminalDimensions dimensions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        dimensions = new TerminalDimensions(dimensions.Columns, dimensions.Rows);
        if (dimensions == Dimensions) return;

        Pty.Resize(dimensions.Columns, dimensions.Rows);
        Dimensions = dimensions;
        Buffer.Resize(dimensions.Columns);
        Parser.Resize(dimensions);
        BufferChanged?.Invoke(this);

        await FlushTerminalResponsesAsync(cancellationToken);
    }

    public string GetTextFromLine(int startLine)
    {
        return Buffer.GetTextBetween(startLine, -1);
    }
}
