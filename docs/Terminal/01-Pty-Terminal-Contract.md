# The PTY Terminal Contract

## Chapter 1: When a Shell Is Not Just a Process

### Background

Everywhere's terminal plugin executes user-approved shell scripts through a PTY instead of a plain redirected process. That choice is intentional. A shell running under a PTY behaves like the shell the user knows: prompts render normally, line editors such as PSReadLine/readline/zle load, multiline input works, and command output contains the same control sequences a real terminal would see.

The price is that a PTY is not only a byte pipe. Once we spawn `pwsh`, `zsh`, or `bash`, Everywhere becomes the terminal emulator on the other side of the connection. The child process writes terminal control sequences to stdout/stderr, and the terminal emulator must sometimes answer by writing bytes back to stdin.

The important direction is:

```text
shell stdout/stderr  -> terminal emulator parser
shell stdin          <- terminal replies and user/input injection
```

If Everywhere only reads output and writes commands, it is only half a terminal. That half-terminal can work for simple commands, but interactive shells and line editors eventually find the missing half.

---

### The Symptom: A 3 Second Detection Timeout That Was Not Really Idle

The terminal plugin currently starts a shell with shell integration installed, then calls `IExecuteStrategy.DetectStrategyAsync` to decide whether the session supports OSC 633 markers. When markers are present, we use `RichExecuteStrategy`; otherwise we fall back to `NoneExecuteStrategy`.

The rich path depends on these shell integration markers:

| Marker | Meaning |
| ------ | ------- |
| `OSC 633;A` | Prompt started |
| `OSC 633;B` | Prompt finished and command input is ready |
| `OSC 633;C` | Command execution started |
| `OSC 633;D;<exitCode>` | Command execution finished |
| `OSC 633;E;<commandLine>` | Command line reported |

The original detection window used a short idle timeout: if no marker appeared and the stream became quiet, Everywhere assumed shell integration was missing. On Windows this was often wrong. With PowerShell 7 from `C:\Program Files\PowerShell\7\pwsh.exe`, the first shell integration marker often arrived after about 3.8 seconds.

At first this looked like a slow `pwsh.exe` startup. Direct measurement disproved that:

```text
pwsh -NoLogo -NoProfile -NonInteractive -Command ...
Program Files pwsh: about 250ms
```

The real PTY path showed something different:

```text
first byte from PTY: about 0.01s
first OSC 633 marker without terminal reply: about 3.75s
first OSC 633 marker after replying to ESC[c: about 0.78s
```

PowerShell was not silent. It emitted early terminal control sequences almost immediately:

```text
ESC[1t ESC[c ESC[?1004h ESC[?9001h
```

The delay came from `ESC[c`.

---

### Device Attributes: The First Missing Terminal Reply

`ESC[c` is `CSI c`, also known as Primary Device Attributes (Primary DA). The application is asking the terminal to identify itself.

For a conservative Windows-compatible response, the terminal can reply:

```text
ESC[?1;0c
```

This means "VT101 with no options". It is deliberately modest: it does not claim sixel graphics, ReGIS, or advanced xterm features that Everywhere does not implement.

The crucial detail is that the reply must be written to the PTY input stream:

```text
child writes: ESC[c
terminal writes back: ESC[?1;0c
```

When Everywhere does not answer, PowerShell/PSReadLine waits until its own timeout path lets prompt rendering continue. That waiting period is what made shell integration detection look flaky.

This is not a PowerShell-only rule. Because Everywhere sets `TERM=xterm-256color`, programs are allowed to believe they are talking to an xterm-like terminal. `bash`, `zsh`, readline, zle, `less`, `vim`, `fzf`, prompt frameworks, and other TUIs may all send terminal queries.

If we claim to be a terminal, we need a terminal responder.

---

### The Terminal Responder Surface

The responder should sit in front of transcript extraction. It observes PTY output, updates the virtual terminal state, and replies to terminal queries before higher-level command detection decides whether output is complete.

