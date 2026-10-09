// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Trayify",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "Trayify",
            path: "Sources/Trayify",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("Carbon"),
                .linkedFramework("ServiceManagement"),
                .linkedFramework("ApplicationServices"),
            ]
        )
    ]
)
