import AppKit

signal(SIGPIPE, SIG_IGN)
let args = Array(CommandLine.arguments.dropFirst())
let first = args.first?.lowercased() ?? ""

// Guardian (watchdog) mode: no UI. Waits for the parent Trayify to exit; if it died without a clean
// shutdown, hidden.json still lists hidden apps and the guardian shows them again.
if first == "--guardian" {
    Log.tag = "guardian"
    let parent = args.count > 1 ? pid_t(args[1]) ?? getppid() : getppid()
    while getppid() == parent && (kill(parent, 0) == 0 || errno == EPERM) { usleep(250_000) }
    usleep(300_000)
    let n = Recovery.restoreFromFile("guardian")
    if n > 0 { Log.warn("Trayify (pid \(parent)) exited without restoring; guardian restored \(n) app(s)") }
    exit(0)
}

// CLI control: Trayify --cmd <command...>
if first == "--cmd" {
    let reply = IpcClient.send(args.dropFirst().joined(separator: " "))
    print(reply ?? "Trayify is not running.")
    exit(reply == nil ? 1 : 0)
}

// Manual recovery: Trayify --recover
if first == "--recover" {
    print("restored \(Recovery.restoreFromFile("manual"))")
    exit(0)
}

if first == "--help" || first == "-h" {
    print("""
    Trayify: send apps to the menu bar instead of closing or minimizing them.
    usage: Trayify [--show | --tray | --no-guardian]   start (or show the running instance)
           Trayify --cmd <command>                     control the running instance
           Trayify --recover                           unhide apps left hidden by a crashed run
    \(AppCore.commandHelp)
    """)
    exit(0)
}

// Single instance: if one is already running, ask it to show its window.
if IpcClient.send("show", timeout: 3) != nil {
    print("Trayify is already running; opened its window.")
    exit(0)
}

let app = NSApplication.shared
let delegate = AppDelegate(args: args)
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
