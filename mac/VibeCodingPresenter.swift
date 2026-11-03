// Vibe Coding Presenter for macOS: a menu bar app that turns a presenter remote's
// Up/Down buttons into "start/stop Claude dictation" and "send".
//
// How it works:
//  1. Presenters are found automatically: any external keyboard-type device whose
//     name does not say it is a keyboard (presenter receivers often call themselves
//     "2.4G Mouse" or "USB Receiver"). PresenterDevices and IgnoreDevices in the
//     config, or the Presenters menu, override the guess.
//  2. For presenters only (matched by VendorID/ProductID), hidutil remaps their
//     Up/Down (and PageUp/PageDown) keys to F18/F19. The built-in keyboard's
//     arrow keys are untouched.
//  3. A CGEvent tap swallows F18/F19 and detects double presses.
//  4. Double Up toggles Claude's dictation (Cmd+D), jumping to Claude first when
//     another app is in front. Double Down stops dictation if it is running,
//     waits for the last words to land, then presses Enter.
//
// Build and install with mac/install.sh. Run with --list-devices to see which
// devices are treated as presenters.

import AppKit
import Carbon.HIToolbox
import IOKit

// MARK: - Paths and logging

let appName = "VibeCodingPresenter"
let home = FileManager.default.homeDirectoryForCurrentUser
let supportDir = home.appendingPathComponent("Library/Application Support/\(appName)")
let configURL = supportDir.appendingPathComponent("config.json")
let logURL = home.appendingPathComponent("Library/Logs/\(appName).log")

let timestampFormatter: DateFormatter = {
    let formatter = DateFormatter()
    formatter.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
    return formatter
}()

func log(_ message: String) {
    let line = "\(timestampFormatter.string(from: Date())) \(message)\n"
    guard let data = line.data(using: .utf8) else { return }
    FileHandle.standardError.write(data)
    if let handle = try? FileHandle(forWritingTo: logURL) {
        handle.seekToEndOfFile()
        handle.write(data)
        try? handle.close()
    } else {
        try? data.write(to: logURL)
    }
}

func milliseconds(_ ms: Int) -> DispatchTimeInterval { .milliseconds(ms) }

@discardableResult
func run(_ path: String, _ arguments: [String]) -> (status: Int32, output: String) {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: path)
    process.arguments = arguments
    let pipe = Pipe()
    process.standardOutput = pipe
    process.standardError = pipe
    do {
        try process.run()
    } catch {
        return (-1, "\(error)")
    }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    return (process.terminationStatus, String(data: data, encoding: .utf8) ?? "")
}

// MARK: - Config

struct DeviceID: Hashable {
    let vendorID: Int
    let productID: Int

    var description: String { String(format: "VendorID 0x%04X, ProductID 0x%04X", vendorID, productID) }
    var shortDescription: String { String(format: "0x%04X:0x%04X", vendorID, productID) }
}

struct KeyCombo {
    let keyCode: CGKeyCode
    let flags: CGEventFlags
    let text: String
}

func parseInt(_ value: Any?) -> Int? {
    if let number = value as? NSNumber { return number.intValue }
    if let text = value as? String {
        let trimmed = text.trimmingCharacters(in: .whitespaces).lowercased()
        return trimmed.hasPrefix("0x") ? Int(trimmed.dropFirst(2), radix: 16) : Int(trimmed)
    }
    return nil
}

