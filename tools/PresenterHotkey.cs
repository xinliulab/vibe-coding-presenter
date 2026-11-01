// PresenterHotkey - low-level keyboard hook edition.
//
// This version intentionally does NOT use Raw Input device filtering. Windows'
// low-level keyboard hook cannot reliably expose the physical HID device, but it
// can suppress the original ArrowUp/ArrowDown keystrokes before Edge/ChatGPT see
// them. That is the behavior needed for the INPHIC presenter:
//
//   double ArrowUp   => Ctrl+Shift+D
//   double ArrowDown => Enter
//
// Single ArrowUp/ArrowDown presses are intentionally swallowed. The presenter
// is treated as a dedicated voice remote, and replaying single arrows causes
// Codex to recall previous composer history.
//
// ChatGPT web treats Ctrl+Shift+D as a toggle. Codex desktop treats
// Ctrl+Shift+D as a hold-to-dictate shortcut, so when Codex is foreground we
// keep Ctrl+Shift+D held until the next double ArrowUp.
//
// PRISM web has unreliable built-in voice mode, so when a PRISM browser tab is
// foreground we focus its "Ask anything" text box through UI Automation, then
// open Windows voice typing with Win+H.
//
// After sending a PRISM prompt, the assistant may edit LaTeX without refreshing
// the PDF pane. We can optionally wait, then invoke PRISM's Compile button.
//
// Claude desktop's built-in dictation (Ctrl+D) is weak on Windows, so by
// default double ArrowUp toggles Windows voice typing (Win+H) instead. Double
// ArrowDown while voice typing is open stops it first, waits for the last
// phrase to land in the composer, then sends. Set ClaudeUseWindowsVoiceTyping
// to false to go back to Ctrl+D.
//
// When some other app is foreground (mail, a PDF, ...), double ArrowUp jumps
// back to the AI agent used last (Claude or Codex, Claude by default), brings
// it to the front, and starts dictation there, so the mouse never has to go
// back to the input box. ChatGPT web tabs still get Ctrl+Shift+D directly.
//
// For modern AI apps, double ArrowDown can also
// click the real Send button via UI Automation before falling back to Enter.
// This survives app updates where synthetic Enter no longer submits.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Automation;

