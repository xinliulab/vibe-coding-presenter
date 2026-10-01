# Vibe Coding Presenter

[English](#english) | [中文](#中文)

## English

Vibe Coding Presenter turns a cheap wireless presenter remote into a voice-first vibe coding controller for Windows: talk to your AI coding agent and send prompts from across the room, without touching the keyboard.

The core idea is simple: before coding agents and AI writing tools, the most important productivity shortcut was often `Ctrl+C` then `Ctrl+V`. In a voice-first AI workflow, the new high-frequency loop is becoming:

```text
start voice input -> speak -> stop voice input -> Enter
```

Or, in shortcut form:

```text
Ctrl+Shift+D + Enter
```

This project maps a presenter remote's Up/Down buttons into that loop, so you can talk to ChatGPT, Codex, Claude, or PRISM with much less mouse and keyboard use.

### What it does

Vibe Coding Presenter is a lightweight Windows tray app written in C#/.NET Framework. It listens for double-presses from a presenter remote that sends `ArrowUp` and `ArrowDown` keyboard events.

- Double Up starts or stops voice input.
- Double Down sends the message. In ChatGPT, Codex, Claude, and PRISM, the app first tries to click the real `Send` button through Windows UI Automation, then falls back to `Enter`.
- Single Up/Down presses are swallowed so the remote does not accidentally scroll pages or recall command history.
- The tray menu includes Pause, Restart hook, Open log file, and Exit.

### Supported workflows

#### ChatGPT web

When a ChatGPT browser tab is active:

- Double Up sends `Ctrl+Shift+D`.
- Double Down clicks the `Send` button, or falls back to `Enter`.

This matches ChatGPT web dictation, where `Ctrl+Shift+D` behaves like a start/stop voice shortcut.

#### Codex desktop app

Codex desktop uses `Ctrl+Shift+D` as a hold-to-dictate shortcut, not a normal toggle. Vibe Coding Presenter adapts to that:

- First Double Up presses and holds `Ctrl+Shift+D`.
- Second Double Up releases `Ctrl+Shift+D`.
- Double Down clicks the `Send` button, or falls back to `Enter`.

This makes Codex feel like a toggle-based voice workflow even though its native shortcut is hold-based.

Recent Codex desktop builds may identify their foreground process as `ChatGPT` instead of `Codex`. The default config treats both `codex` and `chatgpt` desktop processes as hold-to-dictate apps, while browser tabs are still handled as web pages.

#### PRISM web

PRISM's built-in voice mode may not always work reliably. For PRISM, Vibe Coding Presenter uses Windows voice typing:

- Double Up finds and focuses the `Ask anything` input box, then sends `Win+H`.
- Double Down clicks the `Send` button or falls back to `Enter`, waits for the assistant to finish, then clicks `Compile` to refresh the PDF pane.

The input focus uses Windows UI Automation instead of screen coordinates, so it is more robust across window sizes and display layouts.

The auto-compile delay is configurable. The default is 25 seconds:

```json
"PrismAutoCompileAfterEnter": true,
"PrismCompileDelayMs": 25000
```

#### Claude desktop app

Claude's built-in dictation is not very reliable on Windows, so Vibe Coding Presenter uses Windows voice typing instead when the Claude desktop app is active:

- First Double Up sends `Win+H` to open Windows voice typing. Speak, and the text goes into the Claude input box.
- Second Double Up sends `Win+H` again to stop voice typing, leaving the text in the box so you can review it.
- Double Down while voice typing is open stops it, waits briefly (`ClaudeVoiceStopBeforeSendMs`, default 800 ms) so the last phrase lands, then clicks `Send` or falls back to `Enter`.
- Double Down when voice typing is not open just clicks `Send` or falls back to `Enter`.

To go back to Claude's own dictation shortcut (`Ctrl+D`), set:

```json
"ClaudeUseWindowsVoiceTyping": false
```

Windows voice typing may hear you but insert nothing when a third-party IME such as Sogou Pinyin is active in the Claude window. Switch that window to an English keyboard (`Win+Space`) or Microsoft Pinyin before dictating.

This tool only runs on Windows. On a Mac, keep using Claude's built-in dictation directly.

By default, this Claude rule is desktop-app-only and will not trigger inside common browsers, even if a browser tab title contains the word "Claude".

### Installation

Clone or download this repository, then run PowerShell from the project folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-PresenterHotkey.ps1
```

The script uses the C# compiler already included with Windows/.NET Framework. It does not require installing the .NET SDK.

To enable start-at-login:

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-PresenterHotkey.ps1 -AtLogin
```

To stop:

```powershell
powershell -ExecutionPolicy Bypass -File .\Stop-PresenterHotkey.ps1
```

To uninstall the startup shortcut:

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-PresenterHotkey.ps1
```

### Configuration

On first run, the app creates:

```text
build\presenter-hotkey.json
```

It is copied from:

```text
config.sample.json
```

Edit the generated local config for your own machine. Do not commit your local `build\presenter-hotkey.json` if it contains machine-specific settings.

Sample behavior:

```json
{
  "UpVk": "0x26",
  "DownVk": "0x28",
  "UpAction": "Ctrl+Shift+D",
  "CodexUpAction": "Ctrl+Shift+D",
  "CodexHoldProcessNames": ["codex", "chatgpt"],
  "CodexHoldTitleContains": ["codex"],
  "ClaudeUpAction": "Ctrl+D",
  "ClaudeUseWindowsVoiceTyping": true,
  "ClaudeVoiceTypingAction": "Win+H",
  "ClaudeVoiceStopBeforeSendMs": 800,
  "DownAction": "Enter",
  "PrismWindowTitleContains": ["prism"],
  "ClaudeWindowTitleContains": ["claude"],
  "ClaudeDesktopOnly": true,
  "PrismInputNameContains": ["ask anything", "message", "prompt", "ask"],
  "PrismCompileButtonNameContains": ["compile"],
  "SmartSendForegroundContains": ["chatgpt", "codex", "claude", "prism"],
  "SmartSendButtonNameContains": ["send", "submit"],
  "SmartSendButtonNameExcludes": ["feedback", "share", "copy", "stop", "voice", "dictation"],
  "PrismUpAction": "Win+H",
  "PrismDownAction": "Enter",
  "PrismFocusDelayMs": 150,
  "PrismAutoCompileAfterEnter": true,
  "PrismCompileDelayMs": 25000,
  "DoublePressMs": 900,
  "ActionCooldownMs": 800
}
```

If PRISM has a different browser title on your machine, add another keyword to `PrismWindowTitleContains`.

### Notes and limitations

- Windows only.
- Designed for presenter remotes that emit `ArrowUp` and `ArrowDown`.
- While running, normal `ArrowUp` and `ArrowDown` keyboard events are intercepted globally. Use Pause or Exit from the tray menu when you need normal arrow-key navigation.
- If the remote suddenly starts scrolling again, right-click the tray icon and choose Restart hook.
- PRISM support depends on Windows UI Automation being able to see the input box.
- Windows voice typing (used for Claude and PRISM) may not insert text while a third-party IME such as Sogou Pinyin is active. Use an English keyboard or Microsoft Pinyin.

### Inspiration

This project is one small piece of a fun trend: turning everyday devices into vibe coding inputs.

Bilibili creator [林亦LYi](https://space.bilibili.com/4401694) tested eight unusual vibe coding input devices in the video [1元 vs 50万元 Vibe Coding：写代码可以多离谱？](https://www.bilibili.com/video/BV1kp8z6REGj/), and open-sourced the projects in [LYiHub/pub-ai-inputs](https://github.com/LYiHub/pub-ai-inputs): a Xiaomi TV remote, an Apple Watch, a PS5 DualSense controller, and even an AITO M9 (问界 M9) car used as a voice input for a coding agent.

Vibe Coding Presenter brings the same idea to the most ordinary office gadget there is: the presentation clicker already sitting in your bag. No extra hardware, no driver, just two buttons mapped to "talk" and "send".

### Why this matters

The point is not the remote itself. The point is the interface shift.

Copy/paste was the signature gesture of desktop productivity. Voice-controlled AI is creating a new signature gesture: open dictation, say the intent, send it. Small tools like this make that loop physical, fast, and shoulder-friendly.

---

## 中文

Vibe Coding Presenter 是一个 Windows 托盘小工具，可以把普通翻页激光笔/演示遥控器变成 vibe coding 语音控制器：不用碰键盘，站着、坐远一点也能直接对 AI 编程助手说话、发送指令。

核心想法很简单：在 Codex、ChatGPT 这类工具出现以前，最重要的生产力快捷键常常是 `Ctrl+C` 和 `Ctrl+V`。但在语音优先的 AI 工作流里，新的高频动作正在变成：

```text
启动语音输入 -> 说话 -> 停止语音输入 -> Enter 发送
```

也就是：

```text
Ctrl+Shift+D + Enter
```

这个项目把演示遥控器的上/下键映射成这个循环，让你可以用更少的鼠标和键盘操作来控制 ChatGPT、Codex、Claude 和 PRISM。

### 它做什么

Vibe Coding Presenter 是一个轻量的 Windows 托盘程序，用 C#/.NET Framework 编写。它监听遥控器发出的 `ArrowUp` 和 `ArrowDown` 键盘事件，并识别“双击”。

- 向上双击：启动或停止语音输入。
- 向下双击：发送消息。在 ChatGPT、Codex、Claude 和 PRISM 里，程序会先通过 Windows UI Automation 点击真正的 `Send` 按钮；如果找不到按钮，再回退到 `Enter`。
- 单次上/下键会被吞掉，避免网页滚动或 Codex 回放历史输入。
- 托盘菜单提供 Pause、Restart hook、Open log file 和 Exit。

### 支持的场景

#### ChatGPT 网页版

当前台是 ChatGPT 网页时：

- 向上双击发送 `Ctrl+Shift+D`。
- 向下双击点击 `Send` 按钮；如果找不到按钮，再回退到 `Enter`。

ChatGPT 网页里，`Ctrl+Shift+D` 可以作为听写/语音输入的开始与停止快捷键。

#### Codex 桌面应用

Codex 桌面应用里的 `Ctrl+Shift+D` 不是普通 toggle，而是 hold-to-dictate，也就是需要按住才持续听写。Vibe Coding Presenter 对它做了适配：

- 第一次向上双击：按住 `Ctrl+Shift+D`。
- 第二次向上双击：松开 `Ctrl+Shift+D`。
- 向下双击：点击 `Send` 按钮；如果找不到按钮，再回退到 `Enter`。

这样 Codex 用起来就像有了一个“按一下开始、再按一下结束”的语音开关。

最近的 Codex 桌面版可能会把前台进程显示成 `ChatGPT`，而不是 `Codex`。默认配置会把 `codex` 和 `chatgpt` 这两个桌面进程都当作 hold-to-dictate 应用处理；浏览器标签页仍然按网页版逻辑处理。

#### PRISM 网页版

PRISM 自带的 voice mode 有时不稳定，所以这里改用 Windows 自带语音输入：

- 向上双击：先找到并聚焦 `Ask anything` 输入框，然后发送 `Win+H`。
- 向下双击：点击 `Send` 按钮或回退到 `Enter`，等待 AI Assistant 完成修改后，自动点击 `Compile` 刷新右侧 PDF。

输入框定位使用 Windows UI Automation，而不是屏幕坐标，因此对窗口大小和多屏布局更稳。

自动 Compile 的等待时间可以配置。默认等待 25 秒：

```json
"PrismAutoCompileAfterEnter": true,
"PrismCompileDelayMs": 25000
```

#### Claude 桌面应用

Claude 自带的语音输入在 Windows 上效果不太好，所以当前台是 Claude 桌面应用时，改用 Windows 自带的语音输入：

- 第一次向上双击：发送 `Win+H`，打开 Windows 语音输入。说话时文字会直接输入到 Claude 的输入框里。
- 第二次向上双击：再发送一次 `Win+H`，结束语音输入，文字留在输入框里，可以先检查再发送。
- 语音输入还开着时向下双击：先结束语音输入，稍等一下（`ClaudeVoiceStopBeforeSendMs`，默认 800 毫秒）让最后一句落到输入框里，然后点击 `Send` 按钮；找不到按钮时回退到 `Enter`。
- 语音输入没开时向下双击：直接点击 `Send` 按钮或回退到 `Enter`。

如果想换回 Claude 自带的语音快捷键（`Ctrl+D`），把配置改成：

```json
"ClaudeUseWindowsVoiceTyping": false
```

如果 Claude 窗口里用的是搜狗拼音这类第三方输入法，Windows 语音输入可能能听到声音但打不出字。说话前先按 `Win+空格` 把这个窗口切到英文键盘或微软拼音即可。

这个工具只在 Windows 上运行。在 Mac 上直接使用 Claude 自带的语音输入即可。

默认情况下，这条 Claude 规则只针对桌面应用生效；如果普通浏览器网页标题里有 Claude，它不会触发，避免误把 Edge/Chrome 里的 `Ctrl+D` 变成收藏网页。

### 安装

下载或 clone 这个仓库后，在项目目录里运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-PresenterHotkey.ps1
```

脚本使用 Windows/.NET Framework 自带的 C# 编译器，不需要安装 .NET SDK。

如果希望开机自动启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\Start-PresenterHotkey.ps1 -AtLogin
```

停止程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\Stop-PresenterHotkey.ps1
```

删除开机启动快捷方式：

```powershell
powershell -ExecutionPolicy Bypass -File .\Uninstall-PresenterHotkey.ps1
```

### 配置

第一次运行后会生成：

```text
build\presenter-hotkey.json
```

它会从下面这个示例配置复制生成：

```text
config.sample.json
```

如果你需要按自己的电脑环境调整，请改本地生成的 `build\presenter-hotkey.json`。不要把自己的本地配置提交到 GitHub。

示例配置：

```json
{
  "UpVk": "0x26",
  "DownVk": "0x28",
  "UpAction": "Ctrl+Shift+D",
  "CodexUpAction": "Ctrl+Shift+D",
  "CodexHoldProcessNames": ["codex", "chatgpt"],
  "CodexHoldTitleContains": ["codex"],
  "ClaudeUpAction": "Ctrl+D",
  "ClaudeUseWindowsVoiceTyping": true,
  "ClaudeVoiceTypingAction": "Win+H",
  "ClaudeVoiceStopBeforeSendMs": 800,
  "DownAction": "Enter",
  "PrismWindowTitleContains": ["prism"],
  "ClaudeWindowTitleContains": ["claude"],
  "ClaudeDesktopOnly": true,
  "PrismInputNameContains": ["ask anything", "message", "prompt", "ask"],
  "PrismCompileButtonNameContains": ["compile"],
  "SmartSendForegroundContains": ["chatgpt", "codex", "claude", "prism"],
  "SmartSendButtonNameContains": ["send", "submit"],
  "SmartSendButtonNameExcludes": ["feedback", "share", "copy", "stop", "voice", "dictation"],
  "PrismUpAction": "Win+H",
  "PrismDownAction": "Enter",
  "PrismFocusDelayMs": 150,
  "PrismAutoCompileAfterEnter": true,
  "PrismCompileDelayMs": 25000,
  "DoublePressMs": 900,
  "ActionCooldownMs": 800
}
```

如果你的 PRISM 网页标题里没有 `prism`，可以把对应关键词加入 `PrismWindowTitleContains`。

### 注意事项

- 目前仅支持 Windows。
- 适合会发送 `ArrowUp` 和 `ArrowDown` 的演示遥控器。
- 程序运行时会全局拦截普通 `ArrowUp` 和 `ArrowDown`。如果你需要正常使用方向键，可以从托盘菜单 Pause 或 Exit。
- 如果遥控器突然又开始滚动网页，右键托盘图标，点击 Restart hook。
- PRISM 支持依赖 Windows UI Automation 能否识别页面输入框。
- Windows 语音输入（Claude 和 PRISM 用的就是它）在搜狗拼音等第三方输入法下可能打不出字，请切到英文键盘或微软拼音。

### 灵感来源

这个项目是一个有趣潮流里的一小块：把身边各种设备变成 vibe coding 的输入工具。

B 站 UP 主 [林亦LYi](https://space.bilibili.com/4401694) 在视频 [1元 vs 50万元 Vibe Coding：写代码可以多离谱？](https://www.bilibili.com/video/BV1kp8z6REGj/) 里测试了八种离谱的 vibe coding 输入设备，并把项目开源在 [LYiHub/pub-ai-inputs](https://github.com/LYiHub/pub-ai-inputs)：小米遥控器、Apple Watch、PS5 手柄，甚至把问界 M9 汽车变成了编程 agent 的语音输入。

Vibe Coding Presenter 把同样的思路用在最普通的办公小物上：你包里本来就有的那支翻页激光笔。不用额外硬件，不用装驱动，两个按键分别对应“说话”和“发送”。

### 为什么这件事重要

重点不是激光笔本身，而是交互方式正在改变。

复制粘贴曾经是桌面生产力的标志性动作。语音控制 AI 正在创造新的标志性动作：打开语音，说出意图，发送。像 Vibe Coding Presenter 这样的小工具，就是把这个循环变成一个更自然、更快速、也更保护肩膀的物理动作。
