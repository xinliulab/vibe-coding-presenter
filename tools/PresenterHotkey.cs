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
// to false to go back to Ctrl+D. For modern AI apps, double ArrowDown can also
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
    private static bool _paused;

    private static int _upVk = VK_UP;
    private static int _downVk = VK_DOWN;
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

    [STAThread]
    private static void Main()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(exeDir, "PresenterHotkey.log");
        LoadConfig(Path.Combine(exeDir, "presenter-hotkey.json"));

        Log("Started low-level hook edition. up=0x" + _upVk.ToString("X2") +
            " (ChatGPT=>" + _upAction.Text + ", Codex hold=>" + _codexUpAction.Text +
            ", PRISM focus=>" + _prismUpAction.Text + ", Claude=>" +
            (_claudeUseWindowsVoiceTyping ? "voice typing " + _claudeVoiceTypingAction.Text : _claudeUpAction.Text) +
            ") down=0x" + _downVk.ToString("X2") +
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
        public TrayContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Presenter Hotkey - low-level hook") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            _pauseItem = new ToolStripMenuItem("Pause", null, OnPauseToggle);
            menu.Items.Add(_pauseItem);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.Dispose(disposing);
        }
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

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Pass through anything we injected ourselves.
        if ((info.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var vk = info.vkCode;
        if (vk != _upVk && vk != _downVk)
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
                ReleaseCodexHoldIfActive("Claude foreground");
                if (_claudeUseWindowsVoiceTyping)
                {
                    ToggleClaudeVoiceTyping(foreground);
                }
                else
                {
                    SendCombo(_claudeUpAction);
                    Log("Up double-press -> Claude " + _claudeUpAction.Text +
                        " (foreground=" + foreground + ", original arrows suppressed)");
                }
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
                Log("Down double-press -> " + _downAction.Text + " (original arrows suppressed)");
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

    private static void ToggleClaudeVoiceTyping(string foreground)
    {
        bool start;
        lock (Gate)
        {
            _claudeVoiceTypingActive = !_claudeVoiceTypingActive;
            start = _claudeVoiceTypingActive;
            if (start) _claudeVoiceHwnd = GetForegroundWindow();
        }

        // Win+H is itself a toggle: the same combo opens and closes voice typing.
        SendCombo(_claudeVoiceTypingAction);
        Log("Up double-press -> Claude Windows voice typing " + (start ? "START" : "STOP") +
            " via " + _claudeVoiceTypingAction.Text +
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
            if (map.ContainsKey("UpVk")) _upVk = ToInt(map["UpVk"], _upVk);
            if (map.ContainsKey("DownVk")) _downVk = ToInt(map["DownVk"], _downVk);
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
        }
        catch (Exception ex)
        {
            Log("Config load error (using defaults): " + ex.Message);
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
}
