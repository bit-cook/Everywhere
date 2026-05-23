using System.Text;

namespace Everywhere.Terminal;

public delegate void ShellIntegrationMarkerHandler(in ShellIntegrationMarker marker);

public delegate void TerminalTextHandler(char value);

public delegate void TerminalResponseHandler(string response);

/// <summary>
/// A minimal VT100/ECMA-48 sequence parser that drives a <see cref="VirtualTerminalBuffer"/>.
/// Implements the state machine needed to handle ANSI escape sequences from PTY output,
/// including CSI, OSC, and single-character escape sequences.
///
/// This parser is intentionally minimal — it only handles sequences that affect text layout
/// (cursor movement, erase, scroll, etc.). Color/style sequences are consumed but ignored.
/// </summary>
public sealed class VtSequenceParser(
    VirtualTerminalBuffer buffer,
    ShellIntegrationMarkerHandler? shellIntegrationMarkerHandler = null,
    TerminalResponseHandler? terminalResponseHandler = null,
    TerminalTextHandler? terminalTextHandler = null,
    TerminalDimensions? dimensions = null
)
{
    public event ShellIntegrationMarkerHandler? ShellIntegrationMarkerReceived
    {
        add => _shellIntegrationMarkerHandler += value;
        remove => _shellIntegrationMarkerHandler -= value;
    }

    public event TerminalResponseHandler? TerminalResponseRequested
    {
        add => _terminalResponseHandler += value;
        remove => _terminalResponseHandler -= value;
    }

    public event TerminalTextHandler? TerminalTextReceived
    {
        add => _terminalTextHandler += value;
        remove => _terminalTextHandler -= value;
    }

    /// <summary>
    /// Whether any Shell Integration (OSC 633) markers have been detected.
    /// </summary>
    public bool HasDetectedShellIntegration { get; private set; }

    /// <summary>
    /// Whether the shell has requested focus in/out reports via DECSET ?1004.
    /// </summary>
    public bool IsFocusEventTrackingEnabled { get; private set; }

    /// <summary>
    /// Whether the shell has requested bracketed paste via DECSET ?2004.
    /// </summary>
    public bool IsBracketedPasteModeEnabled { get; private set; }

    /// <summary>
    /// Whether the shell has requested Windows Terminal's Win32 input mode via DECSET ?9001.
    /// </summary>
    public bool IsWin32InputModeEnabled { get; private set; }

    private enum State
    {
        Ground,
        Escape,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        OscString,
        OscStringEscape, // Inside OSC, saw ESC — waiting for '\' to form ST
        CharsetSelect,
    }

    private State _state = State.Ground;

    // CSI parameter accumulation
    private readonly List<int> _csiParams = [];

    private int _currentParam = -1; // -1 means "no digits accumulated yet"
    private bool _csiPrivateParam; // '?' prefix
    private char _csiPrefix; // '?', '>', '=' or '\0'
    private char _csiIntermediate; // intermediate character (e.g. '!' for DECSTR)

    // OSC string accumulation
    private readonly StringBuilder _oscBuffer = new(64);

    // Current terminal dimensions for handling cursor position reports and bounds checking.
    private TerminalDimensions _dimensions = dimensions ?? TerminalDimensions.Default;

    // UTF-16 surrogate pair tracking
    private char _highSurrogate;

    private ShellIntegrationMarkerHandler? _shellIntegrationMarkerHandler = shellIntegrationMarkerHandler;
    private TerminalResponseHandler? _terminalResponseHandler = terminalResponseHandler;
    private TerminalTextHandler? _terminalTextHandler = terminalTextHandler;

    /// <summary>
    /// Feed a chunk of text (from PTY output) to the parser.
    /// </summary>
    public void Feed(ReadOnlySpan<char> input)
    {
        foreach (var c in input)
        {
            FeedChar(c);
        }
    }

    /// <summary>
    /// Feed a single character to the parser state machine.
    /// </summary>
    private void FeedChar(char c)
    {
        // Handle UTF-16 surrogate pairs
        if (char.IsHighSurrogate(c))
        {
            _highSurrogate = c;
            return;
        }
        if (char.IsLowSurrogate(c))
        {
            if (_highSurrogate != 0)
            {
                // Complete surrogate pair — write as two chars
                if (_state == State.Ground)
                {
                    buffer.WriteChar(_highSurrogate);
                    _terminalTextHandler?.Invoke(_highSurrogate);
                    buffer.WriteChar(c);
                    _terminalTextHandler?.Invoke(c);
                }
                _highSurrogate = '\0';
            }

            // Orphan low surrogate, skip
            return;
        }

        // Flush any pending high surrogate
        if (_highSurrogate != 0)
        {
            if (_state == State.Ground)
            {
                buffer.WriteChar(_highSurrogate);
                _terminalTextHandler?.Invoke(_highSurrogate);
            }
            _highSurrogate = '\0';
        }

        switch (_state)
        {
            case State.Ground:
                HandleGround(c);
                break;
            case State.Escape:
                HandleEscape(c);
                break;
            case State.CsiEntry:
                HandleCsiEntry(c);
                break;
            case State.CsiParam:
                HandleCsiParam(c);
                break;
            case State.CsiIntermediate:
                HandleCsiIntermediate(c);
                break;
            case State.OscString:
                HandleOscString(c);
                break;
            case State.OscStringEscape:
                HandleOscStringEscape(c);
                break;
            case State.CharsetSelect:
                // Consume the charset designation character and return to ground
                _state = State.Ground;
                break;
        }
    }

    #region State Handlers

    private void HandleGround(char c)
    {
        switch (c)
        {
            case '\e': // ESC
                _state = State.Escape;
                break;
            case '\r': // CR
                buffer.CarriageReturn();
                _terminalTextHandler?.Invoke(c);
                break;
            case '\n': // LF
            case '\v': // VT
            case '\f': // FF
                buffer.LineFeed();
                _terminalTextHandler?.Invoke(c);
                break;
            case '\b': // BS
                buffer.Backspace();
                _terminalTextHandler?.Invoke(c);
                break;
            case '\t': // HT
                buffer.Tab();
                _terminalTextHandler?.Invoke(c);
                break;
            case '\a': // BEL — ignore
                break;
            case '\0': // NUL — ignore
                break;
            default:
                if (c >= 0x20) // Printable character (including Unicode)
                {
                    buffer.WriteChar(c);
                    _terminalTextHandler?.Invoke(c);
                }
                break;
        }
    }

    private void HandleEscape(char c)
    {
        switch (c)
        {
            case '[': // CSI
                _state = State.CsiEntry;
                ResetCsi();
                break;
            case ']': // OSC
                _state = State.OscString;
                _oscBuffer.Clear();
                break;
            case '(': // Charset designation G0
            case ')': // Charset designation G1
                _state = State.CharsetSelect;
                break;
            case '7': // DECSC — Save Cursor
                buffer.SaveCursor();
                _state = State.Ground;
                break;
            case '8': // DECRC — Restore Cursor
                buffer.RestoreCursor();
                _state = State.Ground;
                break;
            case 'D': // IND — Index (move down, scroll if at bottom)
                buffer.LineFeed();
                _state = State.Ground;
                break;
            case 'E': // NEL — Next Line
                buffer.CarriageReturn();
                buffer.LineFeed();
                _state = State.Ground;
                break;
            case 'M': // RI — Reverse Index (move up, scroll if at top)
                if (buffer.CursorY <= 0) buffer.ScrollDown();
                else buffer.CursorUp();
                _state = State.Ground;
                break;
            case 'c': // RIS — Full Reset
                buffer.EraseDisplay(2);
                buffer.CursorPosition();
                _state = State.Ground;
                break;
            case '=': // DECKPAM — Application Keypad Mode
            case '>': // DECKPNM — Normal Keypad Mode
                _state = State.Ground; // Ignore
                break;
            default:
                // Unknown escape sequence — return to ground
                _state = State.Ground;
                break;
        }
    }

    private void HandleCsiEntry(char c)
    {
        switch (c)
        {
            case >= '0' and <= '9':
                _currentParam = c - '0';
                _state = State.CsiParam;
                break;
            case '?':
                _csiPrivateParam = true;
                _csiPrefix = c;
                _state = State.CsiParam;
                break;
            case '>' or '=':
                // Secondary DA or other prefixed CSI
                _csiPrivateParam = true;
                _csiPrefix = c;
                _state = State.CsiParam;
                break;
            default:
            {
                if (c >= 0x40 && c <= 0x7E)
                {
                    // Single-character CSI with no params
                    DispatchCsi(c);
                }
                else if (c >= 0x20 && c <= 0x2F)
                {
                    _csiIntermediate = c;
                    _state = State.CsiIntermediate;
                }
                else
                {
                    // Invalid — return to ground
                    _state = State.Ground;
                }
                break;
            }
        }
    }

    private void HandleCsiParam(char c)
    {
        switch (c)
        {
            case >= '0' and <= '9':
            {
                if (_currentParam < 0) _currentParam = 0;
                _currentParam = _currentParam * 10 + (c - '0');
                break;
            }
            case ';':
                _csiParams.Add(_currentParam < 0 ? 0 : _currentParam);
                _currentParam = -1;
                break;
            default:
            {
                if (c >= 0x40 && c <= 0x7E)
                {
                    // Final byte — dispatch
                    if (_currentParam >= 0)
                    {
                        _csiParams.Add(_currentParam);
                    }
                    DispatchCsi(c);
                }
                else if (c >= 0x20 && c <= 0x2F)
                {
                    _csiIntermediate = c;
                    _state = State.CsiIntermediate;
                }
                else
                {
                    // Invalid — return to ground
                    _state = State.Ground;
                }
                break;
            }
        }
    }

    private void HandleCsiIntermediate(char c)
    {
        if (c >= 0x40 && c <= 0x7E)
        {
            // Final byte — dispatch (with intermediate)
            if (_currentParam >= 0)
            {
                _csiParams.Add(_currentParam);
            }
            DispatchCsi(c);
        }
        else if (c < 0x20 || c > 0x2F)
        {
            // Invalid — return to ground
            _state = State.Ground;
        }
        // else: more intermediate bytes, stay in this state
    }

    private void HandleOscString(char c)
    {
        switch (c)
        {
            case '\a': // BEL — end of OSC
                ProcessOsc();
                _state = State.Ground;
                break;
            case '\e': // Potential ST (ESC \)
                _state = State.OscStringEscape;
                break;
            default:
                _oscBuffer.Append(c);
                break;
        }
    }

    /// <summary>
    /// Inside OSC, we saw ESC. If next char is '\', it's ST (string terminator).
    /// Otherwise, treat the ESC as part of the OSC content and re-enter OscString.
    /// </summary>
    private void HandleOscStringEscape(char c)
    {
        if (c == '\\')
        {
            // ST — String Terminator
            ProcessOsc();
            _state = State.Ground;
        }
        else
        {
            // Not ST — the ESC was part of the content
            _oscBuffer.Append('\e');
            _oscBuffer.Append(c);
            _state = State.OscString;
        }
    }

    #endregion

    #region CSI Dispatch

    /// <summary>
    /// Dispatch a CSI sequence to the buffer.
    /// </summary>
    private void DispatchCsi(char finalByte)
    {
        // For private sequences (DECSET/DECRST), we mostly ignore them
        // but we need to handle some that affect layout
        if (_csiPrivateParam)
        {
            DispatchPrivateCsi(finalByte);
        }
        else
        {
            DispatchStandardCsi(finalByte);
        }

        ResetCsi();
        _state = State.Ground;
    }

    private void DispatchStandardCsi(char finalByte)
    {
        var p0 = GetParam(0, 0);
        var p1 = GetParam(1, 0);

        switch (finalByte)
        {
            case 'A': // CUU — Cursor Up
                buffer.CursorUp(Math.Max(p0, 1));
                break;
            case 'B': // CUD — Cursor Down
                buffer.CursorDown(Math.Max(p0, 1));
                break;
            case 'C': // CUF — Cursor Forward
                buffer.CursorForward(Math.Max(p0, 1));
                break;
            case 'D': // CUB — Cursor Backward
                buffer.CursorBackward(Math.Max(p0, 1));
                break;
            case 'E': // CNL — Cursor Next Line
                buffer.CursorNextLine(Math.Max(p0, 1));
                break;
            case 'F': // CPL — Cursor Previous Line
                buffer.CursorPreviousLine(Math.Max(p0, 1));
                break;
            case 'G': // CHA — Cursor Horizontal Absolute
                buffer.CursorHorizontalAbsolute(Math.Max(p0, 1));
                break;
            case 'H': // CUP — Cursor Position
            case 'f': // HVP — Horizontal Vertical Position
                buffer.CursorPosition(Math.Max(p0, 1), Math.Max(p1, 1));
                break;
            case 'J': // ED — Erase in Display
                buffer.EraseDisplay(p0);
                break;
            case 'K': // EL — Erase in Line
                buffer.EraseLine(p0);
                break;
            case 'L': // IL — Insert Lines
                buffer.InsertLines(Math.Max(p0, 1));
                break;
            case 'M': // DL — Delete Lines
                buffer.DeleteLines(Math.Max(p0, 1));
                break;
            case 'P': // DCH — Delete Characters
                buffer.DeleteChars(Math.Max(p0, 1));
                break;
            case 'S': // SU — Scroll Up
                buffer.ScrollUp(Math.Max(p0, 1));
                break;
            case 'T': // SD — Scroll Down
                buffer.ScrollDown(Math.Max(p0, 1));
                break;
            case '@': // ICH — Insert Characters
                buffer.InsertChars(Math.Max(p0, 1));
                break;
            case 'r': // DECSTBM — Set Scrolling Region
                buffer.SetScrollRegion(Math.Max(p0, 1), p1 > 0 ? p1 : -1);
                break;
            case 's': // SCP — Save Cursor Position
                buffer.SaveCursor();
                break;
            case 'u': // RCP — Restore Cursor Position
                buffer.RestoreCursor();
                break;
            case 'd': // VPA — Vertical Position Absolute
                buffer.CursorPosition(Math.Max(p0, 1), buffer.CursorX + 1);
                break;
            case 'X': // ECH — Erase Characters
                if (buffer.CursorY < 0) break;
                var eraseCount = Math.Max(p0, 1);
                for (var i = 0; i < eraseCount && buffer.CursorX + i < _dimensions.Columns; i++)
                {
                    // Write space at cursor + i position
                    var savedX = buffer.CursorX;
                    buffer.CursorX += i;
                    buffer.WriteChar(' ');
                    buffer.CursorX = savedX;
                }
                break;
            case 'm': // SGR — Select Graphic Rendition (colors/styles) — ignore
                break;
            case 'n': // DSR — Device Status Report
                InvokeDeviceStatusReport(p0);
                break;
            case 'c': // DA — Device Attributes
                if (p0 == 0)
                {
                    _terminalResponseHandler?.Invoke("\e[?1;0c");
                }
                break;
            case 'q': // DECSCUSR — Set Cursor Style — ignore
            case 'x': // DECREQTPARM — ignore
            case 'g': // TBC — Tab Clear — ignore
            case 'Z': // CBT — Cursor Backward Tabulation — ignore
            case 'I': // CHT — Cursor Horizontal Tabulation — ignore
            case 'b': // REP — Repeat — ignore
            case 'z': // DECERA — ignore
            case '{': // DECSERA — ignore
            case 'p': // various — ignore
            case 't': // Window manipulation
                if (p0 == 18)
                {
                    _terminalResponseHandler?.Invoke($"\e[8;{_dimensions.Rows};{_dimensions.Columns}t");
                }
                break;
        }
    }

    private void DispatchPrivateCsi(char finalByte)
    {
        if (_csiPrefix == '>')
        {
            if (finalByte == 'c')
            {
                // Secondary Device Attributes. Keep the identity conservative.
                _terminalResponseHandler?.Invoke("\e[>0;0;0c");
            }
            return;
        }

        if (_csiPrefix != '?')
        {
            return;
        }

        // Private sequences (prefixed with '?')
        // Most are DECSET/DECRST for terminal modes
        var p0 = GetParam(0, 0);

        switch (finalByte)
        {
            case 'h': // DECSET — Set Mode
            case 'l': // DECRST — Reset Mode
            {
                var enabled = finalByte == 'h';
                if (_csiParams.Count == 0)
                {
                    SetPrivateMode(p0, enabled);
                }
                else
                {
                    foreach (var mode in _csiParams)
                    {
                        SetPrivateMode(mode, enabled);
                    }
                }
                break;
            }
            case 'J': // DECSED — Selective Erase in Display
                buffer.EraseDisplay(p0);
                break;
            case 'K': // DECSEL — Selective Erase in Line
                buffer.EraseLine(p0);
                break;
            case 'm': // SGR with private params — ignore (colors)
                break;
            case 'n': // DSR — Device Status Report
                if (p0 == 6)
                {
                    _terminalResponseHandler?.Invoke(BuildCursorPositionReport(isPrivate: true));
                }
                break;
            // Unknown private CSI — ignore
        }
    }

    #endregion

    #region OSC Processing

    private void ProcessOsc()
    {
        var content = _oscBuffer.ToString();

        // Check for Shell Integration markers: OSC 633 ; <type> [; <data>] ST
        if (content.StartsWith("633;"))
        {
            ProcessShellIntegrationMarker(content);
        }

        // All other OSC sequences are ignored for text extraction.
    }

    /// <summary>
    /// Parse an OSC 633 Shell Integration marker and fire the event.
    /// </summary>
    private void ProcessShellIntegrationMarker(string content)
    {
        // Format: 633 ; <type> [; <data>]
        // content = "633;A" or "633;D;0" or "633;E;command;nonce" etc.
        var parts = content.Split(';');
        if (parts.Length < 2) return;

        HasDetectedShellIntegration = true;
        var markerChar = parts[1];
        switch (markerChar)
        {
            case "A":
            {
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.PromptStart,
                        Line: buffer.CursorY));
                break;
            }
            case "B":
            {
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandReady,
                        Line: buffer.CursorY));
                break;
            }
            case "C":
            {
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandExecuted,
                        Line: buffer.CursorY));
                break;
            }
            case "D":
            {
                // D may have an exit code: 633;D;<exitcode>
                int? exitCode = null;
                if (parts.Length >= 3 && int.TryParse(parts[2], out var code))
                {
                    exitCode = code;
                }
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandFinished,
                        ExitCode: exitCode,
                        Line: buffer.CursorY));
                break;
            }
            case "E":
            {
                // E has command text: 633;E;<command>[;<nonce>]
                var cmdLine = parts.Length >= 3 ? parts[2] : null;
                _shellIntegrationMarkerHandler?.Invoke(
                    new ShellIntegrationMarker(
                        ShellIntegrationMarkerType.CommandLine,
                        CommandLine: cmdLine,
                        Line: buffer.CursorY));
                break;
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Reset the parser state. Does not reset <see cref="HasDetectedShellIntegration"/>.
    /// </summary>
    public void Reset()
    {
        _state = State.Ground;
        _highSurrogate = '\0';
        ResetCsi();
        _oscBuffer.Clear();
    }

    /// <summary>
    /// Reset the shell integration detection flag.
    /// </summary>
    public void ResetShellIntegrationDetected()
    {
        HasDetectedShellIntegration = false;
    }

    /// <summary>
    /// Update the visible terminal rows used for terminal query responses.
    /// </summary>
    public void Resize(TerminalDimensions dimensions)
    {
        _dimensions = dimensions;
    }

    private void ResetCsi()
    {
        _csiParams.Clear();
        _currentParam = -1;
        _csiPrivateParam = false;
        _csiPrefix = '\0';
        _csiIntermediate = '\0';
    }

    /// <summary>
    /// Get the n-th CSI parameter (0-based), with a default value if not provided.
    /// CSI parameters are 1-based in the spec, but 0 means "use default".
    /// </summary>
    private int GetParam(int index, int defaultValue)
    {
        if (index < _csiParams.Count)
        {
            var val = _csiParams[index];
            return val > 0 ? val : defaultValue;
        }

        return defaultValue;
    }

    private void InvokeDeviceStatusReport(int request)
    {
        switch (request)
        {
            case 5:
                // "OK" status report.
                _terminalResponseHandler?.Invoke("\e[0n");
                break;
            case 6:
                _terminalResponseHandler?.Invoke(BuildCursorPositionReport(isPrivate: false));
                break;
        }
    }

    private string BuildCursorPositionReport(bool isPrivate)
    {
        var row = Math.Clamp(buffer.CursorY + 1, 1, _dimensions.Rows);
        var column = Math.Clamp(buffer.CursorX + 1, 1, _dimensions.Columns);
        return isPrivate ? $"\e[?{row};{column}R" : $"\e[{row};{column}R";
    }

    private void SetPrivateMode(int mode, bool enabled)
    {
        switch (mode)
        {
            case 1004:
                IsFocusEventTrackingEnabled = enabled;
                break;
            case 2004:
                IsBracketedPasteModeEnabled = enabled;
                break;
            case 9001:
                IsWin32InputModeEnabled = enabled;
                break;
        }
    }

    #endregion

}