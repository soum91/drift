import Foundation
import CoreBluetooth
import UIKit
import Security
import CryptoKit

struct DriftPeer: Identifiable {
    let id: UUID
    let name: String
}

struct DriftInboxFile: Identifiable, Decodable {
    let id: String
    let name: String
    let size: Int64
}

/// Optional Drift receiver integration. Wi-Fi is preferred for large files;
/// BLE is an encrypted GATT small-file fallback, only when the TV can advertise.
@MainActor final class DriftModel: NSObject, ObservableObject, CBCentralManagerDelegate, CBPeripheralDelegate,
    NetServiceBrowserDelegate, NetServiceDelegate {
    @Published var address = UserDefaults.standard.string(forKey: "driftAddress") ?? ""
    @Published var code = ""
    @Published var status = "Open Drift on TV or Windows, then pair over Wi-Fi."
    @Published var progress = 0.0
    @Published var busy = false
    @Published var peers: [DriftPeer] = []
    @Published var connectedName = ""
    @Published var inboxFiles: [DriftInboxFile] = []
    @Published var downloadedFiles: [URL] = []
    @Published var clipboardSyncEnabled = UserDefaults.standard.bool(forKey: "driftClipboardAutoSync")
    @Published private(set) var isWiFiPaired = false
    @Published var autoReconnect = UserDefaults.standard.object(forKey: "driftAutoReconnect") as? Bool ?? true
    @Published var foundWindows = "Searching for Drift on your network…"
    @Published var localNetworkDiagnostics = "Local Network access has not been checked yet."
    @Published var reconnecting = false
    private var seenPhoneClipboard: String?
    private var seenWindowsClipboard: String?

    private let service = CBUUID(string: "D25E69C1-7C6D-46E3-A7C9-A7CFE89A0020")
    private let controlUUID = CBUUID(string: "D25E69C1-7C6D-46E3-A7C9-A7CFE89A0021")
    private let dataUUID = CBUUID(string: "D25E69C1-7C6D-46E3-A7C9-A7CFE89A0022")
    private let statusUUID = CBUUID(string: "D25E69C1-7C6D-46E3-A7C9-A7CFE89A0023")
    private var central: CBCentralManager!
    private var discovered: [UUID: CBPeripheral] = [:]
    private var peripheral: CBPeripheral?
    private var control: CBCharacteristic?
    private var dataCharacteristic: CBCharacteristic?
    private var statusCharacteristic: CBCharacteristic?
    private var bluetoothData = Data()
    private var bluetoothOffset = 0
    private var awaitingApproval = false
    private var token = "" {
        didSet { isWiFiPaired = !token.isEmpty }
    }
    private var discoveredTrustedHost: String?
    private var serviceBrowser: NetServiceBrowser?
    private var discoveredServices: [NetService] = []
    private var discoveryAttempt = UUID()

    private struct Trust: Codable {
        let id: String
        let secret: String
        var host: String
    }
    private let trustAccount = "dev.soum91.drift.windows.trusted"

    override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: .main)
        // A Bonjour browse during app initialization can occur before iOS has
        // activated the scene, when the Local Network prompt is suppressed.
        // ContentView starts discovery after the scene becomes active.
        Task { await reconnectWindowsIfPossible() }
    }

    private func endpoint(for address: String) -> URL? {
        let host = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !host.isEmpty, !host.contains("/"), !host.contains(":"),
              host.range(of: #"^[A-Za-z0-9.\-]+$"#, options: .regularExpression) != nil else { return nil }
        return URL(string: "http://\(host):45788")
    }

    private var endpoint: URL? { endpoint(for: address) }

    func pairWiFi() async {
        guard let url = endpoint?.appendingPathComponent("pair") else { status = "Enter the TV or PC IPv4 address"; return }
        guard code.count == 6, code.allSatisfy(\.isNumber) else { status = "Enter the six-digit code shown on the receiver"; return }
        busy = true; defer { busy = false }
        do {
            var req = URLRequest(url: url, timeoutInterval: 10)
            req.httpMethod = "POST"
            req.setValue(code, forHTTPHeaderField: "X-Drift-Code")
            let (body, response) = try await URLSession.shared.data(for: req)
            try validate(response, body)
            guard let result = String(data: body, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines), !result.isEmpty else {
                throw DriftError.response("Invalid pairing response")
            }
            token = result
            var remembered = false
            if let http = response as? HTTPURLResponse,
               let id = http.value(forHTTPHeaderField: "X-Drift-Device-ID"),
               let secret = http.value(forHTTPHeaderField: "X-Drift-Trust-Key"),
               id.count == 32, secret.count == 64 {
                remembered = saveTrust(Trust(id: id, secret: secret, host: address))
            }
            inboxFiles = []
            seenPhoneClipboard = nil
            seenWindowsClipboard = nil
            UserDefaults.standard.set(address, forKey: "driftAddress")
            status = remembered
                ? "Connected and trusted. Drift will reconnect automatically without another code."
                : "Connected for this session, but trusted pairing could not be saved. Check the Windows Remember setting or iOS Keychain access."
        } catch { status = "Drift pairing failed: \(error.localizedDescription)" }
    }

    func beginDiscovery() {
        serviceBrowser?.stop()
        discoveredServices.removeAll()
        let attempt = UUID()
        discoveryAttempt = attempt
        foundWindows = "Searching for Drift on your network…"
        localNetworkDiagnostics = "Checking Local Network access with Bonjour. If asked, tap Allow."
        let browser = NetServiceBrowser()
        browser.delegate = self
        serviceBrowser = browser
        browser.searchForServices(ofType: "_drift._tcp.", inDomain: "local.")
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(8))
            guard let self, self.discoveryAttempt == attempt,
                  self.discoveredServices.isEmpty,
                  self.serviceBrowser === browser else { return }
            self.localNetworkDiagnostics = "No receiver discovered yet. If no permission prompt appeared, check Settings → Privacy & Security → Local Network."
        }
    }

    func netServiceBrowserWillSearch(_ browser: NetServiceBrowser) {
        localNetworkDiagnostics = "Bonjour discovery started. Waiting for nearby Drift receivers."
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didNotSearch errorDict: [String: NSNumber]) {
        let code = errorDict[NSNetServicesErrorCode]?.intValue ?? 0
        foundWindows = "Drift discovery failed (error \(code))."
        localNetworkDiagnostics = "Local Network discovery was blocked or failed (\(code)). Check Settings → Privacy & Security → Local Network; then tap Check access again."
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        discoveredServices.append(service) // Keep a strong reference until resolved.
        localNetworkDiagnostics = "Local discovery working. Found \(service.name)."
        service.delegate = self
        service.resolve(withTimeout: 8)
        foundWindows = "Found \(service.name). Resolving…"
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        discoveredServices.removeAll { $0 === service }
    }

    func netServiceDidResolveAddress(_ service: NetService) {
        guard let hostname = service.hostName?.trimmingCharacters(in: CharacterSet(charactersIn: ".")),
              !hostname.isEmpty else { return }
        foundWindows = "Found \(service.name) at \(hostname)"
        guard autoReconnect, let trust = loadTrust(),
              let txt = service.txtRecordData(),
              let id = NetService.dictionary(fromTXTRecord: txt)["id"].flatMap({ String(data: $0, encoding: .utf8) }),
              id == trust.id else { return }
        // Discovery is a fallback. Never replace a working saved IPv4 address
        // with an untested .local host, which can break automatic reconnection.
        discoveredTrustedHost = hostname
        Task { await reconnectWindowsIfPossible() }
    }

    func setAutoReconnect(_ enabled: Bool) {
        autoReconnect = enabled
        UserDefaults.standard.set(enabled, forKey: "driftAutoReconnect")
        if enabled { Task { await reconnectWindowsIfPossible() } }
    }

    func setClipboardSyncEnabled(_ enabled: Bool) {
        clipboardSyncEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: "driftClipboardAutoSync")
        if enabled {
            seenPhoneClipboard = nil
            seenWindowsClipboard = nil
            if !isWiFiPaired {
                status = loadTrust() == nil
                    ? "Clipboard preference saved. Pair with Windows once to establish trusted reconnection."
                    : "Clipboard sync enabled. Waiting for the trusted Windows connection."
                Task { await reconnectWindowsIfPossible() }
            }
        }
    }

    func reconnectWindowsIfPossible() async {
        guard autoReconnect, !reconnecting, let trust = loadTrust() else { return }
        reconnecting = true
        defer { reconnecting = false }
        // Try the last working IP first; Bonjour and the saved trusted host are
        // fallbacks if Windows changes addresses after a reboot or Wi-Fi switch.
        let candidates = [address, trust.host, discoveredTrustedHost ?? ""]
            .filter { !$0.isEmpty }
        var attempted = Set<String>()
        var lastError: Error?
        for host in candidates where attempted.insert(host).inserted {
            guard let base = endpoint(for: host) else { continue }
            do {
                if !token.isEmpty && host == address {
                    var health = URLRequest(url: base.appendingPathComponent("health"), timeoutInterval: 5)
                    health.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
                    let (_, response) = try await URLSession.shared.data(for: health)
                    if let http = response as? HTTPURLResponse, http.statusCode == 200 { return }
                }
                // Challenge/HMAC reconnect uses saved Keychain trust, never the six-digit code.
                let challenge = URLRequest(url: base.appendingPathComponent("challenge"), timeoutInterval: 5)
                let (nonceData, challengeResponse) = try await URLSession.shared.data(for: challenge)
                guard let http = challengeResponse as? HTTPURLResponse,
                      http.statusCode == 200,
                      http.value(forHTTPHeaderField: "X-Drift-Device-ID") == trust.id,
                      let nonce = String(data: nonceData, encoding: .utf8), nonce.count == 48,
                      let secret = Data(hexadecimal: trust.secret) else {
                    throw DriftError.response("Saved Windows trust could not be verified")
                }
                let signature = HMAC<SHA256>.authenticationCode(for: Data(nonce.utf8), using: SymmetricKey(data: secret))
                let proof = Data(signature).map { String(format: "%02X", $0) }.joined()
                var request = URLRequest(url: base.appendingPathComponent("reconnect"), timeoutInterval: 5)
                request.httpMethod = "POST"
                request.setValue(proof, forHTTPHeaderField: "X-Drift-Proof")
                let (result, response) = try await URLSession.shared.data(for: request)
                try validate(response, result)
                guard let session = String(data: result, encoding: .utf8), session.count == 48 else {
                    throw DriftError.response("Invalid trusted reconnect response")
                }
                token = session
                if address != host {
                    address = host
                    UserDefaults.standard.set(host, forKey: "driftAddress")
                    _ = saveTrust(Trust(id: trust.id, secret: trust.secret, host: host))
                }
                status = "Reconnected securely to Drift Windows (\(host))."
                return
            } catch {
                lastError = error
            }
        }
        token = ""
        status = "Waiting for trusted Drift Windows… \(lastError?.localizedDescription ?? "No reachable saved address")"
    }

    private func loadTrust() -> Trust? {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrAccount as String: trustAccount,
                                    kSecReturnData as String: true,
                                    kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(Trust.self, from: data)
    }

    @discardableResult private func saveTrust(_ trust: Trust) -> Bool {
        guard let value = try? JSONEncoder().encode(trust) else { return false }
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrAccount as String: trustAccount]
        let updates: [String: Any] = [kSecValueData as String: value]
        let updated = SecItemUpdate(query as CFDictionary, updates as CFDictionary)
        if updated == errSecSuccess { return true }
        guard updated == errSecItemNotFound else { return false }
        var item = query
        item[kSecValueData as String] = value
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    func forgetTrustedWindows() {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                    kSecAttrAccount as String: trustAccount]
        SecItemDelete(query as CFDictionary)
        token = ""
        status = "Windows trust removed. Enter the new pairing code to connect."
    }

    func sendWiFi(_ url: URL) async {
        guard let base = endpoint, !token.isEmpty else { status = "Pair with a Drift receiver first"; return }
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        busy = true; progress = 0
        defer { busy = false }
        do {
            let filename = url.lastPathComponent
            let bytes = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard bytes >= 0, bytes <= 8 * 1024 * 1024 * 1024 else { throw DriftError.response("Maximum file size: 8 GiB") }
            var offer = URLRequest(url: base.appendingPathComponent("offer"))
            var components = URLComponents(url: offer.url!, resolvingAgainstBaseURL: false)!
            components.queryItems = [URLQueryItem(name: "name", value: filename), URLQueryItem(name: "size", value: String(bytes))]
            offer.url = components.url
            offer.httpMethod = "POST"
            offer.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            status = "Waiting for the receiver to accept \(filename)…"
            let (reply, response) = try await URLSession.shared.data(for: offer)
            try validate(response, reply)
            guard let ticket = String(data: reply, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines), !ticket.isEmpty else {
                throw DriftError.response("No approval ticket")
            }
            var uploadURL = URLComponents(url: base.appendingPathComponent("upload"), resolvingAgainstBaseURL: false)!
            uploadURL.queryItems = [URLQueryItem(name: "ticket", value: ticket)]
            var upload = URLRequest(url: uploadURL.url!)
            upload.httpMethod = "POST"
            upload.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            upload.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
            status = "Uploading \(filename)… Keep both apps open."
            // URLSession streams from disk instead of loading multi-gigabyte files into RAM.
            let (result, uploadResponse) = try await URLSession.shared.upload(for: upload, fromFile: url)
            try validate(uploadResponse, result)
            progress = 1
            status = String(data: result, encoding: .utf8) ?? "Received by Drift"
        } catch { status = "Wi-Fi transfer failed: \(error.localizedDescription)" }
    }

    private func windowsRequest(_ path: String, method: String = "GET") throws -> URLRequest {
        guard let base = endpoint, !token.isEmpty else { throw DriftError.response("Pair with Drift on Windows first") }
        var request = URLRequest(url: base.appendingPathComponent(path))
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return request
    }

    func refreshWindowsInbox() async {
        do {
            let request = try windowsRequest("inbox")
            let (data, response) = try await URLSession.shared.data(for: request)
            try validate(response, data)
            inboxFiles = try JSONDecoder().decode([DriftInboxFile].self, from: data)
            status = "Windows has \(inboxFiles.count) shared file(s) available."
        } catch { status = "Windows inbox: \(error.localizedDescription)" }
    }

    func downloadFromWindows(_ file: DriftInboxFile) async {
        busy = true; defer { busy = false }
        do {
            var request = try windowsRequest("download")
            var components = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)!
            components.queryItems = [URLQueryItem(name: "id", value: file.id)]
            request.url = components.url
            let (temporary, response) = try await URLSession.shared.download(for: request)
            try validate(response, Data())
            let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("Drift Downloads", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let safeName = URL(fileURLWithPath: file.name).lastPathComponent
            guard !safeName.isEmpty, !safeName.hasPrefix(".") else { throw DriftError.response("Invalid shared filename") }
            let stem = (safeName as NSString).deletingPathExtension
            let ext = (safeName as NSString).pathExtension
            var destination = directory.appendingPathComponent(safeName)
            var suffix = 1
            while FileManager.default.fileExists(atPath: destination.path) {
                let candidate = "\(stem) (\(suffix))" + (ext.isEmpty ? "" : ".\(ext)")
                destination = directory.appendingPathComponent(candidate)
                suffix += 1
            }
            try FileManager.default.moveItem(at: temporary, to: destination)
            downloadedFiles.insert(destination, at: 0)
            status = "Downloaded \(safeName). Tap Share to save to Files or open in another app."
        } catch { status = "Windows download failed: \(error.localizedDescription)" }
    }

    private func readWindowsClipboard() async throws -> String {
        let request = try windowsRequest("clipboard")
        let (data, response) = try await URLSession.shared.data(for: request)
        try validate(response, data)
        guard data.count <= 64 * 1024, let text = String(data: data, encoding: .utf8) else {
            throw DriftError.response("Windows clipboard exceeds 64 KiB or is not UTF-8 text")
        }
        return text
    }

    private func writeWindowsClipboard(_ text: String) async throws {
        guard text.utf8.count <= 64 * 1024 else { throw DriftError.response("Text clipboard limited to 64 KiB") }
        var request = try windowsRequest("clipboard", method: "POST")
        request.httpBody = Data(text.utf8)
        request.setValue("text/plain; charset=utf-8", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await URLSession.shared.data(for: request)
        try validate(response, data)
    }

    func sendClipboardToWindows() async {
        do {
            guard let text = UIPasteboard.general.string else { throw DriftError.response("Nothing copied on iPhone") }
            try await writeWindowsClipboard(text)
            seenPhoneClipboard = text
            seenWindowsClipboard = text
            status = "iPhone clipboard copied to Windows."
        } catch { status = "Send clipboard: \(error.localizedDescription)" }
    }

    func copyWindowsClipboard() async {
        do {
            let text = try await readWindowsClipboard()
            UIPasteboard.general.string = text
            seenPhoneClipboard = text
            seenWindowsClipboard = text
            status = "Windows clipboard copied to iPhone."
        } catch { status = "Get clipboard: \(error.localizedDescription)" }
    }

    /// Foreground-only opt-in polling. iOS may show a pasteboard permission prompt.
    func syncClipboardOnce() async {
        guard clipboardSyncEnabled, isWiFiPaired else { return }
        do {
            let phone = UIPasteboard.general.string ?? ""
            let windows = try await readWindowsClipboard()
            guard let oldPhone = seenPhoneClipboard, let oldWindows = seenWindowsClipboard else {
                // First poll establishes baseline; never overwrite either clipboard on toggle-on.
                seenPhoneClipboard = phone
                seenWindowsClipboard = windows
                return
            }
            if phone != oldPhone && phone != windows {
                try await writeWindowsClipboard(phone)
                seenPhoneClipboard = phone
                seenWindowsClipboard = phone
                status = "Clipboard synced: iPhone → Windows"
            } else if windows != oldWindows && windows != phone {
                UIPasteboard.general.string = windows
                seenPhoneClipboard = windows
                seenWindowsClipboard = windows
                status = "Clipboard synced: Windows → iPhone"
            } else {
                seenPhoneClipboard = phone
                seenWindowsClipboard = windows
            }
        } catch {
            // Keep the user's preference enabled while Windows sleeps, the LAN
            // disconnects, or a short-lived session requires trusted renewal.
            status = "Clipboard sync paused: \(error.localizedDescription). Will retry when Windows reconnects."
        }
    }

    private func validate(_ response: URLResponse, _ body: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw DriftError.response("No HTTP response") }
        guard http.statusCode == 200 else { throw DriftError.response(String(data: body, encoding: .utf8) ?? "HTTP \(http.statusCode)") }
    }

    func scanBluetooth() {
        guard central.state == .poweredOn else { status = "Enable Bluetooth and allow access to nearby devices"; return }
        peers = []; discovered = [:]
        central.scanForPeripherals(withServices: [service], options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
        status = "Looking for a Windows PC or TV with Drift Bluetooth receiving enabled…"
    }
    func connectBluetooth(_ id: UUID) {
        guard let device = discovered[id] else { status = "Scan again to find Drift"; return }
        central.stopScan()
        peripheral = device; control = nil; dataCharacteristic = nil; statusCharacteristic = nil
        device.delegate = self
        central.connect(device)
        status = "Connecting to Drift over Bluetooth…"
    }
    func sendBluetooth(_ url: URL) {
        guard let device = peripheral, let control, let dataCharacteristic, let statusCharacteristic,
              statusCharacteristic.isNotifying else { status = "Connect to a Bluetooth Drift receiver first"; return }
        let access = url.startAccessingSecurityScopedResource()
        defer { if access { url.stopAccessingSecurityScopedResource() } }
        do {
            let fileSize = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            guard fileSize <= 8 * 1024 * 1024 else { status = "Bluetooth is limited to 8 MiB. Use Wi-Fi for larger files."; return }
            let bytes = try Data(contentsOf: url)
            let command = Data("START:\(url.lastPathComponent):\(bytes.count)".utf8)
            guard command.count <= device.maximumWriteValueLength(for: .withResponse) else {
                throw DriftError.response("Filename too long for Bluetooth packet")
            }
            guard dataCharacteristic.properties.contains(.write), control.properties.contains(.write) else {
                throw DriftError.response("Bluetooth receiver does not expose writable Drift characteristics")
            }
            bluetoothData = bytes; bluetoothOffset = 0; awaitingApproval = true
            busy = true; progress = 0
            status = "Accept \(url.lastPathComponent) on the receiving device to start Bluetooth transfer"
            device.writeValue(command, for: control, type: .withResponse)
        } catch { status = error.localizedDescription; busy = false }
    }
    private func nextBluetoothPacket() {
        guard let device = peripheral, let dataCharacteristic, let control else { return }
        if bluetoothOffset < bluetoothData.count {
            let size = min(device.maximumWriteValueLength(for: .withResponse), bluetoothData.count - bluetoothOffset)
            guard size > 0 else { status = "No usable Bluetooth packet size"; busy = false; return }
            let packet = bluetoothData.subdata(in: bluetoothOffset..<(bluetoothOffset + size))
            bluetoothOffset += size
            progress = Double(bluetoothOffset) / Double(max(bluetoothData.count,1))
            device.writeValue(packet, for: dataCharacteristic, type: .withResponse)
        } else {
            status = "Verifying saved Bluetooth file on receiver…"
            device.writeValue(Data("FINISH".utf8), for: control, type: .withResponse)
        }
    }

    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        if central.state != .poweredOn { connectedName = ""; status = "Bluetooth is unavailable (\(central.state.rawValue))" }
    }
    func centralManager(_ central: CBCentralManager, didDiscover device: CBPeripheral,
                        advertisementData: [String: Any], rssi RSSI: NSNumber) {
        guard discovered[device.identifier] == nil else { return }
        discovered[device.identifier] = device
        peers.append(DriftPeer(id: device.identifier, name: device.name ?? "Drift receiver (\(RSSI) dBm)"))
    }
    func centralManager(_ central: CBCentralManager, didConnect device: CBPeripheral) {
        connectedName = device.name ?? "Drift receiver"
        status = "Connected. Discovering encrypted file-transfer service…"
        device.discoverServices([service])
    }
    func centralManager(_ central: CBCentralManager, didFailToConnect device: CBPeripheral, error: Error?) {
        status = "Bluetooth connection failed: \(error?.localizedDescription ?? "Unknown error")"; connectedName = ""; busy = false
    }
    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral device: CBPeripheral, error: Error?) {
        connectedName = ""; busy = false; bluetoothData = Data()
        status = "Bluetooth disconnected. Connect again or use Wi-Fi."
    }
    func peripheral(_ device: CBPeripheral, didDiscoverServices error: Error?) {
        guard error == nil else { status = error!.localizedDescription; return }
        device.services?.filter { $0.uuid == service }.forEach { device.discoverCharacteristics([controlUUID,dataUUID,statusUUID], for: $0) }
    }
    func peripheral(_ device: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard error == nil else { status = error!.localizedDescription; return }
        for c in service.characteristics ?? [] {
            if c.uuid == controlUUID { control = c }
            if c.uuid == dataUUID { dataCharacteristic = c }
            if c.uuid == statusUUID { statusCharacteristic = c; device.setNotifyValue(true, for: c) }
        }
    }
    func peripheral(_ device: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        if let error { status = "Bluetooth setup failed: \(error.localizedDescription)" }
        else if characteristic.uuid == statusUUID && characteristic.isNotifying { status = "Bluetooth paired. Select a file up to 8 MiB." }
    }
    func peripheral(_ device: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard characteristic.uuid == statusUUID, let text = characteristic.value.flatMap({ String(data: $0, encoding: .utf8) }) else { return }
        if text == "READY" && awaitingApproval { awaitingApproval = false; nextBluetoothPacket() }
        else if text.hasPrefix("DONE:") {
            let remoteHash = String(text.dropFirst(5)).lowercased()
            let localHash = SHA256.hash(data: bluetoothData).map { String(format: "%02x", $0) }.joined()
            progress = 1; busy = false; bluetoothData = Data()
            if remoteHash.count == 64 {
                status = remoteHash == localHash
                    ? "Bluetooth transfer complete and SHA-256 verified."
                    : "WARNING: Bluetooth file checksum does not match. Retry over Wi-Fi."
            } else {
                // GATT notifications can be restricted to 20 bytes on older radios.
                status = "Receiver confirmed Bluetooth transfer. SHA-256 notification was shortened by the radio."
            }
        }
        else if text == "DECLINED" || text == "CANCELLED" || text.hasPrefix("ERROR:") {
            busy = false; awaitingApproval = false; bluetoothData = Data(); status = "Receiver: \(text)"
        }
    }
    func peripheral(_ device: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        if let error { busy = false; bluetoothData = Data(); status = "Bluetooth write failed: \(error.localizedDescription)"; return }
        if characteristic.uuid == dataUUID && !awaitingApproval { nextBluetoothPacket() }
    }
}

private enum DriftError: LocalizedError {
    case response(String)
    var errorDescription: String? { if case .response(let text) = self { return text }; return nil }
}

private extension Data {
    init?(hexadecimal: String) {
        guard hexadecimal.count.isMultiple(of: 2) else { return nil }
        self.init(capacity: hexadecimal.count / 2)
        var cursor = hexadecimal.startIndex
        while cursor < hexadecimal.endIndex {
            let next = hexadecimal.index(cursor, offsetBy: 2)
            guard let byte = UInt8(hexadecimal[cursor..<next], radix: 16) else { return nil }
            append(byte)
            cursor = next
        }
    }
}