let namedKeyCodes: [String: Int] = [
    "A": kVK_ANSI_A, "B": kVK_ANSI_B, "C": kVK_ANSI_C, "D": kVK_ANSI_D, "E": kVK_ANSI_E,
    "F": kVK_ANSI_F, "G": kVK_ANSI_G, "H": kVK_ANSI_H, "I": kVK_ANSI_I, "J": kVK_ANSI_J,
    "K": kVK_ANSI_K, "L": kVK_ANSI_L, "M": kVK_ANSI_M, "N": kVK_ANSI_N, "O": kVK_ANSI_O,
    "P": kVK_ANSI_P, "Q": kVK_ANSI_Q, "R": kVK_ANSI_R, "S": kVK_ANSI_S, "T": kVK_ANSI_T,
    "U": kVK_ANSI_U, "V": kVK_ANSI_V, "W": kVK_ANSI_W, "X": kVK_ANSI_X, "Y": kVK_ANSI_Y,
    "Z": kVK_ANSI_Z,
    "ENTER": kVK_Return, "RETURN": kVK_Return, "SPACE": kVK_Space, "TAB": kVK_Tab,
    "ESC": kVK_Escape, "ESCAPE": kVK_Escape,
]

// Parses shortcuts such as "Cmd+D", "Ctrl+Shift+D" or "Enter".
func parseCombo(_ text: String) -> KeyCombo? {
    var flags: CGEventFlags = []
    var keyCode: Int?
    for part in text.split(separator: "+").map({ $0.trimmingCharacters(in: .whitespaces).uppercased() }) {
        switch part {
        case "CMD", "COMMAND": flags.insert(.maskCommand)
        case "CTRL", "CONTROL": flags.insert(.maskControl)
        case "ALT", "OPT", "OPTION": flags.insert(.maskAlternate)
        case "SHIFT": flags.insert(.maskShift)
        default:
            guard keyCode == nil, let code = namedKeyCodes[part] else { return nil }
            keyCode = code
        }
    }
    guard let code = keyCode else { return nil }
    return KeyCombo(keyCode: CGKeyCode(code), flags: flags, text: text)
}

func parseDeviceList(_ value: Any?) -> [DeviceID] {
    (value as? [[String: Any]] ?? []).compactMap { entry in
        guard let vendor = parseInt(entry["VendorID"]), let product = parseInt(entry["ProductID"]) else { return nil }
        return DeviceID(vendorID: vendor, productID: product)
    }
}

struct Config {
    var presenters: [DeviceID] = []
    var ignoredDevices: [DeviceID] = []
    var autoDetectPresenters = true
    var keyboardNameKeywords = ["keyboard", "keys", "kbd"]
    var agentBundleIDs = ["com.anthropic.claudefordesktop"]
    var dictationShortcut = "Cmd+D"
    var sendKey = "Enter"
    var jumpToAgentFromOtherApps = true
    var requireDoublePress = true
    var doublePressMs = 900
    var actionCooldownMs = 800
    var activateTimeoutMs = 2000
    var focusDelayMs = 300
    var stopBeforeSendMs = 1500
    var dictationStateTimeoutSec = 600

    static func load() -> Config {
        var config = Config()
        guard let data = try? Data(contentsOf: configURL),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            log("Config missing or unreadable at \(configURL.path); using defaults")
            return config
        }
        config.presenters = parseDeviceList(json["PresenterDevices"])
        config.ignoredDevices = parseDeviceList(json["IgnoreDevices"])
        if let value = json["AutoDetectPresenters"] as? Bool { config.autoDetectPresenters = value }
        if let words = json["KeyboardNameKeywords"] as? [String] { config.keyboardNameKeywords = words }
        if let ids = json["AgentBundleIDs"] as? [String], !ids.isEmpty { config.agentBundleIDs = ids }
        if let value = json["DictationShortcut"] as? String { config.dictationShortcut = value }
        if let value = json["SendKey"] as? String { config.sendKey = value }
        if let value = json["JumpToAgentFromOtherApps"] as? Bool { config.jumpToAgentFromOtherApps = value }
        if let value = json["RequireDoublePress"] as? Bool { config.requireDoublePress = value }
        if let value = parseInt(json["DoublePressMs"]) { config.doublePressMs = value }
        if let value = parseInt(json["ActionCooldownMs"]) { config.actionCooldownMs = value }
        if let value = parseInt(json["ActivateTimeoutMs"]) { config.activateTimeoutMs = value }
        if let value = parseInt(json["FocusDelayMs"]) { config.focusDelayMs = value }
        if let value = parseInt(json["StopBeforeSendMs"]) { config.stopBeforeSendMs = value }
        if let value = parseInt(json["DictationStateTimeoutSec"]) { config.dictationStateTimeoutSec = value }
        return config
    }

    // The lists in the config win. Otherwise any external keyboard-type device counts as a
    // presenter unless its name says it is a real keyboard. A wrong guess on a mouse receiver
    // is harmless: the remap only changes arrow and page keys, which a mouse never sends.
    func isPresenter(_ device: HIDDeviceInfo) -> Bool {
        if ignoredDevices.contains(device.id) { return false }
        if presenters.contains(device.id) { return true }
        guard autoDetectPresenters, device.isKeyboard, !device.builtIn else { return false }
        let name = device.product.lowercased()
        return !keyboardNameKeywords.contains { !$0.isEmpty && name.contains($0.lowercased()) }
    }
}