The first useful response set is small:

| Sequence | Name | Behavior |
| -------- | ---- | -------- |
| `ESC[c`, `ESC[0c` | Primary DA | Reply `ESC[?1;0c` |
| `ESC[>c`, `ESC[>0c` | Secondary DA | Reply conservatively, for example `ESC[>0;0;0c` |
| `ESC[5n` | Device status report | Reply `ESC[0n` |
| `ESC[6n` | Cursor position report | Reply `ESC[{row};{col}R` using 1-based coordinates |
| `ESC[18t` | Text area size query | Reply `ESC[8;{rows};{cols}t` |
| `ESC[?1004h/l` | Focus event tracking | Track mode; optionally send `ESC[I` and `ESC[O` on focus changes |
| `ESC[?2004h/l` | Bracketed paste mode | Track mode and use it when injecting paste payloads |
| `ESC[?9001h/l` | Win32 input mode | Track mode; use only if we later synthesize real keyboard events |

Some output sequences are just requests to change terminal state and do not require a reply. For example, `ESC[1t` is an xterm window operation. In a headless PTY capture session, it can be safely ignored.

The responder must be a parser, not a string search. Control sequences can be split across reads:

```text
read 1: ESC[
read 2: ?2004h
```

The response to `ESC[6n` must also be based on the current virtual terminal cursor after all preceding output has been applied. That means the responder and `VirtualTerminalBuffer` need a shared view of terminal state.

---

### Shell Integration Is a Strategy, Not the Terminal

Everywhere has two execution strategies:

`RichExecuteStrategy` is used when OSC 633 shell integration is detected. It captures transcript text between `C` (command executed) and `D` (command finished), then waits for the next `A` (prompt start) to know the shell is ready again. This path is robust against prompt echoes, cursor rewrites, and many redraw artifacts.

`NoneExecuteStrategy` is the fallback. It waits for initial idle, sends the command line by line, watches for command echo and prompt-like lines, then uses `OutputCleaner` to strip the echo and trailing prompt. This path is necessary for shells without integration, but it is inherently heuristic.

Terminal emulation belongs below both strategies. Query replies, mode tracking, cursor state, and paste handling should not be special-cased inside rich detection. Rich detection consumes shell integration markers; the terminal responder handles the terminal contract that makes those markers arrive promptly.

The layering should be:

```text
PTY bytes
  -> VT/CSI/OSC parser
     -> update VirtualTerminalBuffer
     -> answer terminal queries
     -> track terminal modes
     -> emit OSC 633 marker events
     -> emit printable transcript text
  -> ExecuteStrategy
```

In the current implementation, that shared layer is `TerminalSession`. It owns the PTY connection, `PtyTextDecoder`, `VirtualTerminalBuffer`, `VtSequenceParser`, and `TerminalResponseWriter` for the lifetime of one command run. Detection and execution now receive the same session, so terminal modes discovered during startup are still the modes seen by command injection.

That removes an entire class of races:

```text
spawn PTY
  -> create TerminalSession
  -> detect shell integration with session.Parser
  -> execute command with the same session.Parser and session.Buffer
  -> render the same session.Buffer in the inline terminal block
```

`RichExecuteStrategy` no longer gets a copied `isBracketedPasteModeEnabled` boolean. It reads `session.Parser.IsBracketedPasteModeEnabled` at send time. If the shell toggles `?2004h/l` between detection and command injection, the sender follows the current terminal state.

The fallback path also changed. Because detection, execution, and UI rendering share one `VirtualTerminalBuffer`, strategies no longer reset the buffer before a command. Instead, fallback extraction records a command-start baseline and only reads buffer text from that point forward, then applies echo/prompt cleanup. The screen remains continuous for the user, while the model still receives cleaned command output.

---

### Bracketed Paste: A Mode With Input Semantics

`ESC[?2004h/l` enables or disables bracketed paste mode:

```text
ESC[?2004h  enable
ESC[?2004l  disable
```

When enabled, pasted text should be wrapped like this:

```text
ESC[200~<payload>ESC[201~
```

This matters for multiline commands. Without bracketed paste, a shell line editor may treat each newline as a separate Enter keypress and execute too early. With bracketed paste, the line editor knows the bytes are one paste payload.

PowerShell on Windows has an extra trap. PSReadLine's default key bindings include:

```text
Enter       -> AcceptLine
ShiftEnter  -> AddLine
CtrlEnter   -> InsertLineAbove
```

In the Windows ConPTY/PSReadLine path, newline bytes inside a paste can be interpreted like `Ctrl+Enter`. The default `Ctrl+Enter` binding inserts a line above the current line, which can reverse the apparent order of a multiline command.

Everywhere's PowerShell shell integration script already compensates for this by rebinding `Ctrl+Enter`:

```powershell
Set-PSReadLineKeyHandler -Chord Ctrl+Enter -ScriptBlock {
    [Microsoft.PowerShell.PSConsoleReadLine]::Insert("`n")
}
```

That fix is not optional decoration. It is part of the input contract for reliable PowerShell multiline execution.

The safe rules are:

1. Track `?2004h/l` as terminal state.
2. Use bracketed paste only when the shell/line editor has enabled it.
3. Normalize multiline paste payloads to `\n`.
4. Avoid sending bare `\r` or mixed `\r\n` inside the paste payload.
5. For PowerShell + PSReadLine, keep the `Ctrl+Enter` override so newline insertion is stable.
6. Send one final `\r` after the paste terminator only when the command should be submitted.

`RichExecuteStrategy` follows this rule: multiline commands use bracketed paste only when `session.Parser.IsBracketedPasteModeEnabled` is true at the moment the command is sent. If not, Rich logs the fallback and sends the command as Enter-separated lines so unsupported shells do not receive literal `ESC[200~` / `ESC[201~` markers.

For `bash` and `zsh`, bracketed paste generally maps more directly to readline/zle behavior. For `pwsh`, the PSReadLine handler is the guardrail that keeps multiline paste from turning into multiline editing commands.

---

### Win32 Input Mode Is Different From Bracketed Paste

`ESC[?9001h/l` is Windows Terminal's private Win32 input mode used by ConPTY. It asks the terminal to encode keyboard input as Win32-like key records rather than classic VT key sequences.

For command injection, Everywhere usually writes script text directly to stdin. In that case, it is enough to parse and track `?9001h/l` so the sequence does not leak into transcript text.

If Everywhere later supports fully interactive terminal UI input, this mode becomes more important. When enabled, synthesized keyboard events should follow the Win32 input encoding expected by ConPTY. Until then, the conservative design is:

```text
recognize ?9001h/l
track the mode
do not claim full interactive keyboard fidelity unless implemented
```

---

### Detection Should Not Confuse Startup Silence With Missing Integration

The original detection failure was caused by mixing two concepts:

1. Has the shell finished enough startup work to render a prompt?
2. Does the shell support OSC 633 shell integration?

Those are different questions.

Before deciding that shell integration is absent, the detector should first let the terminal responder satisfy early terminal queries. If the child has emitted terminal queries or mode changes, the session is alive even if no marker has arrived yet.

A more robust detector can use phased logic:

```text
Phase 1: spawn shell and start terminal responder
Phase 2: wait for first prompt/integration marker or a startup ceiling
Phase 3: if OSC 633 was observed, use RichExecuteStrategy
Phase 4: otherwise fall back to NoneExecuteStrategy
```

The startup ceiling can be larger than the idle timeout because shell startup is not command completion. Once the shell is ready and query replies work, command execution can use much shorter idle windows again.

With terminal query replies in the shared session, the Program Files PowerShell case stops looking like a slow executable. PowerShell asks for terminal identity early, Everywhere answers immediately, and OSC 633 prompt markers can arrive in roughly the same range as normal shell startup instead of waiting for PSReadLine's query timeout.

---

### Conservative Terminal Identity

A terminal emulator should not over-advertise. If Everywhere responds like a full xterm with advanced graphics, alternate keyboard protocols, or mouse features it does not actually implement, applications may select code paths that break later.

The right posture is:

```text
Implement the common core.
Reply quickly.
Track modes accurately.
Advertise only what we can honor.
```

`ESC[?1;0c` is a good initial Primary DA response because it unblocks programs that simply need confirmation that a terminal exists, without promising optional capabilities.

---

### Current Architecture Map

The terminal plugin is currently organized around these pieces:

| Component | Responsibility |
| --------- | -------------- |
| `TerminalPlugin` | Detect shell, spawn PTY, install shell integration environment, create `TerminalSession`, append the inline terminal block, select execution strategy |
| `TerminalSession` | Own one PTY conversation: connection, decoder, parser, virtual buffer, response writer, input writes, resize, and buffer change notifications |
| `TerminalDimensions` | Carry terminal rows/columns from PTY options now, and from UI measurement later |
| `ShellIntegrationScript` | Build shell args and environment; deploy wrapper behavior for PowerShell, zsh, and bash |
| `shellIntegration.ps1` | Emit OSC 633 markers, wrap PSReadLine, disable history saving, fix PowerShell multiline paste |
| `shellIntegration.bash` / `shellIntegration.zsh` | Emit OSC 633 markers through shell hooks |
| `VtSequenceParser` | Parse ANSI/VT sequences, update the virtual buffer, answer terminal queries, track terminal modes, surface OSC 633 markers |
| `VirtualTerminalBuffer` | Maintain cursor and text grid for output extraction |
| `TerminalResponseWriter` | Buffer parser-produced terminal replies and write them back to the PTY input stream |
| `RichExecuteStrategy` | Use OSC 633 markers for precise transcript extraction |
| `NoneExecuteStrategy` | Use idle detection, prompt heuristics, and command echo stripping |
| `OutputCleaner` | Normalize raw terminal text into command output |
| `ChatPluginTerminalDisplayBlock` | Chat display model for live terminal text and input routing |
| `TerminalView` | First inline Avalonia terminal view; renders monospaced output and forwards text, Enter, Backspace, Ctrl+C, arrows, Delete, and paste to the session |

The first responder pass is intentionally conservative: it replies to DA/DSR/CPR/text-area-size queries and tracks `?1004`, `?2004`, and `?9001` without claiming a full xterm feature surface.

The inline terminal is not a persistent shell yet. The command tool still waits for completion and returns cleaned output to the model; when the command completes or is cancelled, the PTY is closed. During the run, however, the block renders the live shared buffer and can forward user input to the active session. That is the narrow first step toward a real interactive terminal without mixing persistent shell lifetime into this change.

---

### Design Principles Going Forward

1. A PTY session is a terminal conversation, not process redirection.
2. Shell integration is helpful metadata, not a substitute for terminal emulation.
3. The terminal parser must handle fragmented control sequences.
4. Query replies must be written to stdin promptly.
5. Cursor-position replies must reflect the current virtual buffer state.
6. Bracketed paste is input semantics, not just an output mode flag.
7. PowerShell multiline paste needs the PSReadLine `Ctrl+Enter` guardrail.
8. Detection timeouts should distinguish shell startup from command completion.
9. Capabilities should be advertised conservatively.
10. Rich and None execution should both sit above the same terminal contract.

---

### References

- [Microsoft Console Virtual Terminal Sequences](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)
- [XTerm Control Sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html)
- [Windows Terminal ConPTY Win32 Input Mode](https://github.com/microsoft/terminal/blob/main/doc/specs/%234999%20-%20Improved%20keyboard%20handling%20in%20Conpty.md)
