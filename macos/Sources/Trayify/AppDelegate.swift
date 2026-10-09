import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    static var core: AppCore?
    private let args: [String]
    private var signalSources: [DispatchSourceSignal] = []

    init(args: [String]) {
        self.args = args.map { $0.lowercased() }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let core = AppCore()
        Self.core = core
        NSSetUncaughtExceptionHandler { ex in
            Log.error("Uncaught exception: \(ex)")
            Recovery.restoreFromFile("crash")
        }
        for sig in [SIGTERM, SIGINT, SIGHUP] {
            signal(sig, SIG_IGN)
            let src = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            src.setEventHandler {
                MainActor.assumeIsolated {
                    Log.info("Signal \(sig) received; quitting")
                    AppDelegate.core?.quit()
                }
            }
            src.resume()
            signalSources.append(src)
        }
        core.start(spawnGuardian: !args.contains("--no-guardian"))

        // First run (no rules yet) or --show: open the window. Otherwise start quietly in the menu bar.
        let launchedAtLogin = (NSAppleEventManager.shared().currentAppleEvent?.paramDescriptor(forKeyword: keyAELaunchedAsLogInItem)) != nil
        if args.contains("--show") || (core.settings.rules.isEmpty && !args.contains("--tray") && !launchedAtLogin) {
            core.showSettings()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        Self.core?.showSettings()
        return false
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Self.core?.shutdown()
        return .terminateNow
    }

    func applicationWillTerminate(_ notification: Notification) {
        Self.core?.shutdown()
    }
}
