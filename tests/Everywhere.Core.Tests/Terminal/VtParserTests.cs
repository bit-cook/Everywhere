using System.Text;
using Everywhere.Terminal;

namespace Everywhere.Core.Tests.Terminal;

/// <summary>
/// Unit tests for <see cref="VtSequenceParser"/> and <see cref="VirtualTerminalBuffer"/>.
/// </summary>
[TestFixture]
public class VtParserTests
{
    #region VirtualTerminalBuffer — Basic Operations

    [Test]
    public void Buffer_WriteChar_AdvancesCursor()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteChar('A');
        Assert.That(buffer.CursorX, Is.EqualTo(1));
        Assert.That(buffer.CursorY, Is.EqualTo(0));
    }

    [Test]
    public void Buffer_WriteString_PlacesText()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        Assert.That(buffer.GetText(), Is.EqualTo("hello"));
    }

    [Test]
    public void Buffer_CarriageReturn_ResetsCursorX()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        buffer.CarriageReturn();
        Assert.That(buffer.CursorX, Is.EqualTo(0));
        Assert.That(buffer.CursorY, Is.EqualTo(0));
    }

    [Test]
    public void Buffer_LineFeed_MovesCursorDown()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line1");
        buffer.CarriageReturn();
        buffer.LineFeed();
        Assert.That(buffer.CursorY, Is.EqualTo(1));
    }

    [Test]
    public void Buffer_Backspace_MovesCursorLeft()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello"); // cursor at X=5
        buffer.Backspace();          // cursor at X=4
        Assert.That(buffer.CursorX, Is.EqualTo(4));
    }

    [Test]
    public void Buffer_Tab_MovesToNextTabStop()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.Tab();
        Assert.That(buffer.CursorX, Is.EqualTo(8));
    }

    [Test]
    public void Buffer_LineWrap_OnFullColumn()
    {
        var buffer = new VirtualTerminalBuffer(5);
        buffer.WriteString("abcdef"); // 6 chars in 5-col buffer
        Assert.That(buffer.CursorX, Is.EqualTo(1)); // wrapped to next line
        Assert.That(buffer.CursorY, Is.EqualTo(1));
    }

    #endregion

    #region VirtualTerminalBuffer — Text Extraction

    [Test]
    public void GetText_MultipleLines()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line1");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line2");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line3");

        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("line1\nline2\nline3"));
    }

    [Test]
    public void GetText_EmptyBuffer_ReturnsEmpty()
    {
        var buffer = new VirtualTerminalBuffer(80);
        Assert.That(buffer.GetText(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetTextBetween_SpecificRange()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line2");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line3");

        var text = buffer.GetTextBetween(1, 2);
        Assert.That(text, Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void GetTextBetween_OutOfRange_Clamps()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");

        var text = buffer.GetTextBetween(0, 100);
        Assert.That(text, Is.EqualTo("line0\nline1"));
    }

    [Test]
    public void GetTextBetween_StartAfterEnd_ReturnsEmpty()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        var text = buffer.GetTextBetween(5, 3);
        Assert.That(text, Is.EqualTo(string.Empty));
    }

    [Test]
    public void GetLastLine_ReturnsLastNonEmptyLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("first");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("second");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("third");

        Assert.That(buffer.GetLastLine(), Is.EqualTo("third"));
    }

    [Test]
    public void GetCursorLine_ReturnsCurrentLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");

        Assert.That(buffer.GetCursorLine(), Is.EqualTo("line1"));
    }

    #endregion

    #region VirtualTerminalBuffer — Cursor Movement

    [Test]
    public void CursorUp_DecreasesY()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CarriageReturn();
        buffer.LineFeed();
        buffer.WriteString("line1");
        buffer.CursorUp();
        Assert.That(buffer.CursorY, Is.EqualTo(0));
    }

    [Test]
    public void CursorDown_IncreasesY()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("line0");
        buffer.CursorDown();
        Assert.That(buffer.CursorY, Is.EqualTo(1));
    }

    [Test]
    public void CursorForward_IncreasesX()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        buffer.CursorBackward(5);
        buffer.CursorForward(3);
        Assert.That(buffer.CursorX, Is.EqualTo(3));
    }

    [Test]
    public void CursorPosition_MovesToAbsolute()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.CursorPosition(3, 5); // row=3, col=5 (1-based)
        Assert.That(buffer.CursorY, Is.EqualTo(2)); // 0-based
        Assert.That(buffer.CursorX, Is.EqualTo(4)); // 0-based
    }

    [Test]
    public void CursorHorizontalAbsolute_MovesToColumn()
    {
        var buffer = new VirtualTerminalBuffer(80);
        buffer.WriteString("hello");
        buffer.CursorHorizontalAbsolute(n: 1); // Move to column 1 (0-based index 0)
        Assert.That(buffer.CursorX, Is.Zero);
    }

    #endregion

    #region VtSequenceParser — Basic Text

    [Test]
    public void Feed_PrintableText_WrittenToBuffer()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello world");
        Assert.That(buffer.GetText(), Is.EqualTo("hello world"));
    }

    [Test]
    public void Feed_NewlineAdvancesLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // Real PTY output uses \r\n (CR+LF). LF alone only moves down without resetting column.
        parser.Feed("line1\r\nline2");
        Assert.That(buffer.GetText(), Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void Feed_CarriageReturn_ResetsColumn()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello\rworld");
        // CR overwrites from column 0
        Assert.That(buffer.GetText(), Is.EqualTo("world"));
    }

    [Test]
    public void Feed_EmptyString_NoChange()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("");
        Assert.That(buffer.GetText(), Is.EqualTo(string.Empty));
    }

    #endregion

    #region VtSequenceParser — ANSI Sequences

    [Test]
    public void Feed_AnsiColor_DoesNotAffectText()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // \e[31m = set red foreground, \e[0m = reset
        parser.Feed("\e[31mhello\e[0m");
        Assert.That(buffer.GetText(), Is.EqualTo("hello"));
    }

    [Test]
    public void Feed_TextHandler_EmitsGroundTextButSkipsControlSequences()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var text = new StringBuilder();
        var parser = new VtSequenceParser(buffer, terminalTextHandler: c => text.Append(c));

        parser.Feed("A\e[31mB\e[0m\e]633;A\a\r\nC");

        Assert.That(text.ToString(), Is.EqualTo("AB\r\nC"));
        Assert.That(buffer.GetText(), Is.EqualTo("AB\nC"));
    }

    [Test]
    public void Feed_CursorMovement_CSI()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // Write "a", move cursor right 5, write "b"
        parser.Feed("a\e[5Cb");
        Assert.That(buffer.CursorX, Is.EqualTo(7)); // a(1) + 5 + b(1)
    }

    [Test]
    public void Feed_EraseLine_ClearsLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello\r\e[2K"); // Write hello, CR, erase entire line
        var text = buffer.GetCursorLine();
        // After erase, the line should be empty (all spaces or null chars)
        Assert.That(text.Trim(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void Feed_ClearScreen_ResetsBuffer()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("line1\nline2\e[2J"); // Write two lines, then clear screen
        // After clear screen, text should be empty
        Assert.That(buffer.GetText().Trim(), Is.EqualTo(string.Empty));
    }

    #endregion

    #region VtSequenceParser — Terminal Query Responses

    [Test]
    public void Feed_PrimaryDeviceAttributes_QueuesConservativeResponse()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var responses = new List<string>();
        var parser = new VtSequenceParser(buffer, terminalResponseHandler: responses.Add);

        parser.Feed("\e[c");

        Assert.That(responses, Is.EqualTo(new[] { "\e[?1;0c" }));
    }

    [Test]
    public void Feed_FragmentedPrimaryDeviceAttributes_QueuesResponse()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var responses = new List<string>();
        var parser = new VtSequenceParser(buffer, terminalResponseHandler: responses.Add);

        parser.Feed("\e[");
        parser.Feed("c");

        Assert.That(responses, Is.EqualTo(new[] { "\e[?1;0c" }));
    }

    [Test]
    public void Feed_DeviceStatusReport_QueuesOkResponse()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var responses = new List<string>();
        var parser = new VtSequenceParser(buffer, terminalResponseHandler: responses.Add);

        parser.Feed("\e[5n");

        Assert.That(responses, Is.EqualTo(new[] { "\e[0n" }));
    }

    [Test]
    public void Feed_CursorPositionReport_UsesOneBasedCoordinates()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var responses = new List<string>();
        var parser = new VtSequenceParser(buffer, terminalResponseHandler: responses.Add, dimensions: new TerminalDimensions(80, 24));

        parser.Feed("abc\r\nx\e[6n");

        Assert.That(responses, Is.EqualTo(new[] { "\e[2;2R" }));
    }

    [Test]
    public void Feed_TextAreaSizeQuery_UsesConfiguredDimensions()
    {
        var buffer = new VirtualTerminalBuffer(120);
        var responses = new List<string>();
        var parser = new VtSequenceParser(buffer, terminalResponseHandler: responses.Add, dimensions: new TerminalDimensions(120, 50));

        parser.Feed("\e[18t");

        Assert.That(responses, Is.EqualTo(new[] { "\e[8;50;120t" }));
    }

    [Test]
    public void Feed_PrivateModes_TracksTerminalInputModes()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);

        parser.Feed("\e[?1004;2004;9001h");

        Assert.That(parser.IsFocusEventTrackingEnabled, Is.True);
        Assert.That(parser.IsBracketedPasteModeEnabled, Is.True);
        Assert.That(parser.IsWin32InputModeEnabled, Is.True);

        parser.Feed("\e[?1004;2004;9001l");

        Assert.That(parser.IsFocusEventTrackingEnabled, Is.False);
        Assert.That(parser.IsBracketedPasteModeEnabled, Is.False);
        Assert.That(parser.IsWin32InputModeEnabled, Is.False);
    }

    [Test]
    public void Feed_SecondaryDeviceAttributes_QueuesConservativeResponse()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var responses = new List<string>();
        var parser = new VtSequenceParser(buffer, terminalResponseHandler: responses.Add);

        parser.Feed("\e[>c");

        Assert.That(responses, Is.EqualTo(new[] { "\e[>0;0;0c" }));
    }

    #endregion

    #region VtSequenceParser — Shell Integration Markers

    [Test]
    public void Feed_ShellIntegrationMarker_DetectsShellIntegration()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        // OSC 633;A = PromptStart
        parser.Feed("\e]633;A\a");
        Assert.That(parser.HasDetectedShellIntegration, Is.True);
    }

    [Test]
    public void Feed_ShellIntegrationMarker_FiresHandler()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in m) => markers.Add(m));

        // A = PromptStart
        parser.Feed("\e]633;A\a");
        // B = CommandReady
        parser.Feed("\e]633;B\a");
        // C = CommandExecuted
        parser.Feed("\e]633;C\a");
        // D with exit code = CommandFinished
        parser.Feed("\e]633;D;0\a");

        Assert.That(markers, Has.Count.EqualTo(4));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.PromptStart));
        Assert.That(markers[1].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandReady));
        Assert.That(markers[2].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandExecuted));
        Assert.That(markers[3].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandFinished));
        Assert.That(markers[3].ExitCode, Is.EqualTo(0));
    }

    [Test]
    public void Feed_ShellIntegrationMarker_D_WithExitCode()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in m) => markers.Add(m));

        parser.Feed("\e]633;D;42\a");

        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandFinished));
        Assert.That(markers[0].ExitCode, Is.EqualTo(42));
    }

    [Test]
    public void Feed_ShellIntegrationMarker_E_WithCommandLine()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in m) => markers.Add(m));

        parser.Feed("\e]633;E;echo hello;nonce123\a");

        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.CommandLine));
        Assert.That(markers[0].CommandLine, Is.EqualTo("echo hello"));
    }

    [Test]
    public void Feed_NoShellIntegration_DetectedFalse()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("just normal text\nno markers here");
        Assert.That(parser.HasDetectedShellIntegration, Is.False);
    }

    #endregion

    #region VtSequenceParser — ST Terminator

    [Test]
    public void Feed_ShellIntegration_ST_Terminator()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var markers = new List<ShellIntegrationMarker>();
        var parser = new VtSequenceParser(buffer, (in m) => markers.Add(m));

        // OSC terminated with ESC\ (String Terminator) instead of BEL
        parser.Feed("\e]633;A\e\\");

        Assert.That(parser.HasDetectedShellIntegration, Is.True);
        Assert.That(markers, Has.Count.EqualTo(1));
        Assert.That(markers[0].Type, Is.EqualTo(ShellIntegrationMarkerType.PromptStart));
    }

    #endregion

    #region VtSequenceParser — Unicode

    [Test]
    public void Feed_UnicodeText()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("你好世界");
        Assert.That(buffer.GetText(), Is.EqualTo("你好世界"));
    }

    [Test]
    public void Feed_Emoji_SurrogatePairs()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello 🌍 world");
        // Emoji may be written as two chars in the buffer (surrogate pair)
        var text = buffer.GetText();
        Assert.That(text, Does.Contain("hello"));
        Assert.That(text, Does.Contain("world"));
    }

    #endregion

    #region VtSequenceParser — Reset

    [Test]
    public void Reset_ClearsParserState()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("hello");
        // Parser.Reset() resets the parser state machine (CSI params, OSC buffer, etc.)
        // but does NOT reset the buffer or cursor position.
        parser.Reset();
        // Cursor is still at X=5 after "hello", so "world" appends
        parser.Feed("world");
        Assert.That(buffer.GetText(), Is.EqualTo("helloworld"));
    }

    [Test]
    public void ResetShellIntegrationDetected_ClearsFlag()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("\e]633;A\a");
        Assert.That(parser.HasDetectedShellIntegration, Is.True);
        parser.ResetShellIntegrationDetected();
        Assert.That(parser.HasDetectedShellIntegration, Is.False);
    }

    #endregion

    #region VtSequenceParser — CJK / Unicode round-trip

    [Test]
    public void Feed_Cjk_Chinese_RoundTrip()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("你好世界");
        Assert.That(buffer.GetText(), Is.EqualTo("你好世界"));
    }

    [Test]
    public void Feed_Cjk_Japanese_RoundTrip()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("こんにちは");
        Assert.That(buffer.GetText(), Is.EqualTo("こんにちは"));
    }

    [Test]
    public void Feed_Cjk_Korean_RoundTrip()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("안녕하세요");
        Assert.That(buffer.GetText(), Is.EqualTo("안녕하세요"));
    }

    [Test]
    public void Feed_Cjk_MixedWithAscii()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("Hello 你好 World 世界!");
        Assert.That(buffer.GetText(), Is.EqualTo("Hello 你好 World 世界!"));
    }

    [Test]
    public void Feed_Emoji_4ByteUtf8_RoundTrip()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("🎉🎊✨");
        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("🎉🎊✨"));
    }

    [Test]
    public void Feed_ReplacementChar_UFFFD_Preserved()
    {
        // U+FFFD must not be silently discarded by the buffer or parser.
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("Before\uFFFDBetween\uFFFDAfter");
        var text = buffer.GetText();
        Assert.That(text, Does.Contain("\uFFFD"),
            "U+FFFD replacement characters must be preserved in buffer output");
        Assert.That(text, Is.EqualTo("Before\uFFFDBetween\uFFFDAfter"));
    }

    [Test]
    public void Feed_ReplacementChar_WithLineBreaks()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("Line1\uFFFD\r\nLine2\uFFFD");
        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("Line1\uFFFD\nLine2\uFFFD"));
    }

    [Test]
    public void Feed_Cjk_WithAnsiColorSequences()
    {
        // CJK characters should survive ANSI SGR sequences without corruption.
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("\e[31m红色\e[0m \e[32m绿色\e[0m \e[34m蓝色\e[0m");
        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("红色 绿色 蓝色"));
    }

    [Test]
    public void Feed_Cjk_WithCursorMovement_CorrectBufferState()
    {
        // Cursor movement should work correctly with CJK characters
        // (each CJK char is 1 display column in terminal, 1 char in buffer).
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("你好世界");
        Assert.That(buffer.CursorX, Is.EqualTo(4),
            "Cursor should advance by 4 columns for 4 CJK characters");
    }

    #endregion

    #region VtSequenceParser — Multi-line output order (regression: output reversal)

    [Test]
    public void GetText_MultiLine_PreservesChronologicalOrder()
    {
        // Regression: ensure GetText() returns lines in chronological order (not reversed).
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("1\r\n2\r\n3\r\n4\r\n5");
        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("1\n2\n3\n4\n5"),
            "GetText() must return lines in chronological order, not reversed");
    }

    [Test]
    public void GetText_MultiLine_CrLfSequence()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("FIRST\r\nSECOND\r\nTHIRD");
        var text = buffer.GetText();
        Assert.That(text, Is.EqualTo("FIRST\nSECOND\nTHIRD"));
    }

    [Test]
    public void TerminalTextHandler_PreservesOrder_DuringPsReadlineRedraw()
    {
        // Simulate PSReadLine repainting a pasted multi-line command before execution.
        // The terminalTextHandler transcript must preserve chronological order,
        // NOT the virtual screen order after cursor movement / erase sequences.
        var buffer = new VirtualTerminalBuffer(80);
        var transcript = new StringBuilder();
        var parser = new VtSequenceParser(buffer, terminalTextHandler: c => transcript.Append(c));

        // PSReadLine writes the multi-line paste:
        //   1. Write all 3 lines to screen (cursor moves down)
        //   2. Move cursor up 2 lines (\e[2A)
        //   3. Erase and rewrite each line from top to bottom (\e[2K)
        // The transcript must capture text in the order it ARRIVES, not screen position order.
        parser.Feed("LINE_1\r\nLINE_2\r\nLINE_3");
        parser.Feed("\e[2A"); // cursor up 2 lines (cursor is at line 0, col after LINE_1)
        parser.Feed("\r\e[2KLINE_3\r\n"); // CR to col 0, erase + rewrite bottom
        parser.Feed("\r\e[2KLINE_2\r\n"); // CR to col 0, erase + rewrite middle
        parser.Feed("\r\e[2KLINE_1\r\n"); // CR to col 0, erase + rewrite top

        // The transcript should contain BOTH the initial write and the redraw,
        // in the order they arrived.
        var transcriptText = transcript.ToString();
        Assert.That(transcriptText, Does.Contain("LINE_1"),
            "Transcript must capture LINE_1 from initial write");
        Assert.That(transcriptText, Does.Contain("LINE_2"),
            "Transcript must capture LINE_2 from initial write");
        Assert.That(transcriptText, Does.Contain("LINE_3"),
            "Transcript must capture LINE_3 from initial write");

        // But: the virtual screen buffer after redraw should show the final state
        var screenText = buffer.GetText();
        Assert.That(screenText, Is.EqualTo("LINE_3\nLINE_2\nLINE_1"),
            "Virtual screen after PSReadLine bottom-up redraw shows correct final state");
    }

    [Test]
    public void GetTextBetween_PreservesOrder()
    {
        var buffer = new VirtualTerminalBuffer(80);
        var parser = new VtSequenceParser(buffer);
        parser.Feed("line0\r\nline1\r\nline2\r\nline3\r\nline4");

        var text = buffer.GetTextBetween(1, 3);
        Assert.That(text, Is.EqualTo("line1\nline2\nline3"),
            "GetTextBetween must return lines in forward order");
    }

    #endregion
}
