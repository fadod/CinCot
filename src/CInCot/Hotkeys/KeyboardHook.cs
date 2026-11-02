using System.ComponentModel;
using System.Runtime.InteropServices;
using CInCot.Capture;
using CInCot.Native;

namespace CInCot.Hotkeys;

/// <summary>
/// Low-level keyboard hook for the Print Screen key. It runs on its own thread with its own message loop,
/// so a busy UI thread can never make Windows time the hook out. Only VK_SNAPSHOT is inspected; every other
/// key is passed straight through and nothing is recorded.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly SynchronizationContext _ui;
    private Win32.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private uint _threadId;
    private bool _snapDown;
    private int _error;

    public KeyboardHook(SynchronizationContext ui) => _ui = ui;

    /// <summary>Raised on the UI thread when a capture hotkey is pressed.</summary>
    public event Action<CaptureMode>? Triggered;

    /// <summary>When false, Print Screen is passed through to Windows untouched.</summary>
    public bool Enabled { get; set; } = true;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _threadId = Win32.GetCurrentThreadId();
            _proc = HookProc;
            _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
            _error = _hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (Win32.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            Win32.UnhookWindowsHookEx(_hook);
        })
        {
            IsBackground = true,
            Name = "CInCot keyboard hook",
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        ready.Wait();
        if (_hook == IntPtr.Zero) throw new Win32Exception(_error, "Could not install the Print Screen keyboard hook.");
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            bool winKey = Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN);
            if (data.vkCode == Win32.VK_SNAPSHOT && (data.flags & Win32.LLKHF_INJECTED) == 0 && !winKey)
            {
                int msg = (int)wParam;
                if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
                {
                    if (!_snapDown)
                    {
                        _snapDown = true;
                        var mode = CurrentMode();
                        _ui.Post(_ => Triggered?.Invoke(mode), null);
                    }
                    return 1;
                }
                if (msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP)
                {
                    _snapDown = false;
                    return 1;
                }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static CaptureMode CurrentMode()
    {
        if (Win32.IsKeyDown(Win32.VK_CONTROL)) return CaptureMode.FullScreen;
        if (Win32.IsKeyDown(Win32.VK_MENU)) return CaptureMode.ActiveWindow;
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) return CaptureMode.Scroll;
        return CaptureMode.Region;
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
    }
}
