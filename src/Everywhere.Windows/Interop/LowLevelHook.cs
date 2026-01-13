using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Serilog;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Callback for LowLevelHook. Return true to block the message.
/// </summary>
internal delegate void LowLevelHookHandler<T>(WINDOW_MESSAGE msg, ref T hookStruct, ref bool blockNext) where T : unmanaged;

/// <summary>
/// Manages low-level Windows hooks (Keyboard/Mouse) on a dedicated background thread to avoid blocking the UI thread.
/// </summary>
internal static class LowLevelHook
{
    private static readonly Lazy<HookThread> SharedThread = new(() => new HookThread());

    public static IDisposable CreateMouseHook(LowLevelHookHandler<MSLLHOOKSTRUCT> callback)
    {
        return new HookRunner<MSLLHOOKSTRUCT>(WINDOWS_HOOK_ID.WH_MOUSE_LL, callback, SharedThread.Value);
    }

    public static IDisposable CreateKeyboardHook(LowLevelHookHandler<KBDLLHOOKSTRUCT> callback)
    {
        return new HookRunner<KBDLLHOOKSTRUCT>(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, callback, SharedThread.Value);
    }

    /// <summary>
    /// A dedicated background thread that runs a Windows Message Loop required for Hooks.
    /// </summary>
    private class HookThread
    {
        private readonly Thread _thread;
        private uint _threadId;
        private readonly ManualResetEventSlim _started = new(false);
        private const uint WM_USER_RUN_ACTION = PInvoke.WM_USER + 114;

        // Queue to pass actions (Install/Uninstall) to the thread execution context
        private readonly ConcurrentQueue<Action> _actionQueue = new();

        public HookThread()
        {
            _thread = new Thread(ThreadProc)
            {
                IsBackground = true,
                Name = "LowLevelHookThread",
                Priority = ThreadPriority.Highest // Reduce latency for input hooks
            };
            _thread.SetApartmentState(ApartmentState.STA); // Hooks often work best in STA
            _thread.Start();
            _started.Wait();
        }

        private void ThreadProc()
        {
            _threadId = PInvoke.GetCurrentThreadId();

            // Create the message queue for this thread explicitly by calling Check or Peek
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

            _started.Set();

            while (true)
            {
                // GetMessage blocks until a message arrives
                var result = PInvoke.GetMessage(out var msg, HWND.Null, 0, 0);
                if (result == -1) break; // Error

                if (msg.message == WM_USER_RUN_ACTION)
                {
                    while (_actionQueue.TryDequeue(out var action))
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception ex)
                        {
                            Log.ForContext<HookThread>().Error(ex, "Error executing hook action");
                        }
                    }
                }
                else
                {
                    PInvoke.TranslateMessage(msg);
                    PInvoke.DispatchMessage(msg);
                }
            }
        }

        public void RunOnThread(Action action)
        {
            if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            {
                action();
            }
            else
            {
                _actionQueue.Enqueue(action);
                // Wake up the message loop
                var success = PInvoke.PostThreadMessage(_threadId, WM_USER_RUN_ACTION, 0, 0);
                if (!success)
                {
                    Log.ForContext<HookThread>().Error(
                        "Failed to post message to hook thread. Error: {ErrorCode}",
                        Marshal.GetLastWin32Error());
                }
            }
        }
    }

    /// <summary>
    /// The actual generic implementation of the hook.
    /// </summary>
    private class HookRunner<T> : IDisposable where T : unmanaged
    {
        private UnhookWindowsHookExSafeHandle? _hookHandle;
        private GCHandle _hookProcHandle;
        private readonly LowLevelHookHandler<T> _callback;
        private readonly HookThread _thread;
        private bool _disposed;

        public HookRunner(WINDOWS_HOOK_ID id, LowLevelHookHandler<T> callback, HookThread thread)
        {
            _callback = callback;
            _thread = thread;

            // Must install hook on the thread that runs the loop
            _thread.RunOnThread(() => Install(id));
        }

        private void Install(WINDOWS_HOOK_ID id)
        {
            if (_disposed) return;

            using var hModule = PInvoke.GetModuleHandle(null);
            var hookProc = new HOOKPROC(HookProc);
            _hookProcHandle = GCHandle.Alloc(hookProc);

            _hookHandle = PInvoke.SetWindowsHookEx(
                id,
                hookProc,
                hModule,
                0);
        }

        private unsafe LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
        {
            if (code < 0) return PInvoke.CallNextHookEx(null, code, wParam, lParam);

            ref var hookStruct = ref Unsafe.AsRef<T>(lParam.Value.ToPointer());
            var blockNext = false;

            // Note: This callback runs on the background HookThread!
            // Users should dispatch to UI thread if they need to update UI.
            _callback.Invoke((WINDOW_MESSAGE)wParam.Value, ref hookStruct, ref blockNext);

            return blockNext ? (LRESULT)1 : PInvoke.CallNextHookEx(null, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Must unhook on the thread that installed it
            _thread.RunOnThread(Uninstall);
            GC.SuppressFinalize(this);
        }

        private void Uninstall()
        {
            _hookHandle?.Dispose();
            if (_hookProcHandle.IsAllocated)
            {
                _hookProcHandle.Free();
            }
        }

        ~HookRunner()
        {
            Dispose();
        }
    }
}