// Applies a change to config.json, keeping every other setting as it is.
@discardableResult
func updateConfigFile(_ change: (inout [String: Any]) -> Void) -> Bool {
    var json: [String: Any] = [:]
    if let data = try? Data(contentsOf: configURL),
       let existing = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
        json = existing
    }
    change(&json)
    do {
        try FileManager.default.createDirectory(at: supportDir, withIntermediateDirectories: true)
        let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try data.write(to: configURL)
        return true
    } catch {
        log("Could not write \(configURL.path): \(error)")
        return false
    }
}

// Returns a PresenterDevices/IgnoreDevices list with the device added or removed.
func deviceList(_ value: Any?, _ device: HIDDeviceInfo, include: Bool) -> [[String: Any]] {
    var list = (value as? [[String: Any]] ?? []).filter {
        parseInt($0["VendorID"]) != device.vendorID || parseInt($0["ProductID"]) != device.productID
    }
    if include {
        list.append(["VendorID": String(format: "0x%04X", device.vendorID),
                     "ProductID": String(format: "0x%04X", device.productID),
                     "Name": device.product])
    }
    return list
}

// MARK: - HID devices and hidutil remapping

struct HIDDeviceInfo {
    let vendorID: Int
    let productID: Int
    var product: String
    var transport: String
    var isKeyboard: Bool
    var builtIn: Bool

    var id: DeviceID { DeviceID(vendorID: vendorID, productID: productID) }

    // A receiver often shows up as several HID devices (keyboard, mouse, media keys) with the
    // same IDs; together they count as a keyboard if any part is one.
    func merged(with other: HIDDeviceInfo) -> HIDDeviceInfo {
        var result = self
        if product == "(unnamed)" { result.product = other.product }
        if transport.isEmpty { result.transport = other.transport }
        result.isKeyboard = isKeyboard || other.isKeyboard
        result.builtIn = builtIn || other.builtIn
        return result
    }
}

func registryValue(_ entry: io_registry_entry_t, _ key: String) -> Any? {
    IORegistryEntryCreateCFProperty(entry, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue()
}

func hidDeviceInfo(_ entry: io_registry_entry_t) -> HIDDeviceInfo? {
    guard let vendor = parseInt(registryValue(entry, "VendorID")),
          let product = parseInt(registryValue(entry, "ProductID")) else { return nil }
    var usages = (registryValue(entry, "DeviceUsagePairs") as? [[String: Any]] ?? []).map {
        (parseInt($0["DeviceUsagePage"]) ?? 0, parseInt($0["DeviceUsage"]) ?? 0)
    }
    usages.append((parseInt(registryValue(entry, "PrimaryUsagePage")) ?? 0, parseInt(registryValue(entry, "PrimaryUsage")) ?? 0))
    return HIDDeviceInfo(
        vendorID: vendor,
        productID: product,
        product: registryValue(entry, "Product") as? String ?? "(unnamed)",
        transport: registryValue(entry, "Transport") as? String ?? "",
        isKeyboard: usages.contains { $0 == (1, 6) },
        builtIn: (registryValue(entry, "Built-In") as? NSNumber)?.boolValue ?? false)
}

func listHIDDevices() -> [HIDDeviceInfo] {
    var iterator: io_iterator_t = 0
    guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOHIDDevice"), &iterator) == KERN_SUCCESS else {
        return []
    }
    defer { IOObjectRelease(iterator) }
    var devices: [HIDDeviceInfo] = []
    var entry = IOIteratorNext(iterator)
    while entry != 0 {
        if let info = hidDeviceInfo(entry) { devices.append(info) }
        IOObjectRelease(entry)
        entry = IOIteratorNext(iterator)
    }
    return devices
}

