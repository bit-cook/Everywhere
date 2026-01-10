using System.Diagnostics;


// Everywhere.Windows.UacLauncher
// -----------------------------------------------------------------------------------------
// This utility serves as a critical bridge for launching the main application 'Everywhere.exe'
// with elevated privileges (Administrator) from a scheduled task or a non-interactive context.
//
// THE PROBLEM (ERROR 1312 - ERROR_NO_SUCH_LOGON_SESSION):
// When a process is started directly by the Windows Task Scheduler with "Run with highest privileges",
// it often runs in an isolated Logon Session (S4U) that is detached from the user's interactive
// session required by DPAPI and the Windows Credential Manager. This causes operations like 
// CredWrite/CredRead to fail with error 1312, even if the user is technically logged in.
//
// THE SOLUTION (PROXY EXECUTION VIA SHELL):
// By running this lightweight launcher first, and then triggering the main executable via 
// 'ShellExecute' with the 'runas' verb:
// 1. We maintain the Administrator privileges (silent elevation, no UAC prompt if parent is admin).
// 2. Crucially, the process launch request is brokered by the Windows Shell (Explorer).
// 3. This re-anchors the new process to the correct Interactive Logon Session containing the 
//    user's Master Key, restoring full access to the Windows Credential Manager and UI resources.
// -----------------------------------------------------------------------------------------

if (Path.GetDirectoryName(Environment.ProcessPath) is not { Length: > 0 } currentDir)
{
    return;
}

Process.Start(new ProcessStartInfo
{
    FileName = Path.Combine(currentDir, "Everywhere.exe"),
    Arguments = string.Join(' ', args.Select(a => $"\"{a}\"")),
    UseShellExecute = true,
    Verb = "runas"
});