internal static class PresenterHotkey
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_UP = 0x26;
    private const int VK_DOWN = 0x28;
    private const int VK_PRIOR = 0x21; // PageUp
    private const int VK_NEXT = 0x22;  // PageDown
    private const int VK_LSHIFT = 0xA0;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_LMENU = 0xA4;
    private const int VK_LWIN = 0x5B;

    private const int INPUT_KEYBOARD = 1;
    private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int LLKHF_INJECTED = 0x10;
    private const int MAPVK_VK_TO_VSC = 0;

    private static string _logPath;
    private static readonly object Gate = new object();
    private static readonly LowLevelKeyboardProc KeyboardProc = HookCallback;

    private static IntPtr _hook = IntPtr.Zero;
    private static NotifyIcon _tray;
    private static ToolStripMenuItem _pauseItem;
    private static ToolStripMenuItem _claudeWindowsVoiceItem;
    private static ToolStripMenuItem _claudeBuiltInVoiceItem;
    private static string _configPath;
    private static bool _paused;

    private static int _upVk = VK_UP;
    private static int _downVk = VK_DOWN;
    // Presenters differ: some send ArrowUp/ArrowDown, others PageUp/PageDown.
    // Every key listed here counts as that presenter button.
    private static int[] _upVks = new[] { VK_UP, VK_PRIOR };
    private static int[] _downVks = new[] { VK_DOWN, VK_NEXT };
    private static int _doublePressMs = 900;
    private static int _actionCooldownMs = 800;
    private static KeyCombo _upAction = KeyCombo.Parse("Ctrl+Shift+D", null);
    private static KeyCombo _codexUpAction = KeyCombo.Parse("Ctrl+Shift+D", null);
    private static KeyCombo _downAction = KeyCombo.Parse("Enter", null);
    private static KeyCombo _prismUpAction = KeyCombo.Parse("Win+H", null);
    private static KeyCombo _prismDownAction = KeyCombo.Parse("Enter", null);
    private static KeyCombo _claudeUpAction = KeyCombo.Parse("Ctrl+D", null);
    private static KeyCombo _claudeVoiceTypingAction = KeyCombo.Parse("Win+H", null);
    private static string[] _prismTitleContains = new[] { "prism" };
    private static string[] _prismInputNameContains = new[] { "ask anything", "message", "prompt", "ask" };
    private static string[] _prismCompileButtonNameContains = new[] { "compile" };
    private static string[] _claudeTitleContains = new[] { "claude" };
    private static string[] _smartSendButtonNameContains = new[] { "send", "submit" };
    private static string[] _smartSendButtonNameExcludes = new[] { "feedback", "share", "copy", "stop", "voice", "dictation" };
    private static string[] _smartSendForegroundContains = new[] { "chatgpt", "codex", "claude", "prism" };
    private static string[] _codexHoldProcessNames = new[] { "codex", "chatgpt" };
    private static string[] _codexHoldTitleContains = new[] { "codex" };
    private static bool _claudeDesktopOnly = true;
    private static bool _claudeUseWindowsVoiceTyping = true;
    private static int _claudeVoiceStopBeforeSendMs = 800;
    private static string[] _claudeInputNameContains = new[] { "prompt", "prosemirror" };
    private static int _claudeFocusDelayMs = 150;
    private static string[] _claudeProcessNames = new[] { "claude" };
    private static string[] _claudeDictationStopButtonNames = new[] { "stop dictation" };
    private static int _claudeDictationMinWaitMs = 2000;
    private static int _claudeDictationSettleMs = 700;
    private static int _claudeDictationMaxWaitMs = 8000;
    private static string[] _chatGptWebTitleContains = new[] { "chatgpt" };
    private static bool _jumpToAgentFromOtherApps = true;
    private static int _prismFocusDelayMs = 150;
    private static bool _prismAutoCompileAfterEnter = true;
    private static int _prismCompileDelayMs = 25000;

    private static int _pendingVk;
    private static long _pendingAt;
    private static System.Threading.Timer _pendingTimer;
    private static long _lastActionAt;
    private static bool _upHeld;
    private static bool _downHeld;
    private static bool _codexHoldActive;
    private static bool _claudeVoiceTypingActive;
    private static IntPtr _claudeVoiceHwnd;
    private static bool _lastAgentWasCodex;
    private static int _lastClaudeDictationToggleTick;
    // Our own guess of whether Claude's dictation is running: each double Up
    // flips it. Claude's UI alone is ambiguous (the Send button is sometimes
    // visible mid-dictation), so this decides whether Down must stop first.
    private static bool _claudeDictating;
    private static int _lastHookKeyTick;
    private static int _lastRawKeyTick;
    private static int _lastWatchdogReinstallTick;

    [STAThread]
    private static void Main()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(exeDir, "PresenterHotkey.log");
        _configPath = Path.Combine(exeDir, "presenter-hotkey.json");
        LoadConfig(_configPath);

        Log("Started low-level hook edition. up=" + VkList(_upVks) +
            " (ChatGPT=>" + _upAction.Text + ", Codex hold=>" + _codexUpAction.Text +
            ", PRISM focus=>" + _prismUpAction.Text + ", Claude=>" +
            (_claudeUseWindowsVoiceTyping ? "voice typing " + _claudeVoiceTypingAction.Text : _claudeUpAction.Text) +
            ") down=" + VkList(_downVks) +
            " (=>" + _downAction.Text + ") doublePressMs=" + _doublePressMs +
            ". ArrowUp/ArrowDown are suppressed; single arrows are ignored.");

        if (!InstallHook("startup")) return;

        Application.EnableVisualStyles();
        using (var ctx = new TrayContext())
        {
            Application.Run(ctx);
        }

        ReleaseCodexHoldIfActive("application exit");

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        Log("Stopped.");
    }

    private sealed class TrayContext : ApplicationContext
    {
        private readonly RawKeyboardWatcher _rawWatcher;
        private readonly System.Windows.Forms.Timer _hookWatchdog;

        public TrayContext()
        {
            _rawWatcher = RawKeyboardWatcher.TryCreate();
            if (_rawWatcher != null)
            {
                _hookWatchdog = new System.Windows.Forms.Timer { Interval = 1000 };
                _hookWatchdog.Tick += delegate { CheckHookAlive(); };
                _hookWatchdog.Start();
            }

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Presenter Hotkey - low-level hook") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            _pauseItem = new ToolStripMenuItem("Pause", null, OnPauseToggle);
            menu.Items.Add(_pauseItem);

            var claudeVoice = new ToolStripMenuItem("Claude voice input");
            _claudeWindowsVoiceItem = new ToolStripMenuItem(
                "Windows voice typing (" + _claudeVoiceTypingAction.Text + ")", null,
                delegate { SetClaudeVoiceMode(true); });
            _claudeBuiltInVoiceItem = new ToolStripMenuItem(
                "Claude built-in dictation (" + _claudeUpAction.Text + ")", null,
                delegate { SetClaudeVoiceMode(false); });
            claudeVoice.DropDownItems.Add(_claudeWindowsVoiceItem);
            claudeVoice.DropDownItems.Add(_claudeBuiltInVoiceItem);
            menu.Items.Add(claudeVoice);
            UpdateClaudeVoiceMenu();

            menu.Items.Add(new ToolStripMenuItem("Restart hook", null, delegate { ReinstallHook("manual tray restart"); }));
            menu.Items.Add(new ToolStripMenuItem("Open log file", null, delegate { TryOpen(_logPath); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

            _tray = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "Presenter Hotkey",
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += OnPauseToggle;
            _tray.ShowBalloonTip(
                3500,
                "Presenter Hotkey running",
                "Double Up = voice toggle/hold/PRISM dictation. Double Down = Enter.",
                ToolTipIcon.Info);
        }

        private static void OnPauseToggle(object sender, EventArgs e)
        {
            bool nowPaused;
            lock (Gate)
            {
                _paused = !_paused;
                nowPaused = _paused;
                ClearPendingNoReplay();
            }
            if (nowPaused)
            {
                ReleaseCodexHoldIfActive("paused");
            }
            else
            {
                ReinstallHook("resume");
            }
            _pauseItem.Text = _paused ? "Resume" : "Pause";
            _tray.Text = _paused ? "Presenter Hotkey (paused)" : "Presenter Hotkey";
            Log(_paused ? "Paused." : "Resumed.");
        }

        private static void SetClaudeVoiceMode(bool useWindowsVoiceTyping)
        {
            if (_claudeUseWindowsVoiceTyping == useWindowsVoiceTyping) return;

            ForgetClaudeVoiceTyping("Claude voice mode changed");
            _claudeUseWindowsVoiceTyping = useWindowsVoiceTyping;
            UpdateClaudeVoiceMenu();
            SaveConfigBool("ClaudeUseWindowsVoiceTyping", useWindowsVoiceTyping);

            string mode = useWindowsVoiceTyping
                ? "Windows voice typing (" + _claudeVoiceTypingAction.Text + ")"
                : "Claude built-in dictation (" + _claudeUpAction.Text + ")";
            Log("Claude voice mode set to " + mode + " from tray menu.");
            _tray.ShowBalloonTip(2500, "Claude voice input", mode, ToolTipIcon.Info);
        }

        private static void UpdateClaudeVoiceMenu()
        {
            _claudeWindowsVoiceItem.Checked = _claudeUseWindowsVoiceTyping;
            _claudeBuiltInVoiceItem.Checked = !_claudeUseWindowsVoiceTyping;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (disposing && _hookWatchdog != null) _hookWatchdog.Dispose();
            if (disposing && _rawWatcher != null) _rawWatcher.DestroyHandle();
            base.Dispose(disposing);
        }
    }

    // Raw keyboard input reaches this hidden window for every keystroke, even
    // when our app is in the background, and unlike a low-level hook Windows
    // never silently drops it. That makes it a reliable witness for the hook.
    private sealed class RawKeyboardWatcher : NativeWindow
    {
        public static RawKeyboardWatcher TryCreate()
        {
            var watcher = new RawKeyboardWatcher();
            watcher.CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

            var devices = new RAWINPUTDEVICE[1];
            devices[0].usUsagePage = 0x01; // generic desktop
            devices[0].usUsage = 0x06;     // keyboard
            devices[0].dwFlags = RIDEV_INPUTSINK;
            devices[0].hwndTarget = watcher.Handle;
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                Log("Hook watchdog disabled: RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
                watcher.DestroyHandle();
                return null;
            }

            Log("Hook watchdog enabled (raw keyboard input).");
            return watcher;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) _lastRawKeyTick = Environment.TickCount;
            base.WndProc(ref m);
        }
    }

    // Windows 7+ silently removes a low-level hook whose callback ever runs
    // too long (easy to hit right after boot), and the arrows then leak into
    // apps again. If raw input saw a keystroke the hook never did, the hook is
    // gone, so put it back.
    private static void CheckHookAlive()
    {
        int raw = _lastRawKeyTick;
        if (raw == 0) return;
        int missedBy = unchecked(raw - _lastHookKeyTick);
        if (missedBy <= 500) return;

        int now = Environment.TickCount;
        if (_lastWatchdogReinstallTick != 0 && unchecked(now - _lastWatchdogReinstallTick) < 10000) return;
        _lastWatchdogReinstallTick = now;
        _lastHookKeyTick = now;
        ReinstallHook("watchdog: hook missed keystrokes for " + missedBy + "ms");
    }

    private static bool InstallHook(string reason)
    {
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, KeyboardProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Log("SetWindowsHookEx failed during " + reason + ": " + error);
            MessageBox.Show(
                "Presenter Hotkey could not install the keyboard hook. Error: " + error,
                "Presenter Hotkey",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        Log("Keyboard hook installed (" + reason + ").");
        return true;
    }

    private static void ReinstallHook(string reason)
    {
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log("Hook unhook exception during " + reason + ": " + ex.Message);
        }

        InstallHook(reason);
    }

    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        _lastHookKeyTick = Environment.TickCount;
        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Pass through anything we injected ourselves.
        if ((info.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        // Fold every configured key onto the canonical Up/Down code.
        int vk = Array.IndexOf(_upVks, info.vkCode) >= 0 ? _upVk
            : Array.IndexOf(_downVks, info.vkCode) >= 0 ? _downVk
            : 0;
        if (vk == 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var msg = wParam.ToInt32();
        if (_paused)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
        {
            HandleArrowKeyDown(vk);
            return (IntPtr)1; // suppress original ArrowUp/ArrowDown
        }

        if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
        {
            if (vk == _upVk) _upHeld = false;
            if (vk == _downVk) _downHeld = false;
            return (IntPtr)1; // suppress original key-up too
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void HandleArrowKeyDown(int vk)
    {
        int action = 0; // 1 = UpAction, 2 = DownAction

        lock (Gate)
        {
            if (vk == _upVk)
            {
                if (_upHeld) return; // ignore auto-repeat
                _upHeld = true;
            }
            else
            {
                if (_downHeld) return; // ignore auto-repeat
                _downHeld = true;
            }

            long now = Environment.TickCount;

            if (_pendingVk == vk && ElapsedMs(_pendingAt, now) <= _doublePressMs)
            {
                ClearPendingNoReplay();
                if (_lastActionAt == 0 || ElapsedMs(_lastActionAt, now) > _actionCooldownMs)
                {
                    _lastActionAt = now;
                    action = vk == _upVk ? 1 : 2;
                }
            }
            else
            {
                ClearPendingNoReplay();

                _pendingVk = vk;
                _pendingAt = now;
                _pendingTimer = new System.Threading.Timer(
                    delegate { FlushPending(); },
                    null,
                    _doublePressMs,
                    System.Threading.Timeout.Infinite);
            }
        }

        if (action != 0) QueueAction(action);
    }

    private static void QueueAction(int action)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ExecuteAction(action);
            }
            catch (Exception ex)
            {
                Log("Action worker exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
    }

    private static void ExecuteAction(int action)
    {
        if (action == 1)
        {
            var foreground = GetForegroundSummary();
            if (IsCodexForeground(foreground))
            {
                _lastAgentWasCodex = true;
                ForgetClaudeVoiceTyping("Codex foreground");
                ToggleCodexHold(foreground);
            }
            else if (IsPrismForeground(foreground))
            {
                ForgetClaudeVoiceTyping("PRISM foreground");
                ReleaseCodexHoldIfActive("PRISM foreground");
                StartPrismDictation(foreground);
            }
            else if (IsClaudeForeground(foreground) || IsClaudeVoiceTypingPanelForeground(foreground))
            {
                _lastAgentWasCodex = false;
                ReleaseCodexHoldIfActive("Claude foreground");
                StartClaudeDictation(foreground);
            }
            else if (_jumpToAgentFromOtherApps && !IsChatGptWebForeground(foreground))
            {
                JumpToAgentAndDictate(foreground);
            }
            else
            {
                ForgetClaudeVoiceTyping("non-Claude foreground");
                ReleaseCodexHoldIfActive("non-Codex foreground");
                SendCombo(_upAction);
                Log("Up double-press -> " + _upAction.Text + " (foreground=" + foreground +
                    ", original arrows suppressed)");
            }
        }
        else if (action == 2)
        {
            ReleaseCodexHoldIfActive("Down double-press before Enter");
            var foreground = GetForegroundSummary();
            IntPtr claudeHwnd = StopClaudeVoiceTypingIfActive(foreground);
            if (claudeHwnd != IntPtr.Zero)
            {
                SmartSendOrKey(claudeHwnd, "Claude voice typing", _downAction, foreground);
            }
            else if (!_claudeUseWindowsVoiceTyping && IsClaudeForeground(foreground))
            {
                SendClaudeMessage(GetForegroundWindow(), foreground);
            }
            else if (IsPrismForeground(foreground))
            {
                IntPtr hwnd = GetForegroundWindow();
                SmartSendOrKey(hwnd, "PRISM", _prismDownAction, foreground);
                SchedulePrismCompile(hwnd);
            }
            else if (IsSmartSendForeground(foreground))
            {
                IntPtr hwnd = GetForegroundWindow();
                SmartSendOrKey(hwnd, "smart-send", _downAction, foreground);
            }
            else
            {
                SendCombo(_downAction);
                Log("Down double-press -> " + _downAction.Text + " (foreground=" + foreground +
                    ", original arrows suppressed)");
            }
        }
    }

    private static int ElapsedMs(long start, long now)
    {
        return unchecked((int)(now - start));
    }

    private static void FlushPending()
    {
        lock (Gate)
        {
            ClearPendingNoReplay();
        }
    }

    private static void ClearPendingNoReplay()
    {
        if (_pendingTimer != null)
        {
            _pendingTimer.Dispose();
            _pendingTimer = null;
        }
        _pendingVk = 0;
        _pendingAt = 0;
    }

    private static string KeyName(int vk)
    {
        if (vk == _upVk) return "Up";
        if (vk == _downVk) return "Down";
        return "0x" + vk.ToString("X2");
    }

    private static bool IsCodexForeground()
    {
        return IsCodexForeground(GetForegroundSummary());
    }

    private static bool IsCodexForeground(string summary)
    {
        string process = GetForegroundProcessName(summary);
        if (ContainsAnyExact(process, _codexHoldProcessNames)) return true;
        if (IsBrowserForeground(summary)) return false;
        return ContainsAny(summary, _codexHoldTitleContains);
    }

    private static bool IsPrismForeground(string summary)
    {
        return ContainsAny(summary, _prismTitleContains);
    }

    private static bool IsClaudeForeground(string summary)
    {
        if (!ContainsAny(summary, _claudeTitleContains)) return false;
        if (!_claudeDesktopOnly) return true;
        return !IsBrowserForeground(summary);
    }

    // The Win+H panel lives in TextInputHost. If it ever takes the foreground
    // while we have it open for Claude, keep treating presses as Claude ones.
    private static bool IsClaudeVoiceTypingPanelForeground(string summary)
    {
        if (!_claudeVoiceTypingActive) return false;
        return GetForegroundProcessName(summary).Equals("TextInputHost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChatGptWebForeground(string summary)
    {
        return IsBrowserForeground(summary) && ContainsAny(summary, _chatGptWebTitleContains);
    }

    private static bool IsSmartSendForeground(string summary)
    {
        return ContainsAny(summary, _smartSendForegroundContains);
    }

    private static bool IsBrowserForeground(string summary)
    {
        string process = GetForegroundProcessName(summary);
        return process.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("opera", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetForegroundProcessName(string summary)
    {
        if (string.IsNullOrEmpty(summary)) return "";
        int slash = summary.IndexOf('/');
        return slash >= 0 ? summary.Substring(0, slash).Trim() : summary.Trim();
    }

    private static bool ContainsAny(string haystack, string[] needles)
    {
        if (string.IsNullOrEmpty(haystack) || needles == null) return false;
        foreach (var raw in needles)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (haystack.IndexOf(raw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static bool ContainsAnyExact(string value, string[] candidates)
    {
        if (string.IsNullOrEmpty(value) || candidates == null) return false;
        foreach (var raw in candidates)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            if (value.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string GetForegroundSummary()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "none";

            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            string processName = "pid:" + pid;
            try
            {
                var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch { }

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            return processName + " / " + title.ToString();
        }
        catch
        {
            return "unknown";
        }
    }

    // ---- Output ----
    private static void SmartSendOrKey(IntPtr hwnd, string label, KeyCombo fallback, string foreground)
    {
        bool clicked = TryClickSmartSendButton(hwnd);
        if (clicked)
        {
            Log("Down double-press -> " + label + " clicked Send button" +
                " (foreground=" + foreground + ", original arrows suppressed)");
            return;
        }

        SendCombo(fallback);
        Log("Down double-press -> " + label + " fallback " + fallback.Text +
            " (foreground=" + foreground + ", original arrows suppressed)");
    }

    private static bool TryClickSmartSendButton(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            AutomationElement button = FindSmartSendButton(root);
            if (button == null)
            {
                Log("Smart send failed: no Send button found.");
                return false;
            }

            object invokeObj;
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out invokeObj))
            {
                ((InvokePattern)invokeObj).Invoke();
                Log("Smart send clicked button: " + SafeAutomationText(button).Trim());
                return true;
            }

            button.SetFocus();
            SendCombo(KeyCombo.Parse("Enter", _downAction));
            Log("Smart send focused button and sent Enter: " + SafeAutomationText(button).Trim());
            return true;
        }
        catch (Exception ex)
        {
            Log("Smart send exception: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static AutomationElement FindSmartSendButton(AutomationElement root)
    {
        var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, buttonCondition);
        System.Windows.Rect rootRect = root.Current.BoundingRectangle;
        double minTop = rootRect.Top + Math.Max(120, rootRect.Height * 0.35);

        AutomationElement best = null;
        double bestScore = double.MinValue;

        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement e = all[i];
            if (!IsUsableAutomationElement(e)) continue;
            string text = SafeAutomationText(e);
            if (!ContainsAny(text, _smartSendButtonNameContains)) continue;
            if (ContainsAny(text, _smartSendButtonNameExcludes)) continue;

            System.Windows.Rect r = e.Current.BoundingRectangle;
            double bottomBias = r.Bottom - minTop;
            double rightBias = r.Right - rootRect.Left;
            double score = bottomBias * 10 + rightBias;

            if (score > bestScore)
            {
                best = e;
                bestScore = score;
            }
        }

        return best;
    }

    private static void StartPrismDictation(string foreground)
    {
        bool focused = TryFocusPrismInput();
        if (_prismFocusDelayMs > 0) Thread.Sleep(_prismFocusDelayMs);
        SendCombo(_prismUpAction);
        Log("Up double-press -> PRISM focus=" + focused + ", " + _prismUpAction.Text +
            " (foreground=" + foreground + ", original arrows suppressed)");
    }

    private static bool TryFocusPrismInput()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Log("PRISM focus failed: no foreground window.");
                return false;
            }

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
            {
                Log("PRISM focus failed: no automation root.");
                return false;
            }

            AutomationElement named = FindNamedPrismInput(root);
            if (TrySetAutomationFocus(named, "named input")) return true;

            AutomationElement bottomEdit = FindBottomEdit(root);
            if (TrySetAutomationFocus(bottomEdit, "bottom edit fallback")) return true;

            Log("PRISM focus failed: no matching editable element found.");
            return false;
        }
        catch (Exception ex)
        {
            Log("PRISM focus exception: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static void SchedulePrismCompile(IntPtr hwnd)
    {
        if (!_prismAutoCompileAfterEnter)
        {
            Log("PRISM auto-compile disabled.");
            return;
        }

        if (hwnd == IntPtr.Zero)
        {
            Log("PRISM auto-compile skipped: no foreground window handle.");
            return;
        }

        int delay = Math.Max(0, _prismCompileDelayMs);
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (delay > 0) Thread.Sleep(delay);
                bool clicked = TryClickPrismCompile(hwnd);
                Log("PRISM auto-compile after Enter: clicked=" + clicked +
                    " delayMs=" + delay);
            }
            catch (Exception ex)
            {
                Log("PRISM auto-compile exception: " + ex.GetType().Name + ": " + ex.Message);
            }
        });
    }

    private static bool TryClickPrismCompile(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
            {
                Log("PRISM compile failed: no automation root.");
                return false;
            }

            AutomationElement button = FindNamedButton(root, _prismCompileButtonNameContains);
            if (button == null)
            {
                Log("PRISM compile failed: no Compile button found.");
                return false;
            }

            object invokeObj;
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out invokeObj))
            {
                ((InvokePattern)invokeObj).Invoke();
                Log("PRISM clicked Compile button: " + SafeAutomationText(button).Trim());
                return true;
            }

            button.SetFocus();
            SendCombo(KeyCombo.Parse("Enter", _downAction));
            Log("PRISM focused Compile button and sent Enter: " + SafeAutomationText(button).Trim());
            return true;
        }
        catch (Exception ex)
        {
            Log("PRISM compile click exception: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static AutomationElement FindNamedButton(AutomationElement root, string[] nameContains)
    {
        var buttonCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, buttonCondition);
        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement e = all[i];
            if (!IsUsableAutomationElement(e)) continue;
            string text = SafeAutomationText(e);
            if (ContainsAny(text, nameContains)) return e;
        }
        return null;
    }

    private static AutomationElement FindNamedPrismInput(AutomationElement root)
    {
        var editable = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, editable);
        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement e = all[i];
            if (!IsUsableAutomationElement(e)) continue;
            string text = SafeAutomationText(e);
            if (ContainsAny(text, _prismInputNameContains)) return e;
        }
        return null;
    }

    private static AutomationElement FindBottomEdit(AutomationElement root)
    {
        var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, editCondition);
        System.Windows.Rect rootRect = root.Current.BoundingRectangle;
        double minTop = rootRect.Top + Math.Max(120, rootRect.Height * 0.35);
        AutomationElement best = null;
        double bestBottom = double.MinValue;

        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement e = all[i];
            if (!IsUsableAutomationElement(e)) continue;
            System.Windows.Rect r = e.Current.BoundingRectangle;
            if (r.Top < minTop) continue;
            if (r.Width < 80 || r.Height < 12) continue;
            if (r.Bottom > bestBottom)
            {
                best = e;
                bestBottom = r.Bottom;
            }
        }
        return best;
    }

    private static bool IsUsableAutomationElement(AutomationElement e)
    {
        if (e == null) return false;
        try
        {
            if (e.Current.IsOffscreen) return false;
            if (!e.Current.IsEnabled) return false;
            System.Windows.Rect r = e.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeAutomationText(AutomationElement e)
    {
        try
        {
            return (e.Current.Name ?? "") + " " +
                (e.Current.AutomationId ?? "") + " " +
                (e.Current.HelpText ?? "") + " " +
                (e.Current.ClassName ?? "");
        }
        catch
        {
            return "";
        }
    }

    private static bool TrySetAutomationFocus(AutomationElement e, string label)
    {
        if (e == null) return false;
        try
        {
            e.SetFocus();
            Log("PRISM focused " + label + ": " + SafeAutomationText(e).Trim());
            return true;
        }
        catch (Exception ex)
        {
            Log("PRISM focus failed for " + label + ": " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static void ToggleCodexHold(string foreground)
    {
        bool shouldPress;
        lock (Gate)
        {
            _codexHoldActive = !_codexHoldActive;
            shouldPress = _codexHoldActive;
        }

        if (shouldPress)
        {
            PressCombo(_codexUpAction);
            Log("Up double-press -> hold " + _codexUpAction.Text +
                " START (foreground=" + foreground + ", original arrows suppressed)");
        }
        else
        {
            ReleaseCombo(_codexUpAction);
            Log("Up double-press -> hold " + _codexUpAction.Text +
                " STOP (foreground=" + foreground + ", original arrows suppressed)");
        }
    }

    private static void StartClaudeDictation(string foreground)
    {
        if (_claudeUseWindowsVoiceTyping)
        {
            ToggleClaudeVoiceTyping(foreground);
            return;
        }

        SendCombo(_claudeUpAction);
        _lastClaudeDictationToggleTick = Environment.TickCount;
        _claudeDictating = !_claudeDictating;
        Log("Up double-press -> Claude " + _claudeUpAction.Text + (_claudeDictating ? " START" : " STOP") +
            " (foreground=" + foreground + ", original arrows suppressed)");
    }

    // Claude's own dictation streams a rough transcript while you talk, then
    // rewrites it with context about 0.5-1 s after dictation stops (often
    // English -> Chinese). While dictating, Claude hides its Send button and
    // Enter only stops dictation. So double Down stops dictation if needed,
    // waits for the rewrite, sends, and checks that the box really emptied.
    private static void SendClaudeMessage(IntPtr hwnd, string foreground)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            AutomationElement input = root == null ? null : FindClaudeInput(root);
            if (input == null)
            {
                SmartSendOrKey(hwnd, "Claude", _downAction, foreground);
                return;
            }

            string stoppedBy = null;
            AutomationElement stop = FindClaudeComposerButton(root, input, _claudeDictationStopButtonNames);
            if (stop != null)
            {
                stoppedBy = "clicked '" + stop.Current.Name + "'";
                object invokeObj;
                if (stop.TryGetCurrentPattern(InvokePattern.Pattern, out invokeObj))
                    ((InvokePattern)invokeObj).Invoke();
                else
                    SendCombo(_claudeUpAction);
            }
            else if (FindSmartSendButton(root) == null)
            {
                // Some sessions only paste the transcript in once dictation
                // stops, so an empty box can still mean "dictating". Enter on a
                // truly empty, idle box does nothing.
                SendCombo(_downAction);
                stoppedBy = "pressed " + _downAction.Text + " (Send hidden: dictating or empty box)";
            }
            else if (_claudeDictating)
            {
                // Send is visible, so Enter would send the rough draft; use the
                // same toggle that started dictation to stop it.
                SendCombo(_claudeUpAction);
                stoppedBy = "pressed " + _claudeUpAction.Text + " (dictation started by Up)";
            }
            else if (_lastClaudeDictationToggleTick != 0 &&
                ElapsedMs(_lastClaudeDictationToggleTick, Environment.TickCount) < 5000)
            {
                stoppedBy = "stopped by Up just before";
            }

            _claudeDictating = false;
            if (stoppedBy != null)
            {
                _lastClaudeDictationToggleTick = 0;
                WaitForClaudeRewrite(root, ref input, stoppedBy);
            }

            SmartSendOrKey(hwnd, "Claude", _downAction, foreground);
            EnsureClaudeSent(hwnd, root, ref input, foreground);
        }
        catch (Exception ex)
        {
            Log("Claude send exception: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void WaitForClaudeRewrite(AutomationElement root, ref AutomationElement input, string stoppedBy)
    {
        int start = Environment.TickCount;
        string lastText = ReadClaudeInputText(root, ref input);
        int lastChange = start;
        int changes = 0;
        while (ElapsedMs(start, Environment.TickCount) < _claudeDictationMaxWaitMs)
        {
            Thread.Sleep(100);
            string text = ReadClaudeInputText(root, ref input);
            if (text != lastText)
            {
                lastText = text;
                lastChange = Environment.TickCount;
                changes++;
            }

            // The transcript may land first and the context rewrite ~0.5-1 s
            // later, so never send before the minimum wait.
            if (input != null && FindClaudeComposerButton(root, input, _claudeDictationStopButtonNames) != null) continue;
            if (ElapsedMs(start, Environment.TickCount) >= _claudeDictationMinWaitMs &&
                ElapsedMs(lastChange, Environment.TickCount) >= _claudeDictationSettleMs) break;
        }

        Log("Claude dictation finished (" + stoppedBy + "): waited " + ElapsedMs(start, Environment.TickCount) +
            "ms, text changes=" + changes + ", last change at " + ElapsedMs(start, lastChange) + "ms");
    }

    private static void EnsureClaudeSent(IntPtr hwnd, AutomationElement root, ref AutomationElement input, string foreground)
    {
        int start = Environment.TickCount;
        while (ElapsedMs(start, Environment.TickCount) < 1500)
        {
            if (ReadClaudeInputText(root, ref input).Trim().Length == 0) return;
            Thread.Sleep(100);
        }

        Log("Claude input still has text 1.5s after send; sending again.");
        SmartSendOrKey(hwnd, "Claude retry", _downAction, foreground);
    }

    private static string ReadClaudeInputText(AutomationElement root, ref AutomationElement input)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (input == null) input = FindClaudeInput(root);
                if (input == null) return "";
                object valueObj;
                if (input.TryGetCurrentPattern(ValuePattern.Pattern, out valueObj))
                    return ((ValuePattern)valueObj).Current.Value ?? "";
                return "";
            }
            catch (ElementNotAvailableException)
            {
                input = null; // the editor re-rendered; look it up again
            }
        }
        return "";
    }

    // Only the strip of buttons along the bottom of the input box counts, so
    // chat history buttons whose names quote old messages never match.
    private static AutomationElement FindClaudeComposerButton(AutomationElement root, AutomationElement input, string[] nameContains)
    {
        System.Windows.Rect ir;
        try { ir = input.Current.BoundingRectangle; }
        catch (ElementNotAvailableException) { return null; }

        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, condition);
        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement e = all[i];
            try
            {
                if (e.Current.IsOffscreen) continue;
                System.Windows.Rect r = e.Current.BoundingRectangle;
                if (r.IsEmpty || r.Top < ir.Bottom - 60 || r.Top > ir.Bottom + 20) continue;
                if (r.Left < ir.Left - 80 || r.Right > ir.Right + 80) continue;
                if (ContainsAny(e.Current.Name, nameContains)) return e;
            }
            catch (ElementNotAvailableException)
            {
            }
        }
        return null;
    }

    private static void JumpToAgentAndDictate(string foreground)
    {
        ForgetClaudeVoiceTyping("jumping to agent from " + foreground);
        ReleaseCodexHoldIfActive("jumping to agent");

        bool codex = _lastAgentWasCodex;
        IntPtr hwnd = FindAgentWindow(codex);
        if (hwnd == IntPtr.Zero)
        {
            codex = !codex;
            hwnd = FindAgentWindow(codex);
        }
        if (hwnd == IntPtr.Zero)
        {
            Log("Up double-press -> no Claude or Codex window to jump to (foreground=" + foreground + ")");
            return;
        }

        bool activated = ActivateWindow(hwnd);
        string target = GetForegroundSummary();
        Log("Up double-press -> jump to " + (codex ? "Codex" : "Claude") + " activated=" + activated +
            " (from=" + foreground + ", now=" + target + ")");
        if (!activated) return;

        // Give the app a moment to restore focus to its input box.
        if (_claudeFocusDelayMs > 0) Thread.Sleep(_claudeFocusDelayMs);
        if (codex)
        {
            ToggleCodexHold(target);
        }
        else
        {
            StartClaudeDictation(target);
        }
    }

    // EnumWindows walks top-level windows in z-order, so the first match is the
    // agent window the user had in front most recently.
    private static IntPtr FindAgentWindow(bool codex)
    {
        IntPtr found = IntPtr.Zero;
        var processNames = new Dictionary<uint, string>();
        EnumWindows(delegate (IntPtr h, IntPtr lParam)
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            if (title.Length == 0) return true;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            string process;
            if (!processNames.TryGetValue(pid, out process))
            {
                try { process = Process.GetProcessById((int)pid).ProcessName; } catch { process = ""; }
                processNames[pid] = process;
            }

            bool match = codex
                ? ContainsAnyExact(process, _codexHoldProcessNames)
                : ContainsAnyExact(process, _claudeProcessNames) && ContainsAny(title.ToString(), _claudeTitleContains);
            if (!match) return true;

            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool ActivateWindow(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // Windows only lets the process behind the latest input take the
        // foreground. A synthetic Alt tap makes that us; it lands in the app
        // we are leaving, before the switch.
        if (!SetForegroundWindow(hwnd) || !WaitForForeground(hwnd))
        {
            var alt = new List<INPUT>(2);
            alt.Add(KeyInput(VK_LMENU, false));
            alt.Add(KeyInput(VK_LMENU, true));
            SendInputs("Alt tap for activation", alt);
            SetForegroundWindow(hwnd);
        }

        return WaitForForeground(hwnd);
    }

    private static bool WaitForForeground(IntPtr hwnd)
    {
        for (int i = 0; i < 20; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(25);
        }
        return GetForegroundWindow() == hwnd;
    }

    private static void ToggleClaudeVoiceTyping(string foreground)
    {
        bool start;
        IntPtr hwnd;
        lock (Gate)
        {
            _claudeVoiceTypingActive = !_claudeVoiceTypingActive;
            start = _claudeVoiceTypingActive;
            if (start) _claudeVoiceHwnd = GetForegroundWindow();
            hwnd = _claudeVoiceHwnd;
        }

        bool refocused = false;
        if (start)
        {
            refocused = RefreshClaudeInputFocus(hwnd);
            if (_claudeFocusDelayMs > 0) Thread.Sleep(_claudeFocusDelayMs);
        }

        // Win+H is itself a toggle: the same combo opens and closes voice typing.
        SendCombo(_claudeVoiceTypingAction);
        Log("Up double-press -> Claude Windows voice typing " + (start ? "START" : "STOP") +
            " via " + _claudeVoiceTypingAction.Text + (start ? ", refocused=" + refocused : "") +
            " (foreground=" + foreground + ", original arrows suppressed)");
    }

    // Returns the Claude window to send to if voice typing was open there and
    // has now been closed, or IntPtr.Zero if there was nothing to stop.
    private static IntPtr StopClaudeVoiceTypingIfActive(string foreground)
    {
        bool panelForeground = IsClaudeVoiceTypingPanelForeground(foreground);
        IntPtr hwnd;
        lock (Gate)
        {
            if (!_claudeVoiceTypingActive) return IntPtr.Zero;
            _claudeVoiceTypingActive = false;
            hwnd = _claudeVoiceHwnd;
            _claudeVoiceHwnd = IntPtr.Zero;
        }

        if (IsClaudeForeground(foreground))
        {
            hwnd = GetForegroundWindow();
        }
        else if (!panelForeground)
        {
            Log("Forgot Claude voice typing state (Down double-press outside Claude, foreground=" + foreground + ")");
            return IntPtr.Zero;
        }

        SendCombo(_claudeVoiceTypingAction);
        Log("Down double-press -> Claude Windows voice typing STOP via " + _claudeVoiceTypingAction.Text +
            ", waiting " + _claudeVoiceStopBeforeSendMs + "ms before send (foreground=" + foreground + ")");
        if (_claudeVoiceStopBeforeSendMs > 0) Thread.Sleep(_claudeVoiceStopBeforeSendMs);
        return hwnd;
    }

    // Windows voice typing targets whatever the last UIA focus event named, not
    // the live focus. After the mouse wanders to another monitor that can go
    // stale, so Win+H opens on the wrong screen and types nowhere even though
    // the caret is still in Claude. Bouncing focus off the page and back fires
    // a fresh focus event; ProseMirror restores its caret on refocus.
    private static bool RefreshClaudeInputFocus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return false;

            AutomationElement input = FindClaudeInput(root);
            if (input == null)
            {
                Log("Claude refocus failed: no input box found (focused=" + DescribeFocusedElement() + ")");
                return false;
            }

            string before = DescribeFocusedElement();
            AutomationElement page = FindAncestor(input, ControlType.Document, root);
            if (page != null)
            {
                try
                {
                    page.SetFocus();
                    Thread.Sleep(30);
                }
                catch (Exception ex)
                {
                    Log("Claude refocus: page focus failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            input.SetFocus();
            Log("Claude refocused input box (before=" + before + ")");
            return true;
        }
        catch (Exception ex)
        {
            Log("Claude refocus exception: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static AutomationElement FindClaudeInput(AutomationElement root)
    {
        var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
        AutomationElementCollection all = root.FindAll(TreeScope.Descendants, editCondition);
        AutomationElement best = null;
        double bestBottom = double.MinValue;

        for (int i = 0; i < all.Count; i++)
        {
            AutomationElement e = all[i];
            if (!IsUsableAutomationElement(e)) continue;
            if (!ContainsAny(SafeAutomationText(e), _claudeInputNameContains)) continue;
            if (e.Current.HasKeyboardFocus) return e;

            System.Windows.Rect r = e.Current.BoundingRectangle;
            if (r.Bottom > bestBottom)
            {
                best = e;
                bestBottom = r.Bottom;
            }
        }
        return best;
    }

    private static AutomationElement FindAncestor(AutomationElement e, ControlType type, AutomationElement stopAt)
    {
        TreeWalker walker = TreeWalker.ControlViewWalker;
        AutomationElement current = walker.GetParent(e);
        while (current != null && !Automation.Compare(current, stopAt))
        {
            if (current.Current.ControlType == type) return current;
            current = walker.GetParent(current);
        }
        return null;
    }

    private static string DescribeFocusedElement()
    {
        try
        {
            AutomationElement f = AutomationElement.FocusedElement;
            if (f == null) return "none";
            string process = "pid:" + f.Current.ProcessId;
            try { process = Process.GetProcessById(f.Current.ProcessId).ProcessName; } catch { }
            return process + " " + f.Current.ControlType.ProgrammaticName + " '" + f.Current.Name + "'";
        }
        catch
        {
            return "unknown";
        }
    }

    // Clears our Claude voice typing flag without sending Win+H, used when the
    // user has moved to another app and we can no longer tell the panel's state.
    private static void ForgetClaudeVoiceTyping(string reason)
    {
        bool wasActive;
        lock (Gate)
        {
            wasActive = _claudeVoiceTypingActive;
            _claudeVoiceTypingActive = false;
            _claudeVoiceHwnd = IntPtr.Zero;
        }

        if (wasActive) Log("Forgot Claude voice typing state (" + reason + ")");
    }

    private static void ReleaseCodexHoldIfActive(string reason)
    {
        bool shouldRelease;
        lock (Gate)
        {
            shouldRelease = _codexHoldActive;
            _codexHoldActive = false;
        }

        if (shouldRelease)
        {
            ReleaseCombo(_codexUpAction);
            Log("Released held " + _codexUpAction.Text + " (" + reason + ")");
        }
    }

    private static void SendCombo(KeyCombo combo)
    {
        if (combo == null || combo.Key == 0) return;
        var list = new List<INPUT>(8);
        foreach (var mod in combo.Mods) list.Add(KeyInput(mod, false));
        list.Add(KeyInput(combo.Key, false));
        list.Add(KeyInput(combo.Key, true));
        for (int i = combo.Mods.Length - 1; i >= 0; i--) list.Add(KeyInput(combo.Mods[i], true));
        SendInputs(combo.Text, list);
    }

    private static void PressCombo(KeyCombo combo)
    {
        if (combo == null || combo.Key == 0) return;
        var list = new List<INPUT>(8);
        foreach (var mod in combo.Mods) list.Add(KeyInput(mod, false));
        list.Add(KeyInput(combo.Key, false));
        SendInputs("hold-start " + combo.Text, list);
    }

    private static void ReleaseCombo(KeyCombo combo)
    {
        if (combo == null || combo.Key == 0) return;
        var list = new List<INPUT>(8);
        list.Add(KeyInput(combo.Key, true));
        for (int i = combo.Mods.Length - 1; i >= 0; i--) list.Add(KeyInput(combo.Mods[i], true));
        SendInputs("hold-stop " + combo.Text, list);
    }

    private static void SendInputs(string label, List<INPUT> inputs)
    {
        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        if (sent != (uint)inputs.Count)
        {
            Log("SendInput partial/fail for " + label + ": sent=" + sent +
                " expected=" + inputs.Count + " error=" + Marshal.GetLastWin32Error());
        }
    }

    private static INPUT KeyInput(int vk, bool keyUp)
    {
        uint scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        int flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static bool IsExtendedKey(int vk)
    {
        switch (vk)
        {
            case 0x21: case 0x22: case 0x23: case 0x24:
            case 0x25: case 0x26: case 0x27: case 0x28:
            case 0x2D: case 0x2E:
            case 0x5B: case 0x5C:
            case 0xA3: case 0xA5:
                return true;
            default:
                return false;
        }
    }

    // ---- Config / logging ----
    private static void LoadConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (map == null) return;
            if (map.ContainsKey("UpVks")) _upVks = ToIntArray(map["UpVks"], _upVks);
            if (map.ContainsKey("DownVks")) _downVks = ToIntArray(map["DownVks"], _downVks);
            // Older configs name a single key; keep it working alongside the list.
            if (map.ContainsKey("UpVk")) _upVks = WithKey(_upVks, ToInt(map["UpVk"], _upVk));
            if (map.ContainsKey("DownVk")) _downVks = WithKey(_downVks, ToInt(map["DownVk"], _downVk));
            _upVk = _upVks[0];
            _downVk = _downVks[0];
            if (map.ContainsKey("UpAction"))
                _upAction = KeyCombo.Parse(Convert.ToString(map["UpAction"], CultureInfo.InvariantCulture), _upAction);
            if (map.ContainsKey("CodexUpAction"))
                _codexUpAction = KeyCombo.Parse(Convert.ToString(map["CodexUpAction"], CultureInfo.InvariantCulture), _codexUpAction);
            if (map.ContainsKey("DownAction"))
                _downAction = KeyCombo.Parse(Convert.ToString(map["DownAction"], CultureInfo.InvariantCulture), _downAction);
            if (map.ContainsKey("PrismUpAction"))
                _prismUpAction = KeyCombo.Parse(Convert.ToString(map["PrismUpAction"], CultureInfo.InvariantCulture), _prismUpAction);
            if (map.ContainsKey("PrismDownAction"))
                _prismDownAction = KeyCombo.Parse(Convert.ToString(map["PrismDownAction"], CultureInfo.InvariantCulture), _prismDownAction);
            if (map.ContainsKey("ClaudeUpAction"))
                _claudeUpAction = KeyCombo.Parse(Convert.ToString(map["ClaudeUpAction"], CultureInfo.InvariantCulture), _claudeUpAction);
            if (map.ContainsKey("ClaudeVoiceTypingAction"))
                _claudeVoiceTypingAction = KeyCombo.Parse(Convert.ToString(map["ClaudeVoiceTypingAction"], CultureInfo.InvariantCulture), _claudeVoiceTypingAction);
            if (map.ContainsKey("PrismWindowTitleContains"))
                _prismTitleContains = ToStringArray(map["PrismWindowTitleContains"], _prismTitleContains);
            if (map.ContainsKey("ClaudeWindowTitleContains"))
                _claudeTitleContains = ToStringArray(map["ClaudeWindowTitleContains"], _claudeTitleContains);
            if (map.ContainsKey("PrismInputNameContains"))
                _prismInputNameContains = ToStringArray(map["PrismInputNameContains"], _prismInputNameContains);
            if (map.ContainsKey("PrismCompileButtonNameContains"))
                _prismCompileButtonNameContains = ToStringArray(map["PrismCompileButtonNameContains"], _prismCompileButtonNameContains);
            if (map.ContainsKey("SmartSendButtonNameContains"))
                _smartSendButtonNameContains = ToStringArray(map["SmartSendButtonNameContains"], _smartSendButtonNameContains);
            if (map.ContainsKey("SmartSendButtonNameExcludes"))
                _smartSendButtonNameExcludes = ToStringArray(map["SmartSendButtonNameExcludes"], _smartSendButtonNameExcludes);
            if (map.ContainsKey("SmartSendForegroundContains"))
                _smartSendForegroundContains = ToStringArray(map["SmartSendForegroundContains"], _smartSendForegroundContains);
            if (map.ContainsKey("CodexHoldProcessNames"))
                _codexHoldProcessNames = ToStringArray(map["CodexHoldProcessNames"], _codexHoldProcessNames);
            if (map.ContainsKey("CodexHoldTitleContains"))
                _codexHoldTitleContains = ToStringArray(map["CodexHoldTitleContains"], _codexHoldTitleContains);
            if (map.ContainsKey("DoublePressMs")) _doublePressMs = ToInt(map["DoublePressMs"], _doublePressMs);
            if (map.ContainsKey("ActionCooldownMs")) _actionCooldownMs = ToInt(map["ActionCooldownMs"], _actionCooldownMs);
            if (map.ContainsKey("PrismFocusDelayMs")) _prismFocusDelayMs = ToInt(map["PrismFocusDelayMs"], _prismFocusDelayMs);
            if (map.ContainsKey("PrismAutoCompileAfterEnter")) _prismAutoCompileAfterEnter = ToBool(map["PrismAutoCompileAfterEnter"], _prismAutoCompileAfterEnter);
            if (map.ContainsKey("PrismCompileDelayMs")) _prismCompileDelayMs = ToInt(map["PrismCompileDelayMs"], _prismCompileDelayMs);
            if (map.ContainsKey("ClaudeDesktopOnly")) _claudeDesktopOnly = ToBool(map["ClaudeDesktopOnly"], _claudeDesktopOnly);
            if (map.ContainsKey("ClaudeUseWindowsVoiceTyping")) _claudeUseWindowsVoiceTyping = ToBool(map["ClaudeUseWindowsVoiceTyping"], _claudeUseWindowsVoiceTyping);
            if (map.ContainsKey("ClaudeVoiceStopBeforeSendMs")) _claudeVoiceStopBeforeSendMs = ToInt(map["ClaudeVoiceStopBeforeSendMs"], _claudeVoiceStopBeforeSendMs);
            if (map.ContainsKey("ClaudeInputNameContains"))
                _claudeInputNameContains = ToStringArray(map["ClaudeInputNameContains"], _claudeInputNameContains);
            if (map.ContainsKey("ClaudeFocusDelayMs")) _claudeFocusDelayMs = ToInt(map["ClaudeFocusDelayMs"], _claudeFocusDelayMs);
            if (map.ContainsKey("ClaudeProcessNames"))
                _claudeProcessNames = ToStringArray(map["ClaudeProcessNames"], _claudeProcessNames);
            if (map.ContainsKey("ChatGptWebTitleContains"))
                _chatGptWebTitleContains = ToStringArray(map["ChatGptWebTitleContains"], _chatGptWebTitleContains);
            if (map.ContainsKey("JumpToAgentFromOtherApps")) _jumpToAgentFromOtherApps = ToBool(map["JumpToAgentFromOtherApps"], _jumpToAgentFromOtherApps);
            if (map.ContainsKey("ClaudeDictationStopButtonNames"))
                _claudeDictationStopButtonNames = ToStringArray(map["ClaudeDictationStopButtonNames"], _claudeDictationStopButtonNames);
            if (map.ContainsKey("ClaudeDictationMinWaitMs")) _claudeDictationMinWaitMs = ToInt(map["ClaudeDictationMinWaitMs"], _claudeDictationMinWaitMs);
            if (map.ContainsKey("ClaudeDictationSettleMs")) _claudeDictationSettleMs = ToInt(map["ClaudeDictationSettleMs"], _claudeDictationSettleMs);
            if (map.ContainsKey("ClaudeDictationMaxWaitMs")) _claudeDictationMaxWaitMs = ToInt(map["ClaudeDictationMaxWaitMs"], _claudeDictationMaxWaitMs);
        }
        catch (Exception ex)
        {
            Log("Config load error (using defaults): " + ex.Message);
        }
    }

    // Edits one boolean in place so the rest of the user's hand-edited config
    // (formatting, key order, unknown keys) survives.
    private static void SaveConfigBool(string key, bool value)
    {
        try
        {
            string text = File.Exists(_configPath) ? File.ReadAllText(_configPath) : "{\r\n}\r\n";
            string literal = value ? "true" : "false";
            var existing = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)\"?(true|false)\"?", RegexOptions.IgnoreCase);
            if (existing.IsMatch(text))
            {
                text = existing.Replace(text, "${1}" + literal, 1);
            }
            else
            {
                int brace = text.IndexOf('{');
                if (brace < 0) throw new InvalidDataException("config has no opening brace");
                string newline = text.Contains("\r\n") ? "\r\n" : "\n";
                bool empty = text.Substring(brace + 1).TrimStart().StartsWith("}");
                text = text.Insert(brace + 1, newline + "    \"" + key + "\": " + literal + (empty ? "" : ","));
            }

            File.WriteAllText(_configPath, text);
            Log("Saved " + key + "=" + literal + " to " + _configPath);
        }
        catch (Exception ex)
        {
            Log("Config save error for " + key + ": " + ex.Message);
        }
    }

    private static int ToInt(object value, int fallback)
    {
        try
        {
            var s = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return fallback;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return (int)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }
        catch { return fallback; }
    }

    private static int[] ToIntArray(object value, int[] fallback)
    {
        string[] items = ToStringArray(value, null);
        if (items == null) return fallback;
        var list = new List<int>();
        foreach (var item in items)
        {
            int vk = ToInt(item, 0);
            if (vk != 0 && !list.Contains(vk)) list.Add(vk);
        }
        return list.Count > 0 ? list.ToArray() : fallback;
    }

    private static int[] WithKey(int[] keys, int vk)
    {
        if (vk == 0 || Array.IndexOf(keys, vk) >= 0) return keys;
        var list = new List<int>(keys);
        list.Insert(0, vk);
        return list.ToArray();
    }

    private static string VkList(int[] keys)
    {
        var parts = new List<string>();
        foreach (int vk in keys) parts.Add("0x" + vk.ToString("X2"));
        return string.Join("/", parts.ToArray());
    }

    private static bool ToBool(object value, bool fallback)
    {
        try
        {
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            var s = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim();
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("1", StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("0", StringComparison.OrdinalIgnoreCase))
                return false;
            return fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string[] ToStringArray(object value, string[] fallback)
    {
        try
        {
            if (value == null) return fallback;
            string single = value as string;
            if (single != null)
            {
                var pieces = single.Split(new[] { '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                return TrimStrings(pieces, fallback);
            }

            var enumerable = value as IEnumerable;
            if (enumerable == null) return fallback;

            var list = new List<string>();
            foreach (var item in enumerable)
            {
                var s = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            }
            return list.Count > 0 ? list.ToArray() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string[] TrimStrings(string[] values, string[] fallback)
    {
        var list = new List<string>();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) list.Add(value.Trim());
        }
        return list.Count > 0 ? list.ToArray() : fallback;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + "  " + message + Environment.NewLine);
        }
        catch { }
    }

    private static void TryOpen(string path)
    {
        try { System.Diagnostics.Process.Start(path); } catch { }
    }

    private sealed class KeyCombo
    {
        public readonly string Text;
        public readonly int[] Mods;
        public readonly int Key;

        private KeyCombo(string text, int[] mods, int key)
        {
            Text = text;
            Mods = mods;
            Key = key;
        }

        public static KeyCombo Parse(string spec, KeyCombo fallback)
        {
            if (string.IsNullOrEmpty(spec)) return fallback;
            var parts = spec.Split('+');
            var mods = new List<int>();
            int key = 0;
            foreach (var rawPart in parts)
            {
                var p = rawPart.Trim();
                if (p.Length == 0) continue;
                int vk = NameToVk(p);
                if (vk == 0) return fallback;
                if (IsModifier(vk)) mods.Add(vk);
                else key = vk;
            }
            if (key == 0) return fallback;
            return new KeyCombo(spec.Trim(), mods.ToArray(), key);
        }

        private static bool IsModifier(int vk)
        {
            switch (vk)
            {
                case 0x10: case 0x11: case 0x12: case 0x5B:
                case 0xA0: case 0xA1: case 0xA2: case 0xA3: case 0xA4: case 0xA5:
                    return true;
                default:
                    return false;
            }
        }

        private static int NameToVk(string name)
        {
            string k = name.ToLowerInvariant();
            switch (k)
            {
                case "ctrl": case "control": return VK_LCONTROL;
                case "shift": return VK_LSHIFT;
                case "alt": case "menu": return VK_LMENU;
                case "win": case "lwin": case "super": case "meta": return VK_LWIN;
                case "enter": case "return": return 0x0D;
                case "tab": return 0x09;
                case "space": case "spacebar": return 0x20;
                case "esc": case "escape": return 0x1B;
                case "backspace": case "back": return 0x08;
                case "delete": case "del": return 0x2E;
                case "insert": case "ins": return 0x2D;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "pgup": return 0x21;
                case "pagedown": case "pgdn": return 0x22;
            }
            if (k.Length == 1)
            {
                char c = k[0];
                if (c >= 'a' && c <= 'z') return 0x41 + (c - 'a');
                if (c >= '0' && c <= '9') return 0x30 + (c - '0');
            }
            if (k.Length >= 2 && k[0] == 'f')
            {
                int n;
                if (int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 12) return 0x70 + (n - 1);
            }
            return 0;
        }
    }

    // ---- P/Invoke ----
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public int dwFlags;
        public int time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private const int WM_INPUT = 0x00FF;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    private const int GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