// One entry per VendorID/ProductID, in the order the system lists them.
func connectedDevices() -> [HIDDeviceInfo] {
    var merged: [DeviceID: HIDDeviceInfo] = [:]
    var order: [DeviceID] = []
    for info in listHIDDevices() {
        if let existing = merged[info.id] {
            merged[info.id] = existing.merged(with: info)
        } else {
            merged[info.id] = info
            order.append(info.id)
        }
    }
    return order.compactMap { merged[$0] }
}

// HID keyboard usages (page 7). Up/PageUp become F18, Down/PageDown become F19.
let remapPairs: [(source: UInt64, destination: UInt64)] = [
    (0x7_0000_0052, 0x7_0000_006D), // Up Arrow   -> F18
    (0x7_0000_004B, 0x7_0000_006D), // Page Up    -> F18
    (0x7_0000_0051, 0x7_0000_006E), // Down Arrow -> F19
    (0x7_0000_004E, 0x7_0000_006E), // Page Down  -> F19
]
let upKeyCode = Int64(kVK_F18)
let downKeyCode = Int64(kVK_F19)

@discardableResult
func setRemap(_ device: DeviceID, enabled: Bool, name: String = "") -> Bool {
    let entries = enabled
        ? remapPairs.map { "{\"HIDKeyboardModifierMappingSrc\":\($0.source),\"HIDKeyboardModifierMappingDst\":\($0.destination)}" }
        : []
    let matching = "{\"VendorID\":\(device.vendorID),\"ProductID\":\(device.productID)}"
    let value = "{\"UserKeyMapping\":[\(entries.joined(separator: ","))]}"
    let label = name.isEmpty ? device.description : "\(name) (\(device.description))"
    let result = run("/usr/bin/hidutil", ["property", "--matching", matching, "--set", value])
    if result.status == 0 {
        log("\(enabled ? "Remapped" : "Restored") presenter keys for \(label)")
        return true
    }
    log("hidutil failed for \(label) (status \(result.status)): \(result.output)")
    return false
}

// MARK: - Controller

final class Controller: NSObject, NSMenuDelegate {
    var config = Config.load()
    var dictationCombo = parseCombo("Cmd+D")!
    var sendCombo = parseCombo("Enter")!
    var tap: CFMachPort?
    var tapWarningLogged = false
    var paused = false
    var dictating = false
    var dictatingSince = Date.distantPast
    var lastPress: (up: Bool, time: TimeInterval)?
    var lastActionTime: TimeInterval = 0
    var notifyPort: IONotificationPortRef?
    var addedIterator: io_iterator_t = 0
    var remapped = Set<DeviceID>()
    var statusItem: NSStatusItem?
    let statusLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    let pauseItem = NSMenuItem(title: "Pause", action: nil, keyEquivalent: "")
    let deviceMenu = NSMenu()

    func start() {
        applyConfig()
        log("Starting. Agent apps: \(config.agentBundleIDs.joined(separator: ", ")); presenter auto-detect "
            + (config.autoDetectPresenters ? "on" : "off")
            + "; listed: \(config.presenters.count), ignored: \(config.ignoredDevices.count)")
        setupMenu()
        let promptKey = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        if !AXIsProcessTrustedWithOptions([promptKey: true] as CFDictionary) {
            log("Accessibility permission is not granted yet; macOS should show a prompt")
        }
        startTap()
        applyMappings(enabled: true)
        watchDevices()
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { self?.reapplyIfActive() }
        }
    }

    func applyConfig() {
        if let combo = parseCombo(config.dictationShortcut) { dictationCombo = combo }
        else { log("Bad DictationShortcut \"\(config.dictationShortcut)\"; using Cmd+D") }
        if let combo = parseCombo(config.sendKey) { sendCombo = combo }
        else { log("Bad SendKey \"\(config.sendKey)\"; using Enter") }
    }

    // MARK: Event tap

    func startTap() {
        let mask = CGEventMask(1 << CGEventType.keyDown.rawValue) | CGEventMask(1 << CGEventType.keyUp.rawValue)
        guard let newTap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                             eventsOfInterest: mask, callback: eventTapCallback, userInfo: nil) else {
            if !tapWarningLogged {
                log("Cannot create the keyboard event tap. Allow \(appName) in System Settings > Privacy & Security > Accessibility. Retrying every 3 s.")
                tapWarningLogged = true
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.startTap() }
            return
        }
        tap = newTap
        CFRunLoopAddSource(CFRunLoopGetMain(), CFMachPortCreateRunLoopSource(kCFAllocatorDefault, newTap, 0), .commonModes)
        CGEvent.tapEnable(tap: newTap, enable: true)
        log("Keyboard event tap is running")
    }

    func handlePress(up: Bool) {
        guard !paused else { return }
        let now = ProcessInfo.processInfo.systemUptime
        if !config.requireDoublePress {
            fire(up: up, now: now)
            return
        }
        if let last = lastPress, last.up == up, (now - last.time) * 1000 <= Double(config.doublePressMs) {
            lastPress = nil
            fire(up: up, now: now)
        } else {
            lastPress = (up, now)
        }
    }

    func fire(up: Bool, now: TimeInterval) {
        if (now - lastActionTime) * 1000 < Double(config.actionCooldownMs) {
            log("Ignored \(up ? "Up" : "Down") during cooldown")
            return
        }
        lastActionTime = now
        if up { onUp() } else { onDown() }
    }

    // MARK: Actions

    var frontBundleID: String? { NSWorkspace.shared.frontmostApplication?.bundleIdentifier }

    func isAgent(_ bundleID: String?) -> Bool {
        guard let id = bundleID else { return false }
        return config.agentBundleIDs.contains(id)
    }

    var isDictating: Bool {
        if dictating && Date().timeIntervalSince(dictatingSince) > Double(config.dictationStateTimeoutSec) {
            log("Dictation state expired; assuming dictation is off")
            dictating = false
        }
        return dictating
    }

    func onUp() {
        if isAgent(frontBundleID) {
            toggleDictation()
        } else if config.jumpToAgentFromOtherApps {
            activateAgent { self.toggleDictation() }
        } else {
            log("Double Up ignored: \(frontBundleID ?? "unknown app") is not an agent app")
        }
    }

    func onDown() {
        if isAgent(frontBundleID) {
            stopAndSend()
        } else if isDictating {
            activateAgent { self.stopAndSend() }
        } else {
            log("Double Down ignored: \(frontBundleID ?? "unknown app") is not an agent app and dictation is off")
        }
    }

    func toggleDictation() {
        post(dictationCombo)
        dictating.toggle()
        dictatingSince = Date()
        log("\(dictating ? "Started" : "Stopped") dictation with \(dictationCombo.text)")
    }

    func stopAndSend() {
        guard isDictating else {
            post(sendCombo)
            log("Sent with \(sendCombo.text)")
            return
        }
        post(dictationCombo)
        dictating = false
        log("Stopped dictation with \(dictationCombo.text); sending in \(config.stopBeforeSendMs) ms")
        DispatchQueue.main.asyncAfter(deadline: .now() + milliseconds(config.stopBeforeSendMs)) {
            guard self.isAgent(self.frontBundleID) else {
                log("Not sending: the agent app is no longer in front")
                return
            }
            self.post(self.sendCombo)
            log("Sent with \(self.sendCombo.text)")
        }
    }

    func post(_ combo: KeyCombo) {
        let source = CGEventSource(stateID: .hidSystemState)
        for isDown in [true, false] {
            guard let event = CGEvent(keyboardEventSource: source, virtualKey: combo.keyCode, keyDown: isDown) else { continue }
            event.flags = combo.flags
            event.post(tap: .cghidEventTap)
        }
    }

    func agentBundleID() -> String? {
        let running = Set(NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier))
        if let id = config.agentBundleIDs.first(where: { running.contains($0) }) { return id }
        return config.agentBundleIDs.first { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) != nil }
    }

    // Brings the agent app to the front (restoring a window if needed), then runs the action.
    func activateAgent(then action: @escaping () -> Void) {
        guard let id = agentBundleID() else {
            log("No agent app found among \(config.agentBundleIDs)")
            NSSound.beep()
            return
        }
        log("Bringing \(id) to the front")
        run("/usr/bin/open", ["-b", id])
        let deadline = Date().addingTimeInterval(Double(config.activateTimeoutMs) / 1000)
        func waitForFront() {
            if frontBundleID == id {
                DispatchQueue.main.asyncAfter(deadline: .now() + milliseconds(config.focusDelayMs), execute: action)
            } else if Date() < deadline {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05, execute: waitForFront)
            } else {
                log("\(id) did not come to the front within \(config.activateTimeoutMs) ms")
            }
        }
        waitForFront()
    }

    // MARK: Presenter remapping

    func connectedPresenters() -> [HIDDeviceInfo] { connectedDevices().filter(config.isPresenter) }

    // Remaps every connected presenter, and gives normal keys back to devices that are no
    // longer presenters (or to all of them when disabling).
    func applyMappings(enabled: Bool) {
        let presenters = connectedPresenters()
        let wanted = enabled ? Set(presenters.map(\.id)) : []
        for id in remapped.union(presenters.map(\.id)).subtracting(wanted) {
            setRemap(id, enabled: false)
            remapped.remove(id)
        }
        guard enabled else { return }
        if presenters.isEmpty { log("No presenter connected; one is picked up as soon as it is plugged in") }
        for device in presenters where setRemap(device.id, enabled: true, name: device.product) {
            remapped.insert(device.id)
        }
    }

    func reapplyIfActive() {
        if !paused { applyMappings(enabled: true) }
    }

    func watchDevices() {
        guard let port = IONotificationPortCreate(kIOMainPortDefault) else { return }
        notifyPort = port
        CFRunLoopAddSource(CFRunLoopGetMain(), IONotificationPortGetRunLoopSource(port).takeUnretainedValue(), .defaultMode)
        let callback: IOServiceMatchingCallback = { _, iterator in controller.devicesAdded(iterator) }
        IOServiceAddMatchingNotification(port, kIOFirstMatchNotification, IOServiceMatching("IOHIDDevice"),
                                         callback, nil, &addedIterator)
        devicesAdded(addedIterator, initial: true)
    }

    func devicesAdded(_ iterator: io_iterator_t, initial: Bool = false) {
        var presenterArrived = false
        var entry = IOIteratorNext(iterator)
        while entry != 0 {
            if let info = hidDeviceInfo(entry), config.isPresenter(info) {
                presenterArrived = true
            }
            IOObjectRelease(entry)
            entry = IOIteratorNext(iterator)
        }
        if presenterArrived && !initial {
            log("Presenter connected")
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.reapplyIfActive() }
        }
    }

    // MARK: Menu bar

    func setupMenu() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            button.image = NSImage(systemSymbolName: "mic.circle", accessibilityDescription: "Vibe Coding Presenter")
            if button.image == nil { button.title = "VCP" }
        }
        let menu = NSMenu()
        menu.delegate = self
        menu.addItem(NSMenuItem(title: "Vibe Coding Presenter", action: nil, keyEquivalent: ""))
        menu.addItem(statusLine)
        menu.addItem(.separator())
        pauseItem.action = #selector(togglePause)
        pauseItem.target = self
        menu.addItem(pauseItem)
        let devicesItem = NSMenuItem(title: "Presenters", action: nil, keyEquivalent: "")
        devicesItem.submenu = deviceMenu
        menu.addItem(devicesItem)
        addMenuItem(menu, "Re-apply presenter mapping", #selector(reapplyMapping))
        addMenuItem(menu, "Reload config", #selector(reloadConfig))
        addMenuItem(menu, "Open config file", #selector(openConfig))
        addMenuItem(menu, "Open log file", #selector(openLog))
        menu.addItem(.separator())
        addMenuItem(menu, "Quit", #selector(quit))
        item.menu = menu
        statusItem = item
    }

    func addMenuItem(_ menu: NSMenu, _ title: String, _ action: Selector) {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        menu.addItem(item)
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        let devices = connectedDevices()
        let presenters = devices.filter(config.isPresenter)
        let presenterState = presenters.isEmpty ? "No presenter connected"
            : "Presenter: " + presenters.map(\.product).joined(separator: ", ")
        let tapState = tap == nil ? "needs Accessibility permission" : paused ? "paused" : "listening"
        statusLine.title = "\(presenterState) · \(tapState)"
        pauseItem.title = paused ? "Resume" : "Pause (presenter works as normal arrows)"
        rebuildDeviceMenu(devices)
    }

    // Lists the external keyboard-type devices; a checkmark means the device is used as a presenter.
    func rebuildDeviceMenu(_ devices: [HIDDeviceInfo]) {
        deviceMenu.removeAllItems()
        let candidates = devices.filter { !$0.builtIn && ($0.isKeyboard || config.isPresenter($0)) }
        if candidates.isEmpty {
            deviceMenu.addItem(NSMenuItem(title: "No external device connected", action: nil, keyEquivalent: ""))
        }
        for device in candidates {
            let item = NSMenuItem(title: "\(device.product)  (\(device.id.shortDescription))",
                                  action: #selector(toggleDevice(_:)), keyEquivalent: "")
            item.target = self
            item.tag = device.vendorID << 16 | device.productID
            item.state = config.isPresenter(device) ? .on : .off
            deviceMenu.addItem(item)
        }
        deviceMenu.addItem(.separator())
        deviceMenu.addItem(NSMenuItem(title: "Checked devices act as presenters. Click to change.", action: nil, keyEquivalent: ""))
    }

    // Saves the choice in config.json (PresenterDevices or IgnoreDevices) and applies it at once.
    @objc func toggleDevice(_ sender: NSMenuItem) {
        let id = DeviceID(vendorID: sender.tag >> 16, productID: sender.tag & 0xFFFF)
        guard let device = connectedDevices().first(where: { $0.id == id }) else { return }
        let makePresenter = !config.isPresenter(device)
        updateConfigFile { json in
            json["PresenterDevices"] = deviceList(json["PresenterDevices"], device, include: makePresenter)
            json["IgnoreDevices"] = deviceList(json["IgnoreDevices"], device, include: !makePresenter)
        }
        config = Config.load()
        applyConfig()
        log("\(device.product) (\(id.description)) is \(makePresenter ? "now a presenter" : "no longer a presenter")")
        applyMappings(enabled: !paused)
    }

    @objc func togglePause() {
        paused.toggle()
        applyMappings(enabled: !paused)
        log(paused ? "Paused" : "Resumed")
    }

    @objc func reapplyMapping() { applyMappings(enabled: !paused) }

    @objc func reloadConfig() {
        config = Config.load()
        applyConfig()
        log("Reloaded config")
        applyMappings(enabled: !paused)
    }

    @objc func openConfig() { run("/usr/bin/open", ["-t", configURL.path]) }

    @objc func openLog() { run("/usr/bin/open", ["-t", logURL.path]) }

    @objc func quit() { shutdown() }

    func shutdown() {
        log("Quitting; restoring presenter keys")
        applyMappings(enabled: false)
        exit(0)
    }
}

var controller: Controller!

func eventTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent,
                      refcon: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        if let tap = controller.tap { CGEvent.tapEnable(tap: tap, enable: true) }
        log("Event tap was disabled by the system; re-enabled")
        return Unmanaged.passUnretained(event)
    }
    let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
    guard keyCode == upKeyCode || keyCode == downKeyCode else { return Unmanaged.passUnretained(event) }
    if controller.paused { return Unmanaged.passUnretained(event) }
    if type == .keyDown && event.getIntegerValueField(.keyboardEventAutorepeat) == 0 {
        let up = keyCode == upKeyCode
        DispatchQueue.main.async { controller.handlePress(up: up) }
    }
    return nil
}

// MARK: - Command line helpers

func printDevices() {
    let config = Config.load()
    print("HID devices (VendorID ProductID  type  transport  name):")
    for device in connectedDevices() {
        let mark = config.ignoredDevices.contains(device.id) ? "  <- ignored (IgnoreDevices)"
            : config.presenters.contains(device.id) ? "  <- presenter (PresenterDevices)"
            : config.isPresenter(device) ? "  <- presenter (auto-detected)" : ""
        let kind = device.builtIn ? "built-in" : device.isKeyboard ? "keyboard" : "other"
        print(String(format: "  0x%04X  0x%04X  %@  %@  %@%@", device.vendorID, device.productID,
                     kind, device.transport, device.product, mark))
    }
}

// Waits for a new keyboard-type HID device to appear and saves it as a presenter. Presenters are
// detected automatically, so this is only needed when the automatic guess gets one wrong.
func detectPresenter(seconds: Int) {
    let before = Set(connectedDevices().map(\.id))
    print("Plug in the presenter's USB receiver now (or switch the presenter on). Waiting up to \(seconds) s...")
    let deadline = Date().addingTimeInterval(Double(seconds))
    while Date() < deadline {
        Thread.sleep(forTimeInterval: 0.5)
        guard connectedDevices().contains(where: { !before.contains($0.id) }) else { continue }
        Thread.sleep(forTimeInterval: 1)
        let candidates = connectedDevices().filter { !before.contains($0.id) }
        guard let device = candidates.first(where: \.isKeyboard) ?? candidates.first else { continue }
        print(String(format: "Found: %@ (VendorID 0x%04X, ProductID 0x%04X)", device.product, device.vendorID, device.productID))
        let saved = updateConfigFile { json in
            json["PresenterDevices"] = deviceList(json["PresenterDevices"], device, include: true)
            json["IgnoreDevices"] = deviceList(json["IgnoreDevices"], device, include: false)
        }
        print(saved ? "Saved to \(configURL.path)" : "Could not write \(configURL.path)")
        return
    }
    print("No new device appeared. Run with --list-devices to see what is connected.")
}

// MARK: - Main

let arguments = CommandLine.arguments
if arguments.contains("--list-devices") {
    printDevices()
    exit(0)
}
if let index = arguments.firstIndex(of: "--detect") {
    let seconds = index + 1 < arguments.count ? Int(arguments[index + 1]) ?? 60 : 60
    detectPresenter(seconds: seconds)
    exit(0)
}

try? FileManager.default.createDirectory(at: supportDir, withIntermediateDirectories: true)
if let bundleID = Bundle.main.bundleIdentifier,
   NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).count > 1 {
    log("Another instance is already running; exiting")
    exit(0)
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
controller = Controller()

signal(SIGTERM, SIG_IGN)
signal(SIGINT, SIG_IGN)
let signalSources = [SIGTERM, SIGINT].map { signalNumber -> DispatchSourceSignal in
    let source = DispatchSource.makeSignalSource(signal: signalNumber, queue: .main)
    source.setEventHandler { controller.shutdown() }
    source.resume()
    return source
}

controller.start()
app.run()
