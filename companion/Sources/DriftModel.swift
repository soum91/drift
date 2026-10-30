import Foundation
import CoreBluetooth

struct DriftPeer: Identifiable {
    let id: UUID
    let name: String
}

/// Optional Drift receiver integration. Wi-Fi is preferred for large files;
/// BLE is an encrypted GATT small-file fallback, only when the TV can advertise.
@MainActor final class DriftModel: NSObject, ObservableObject, CBCentralManagerDelegate, CBPeripheralDelegate {
    @Published var address = UserDefaults.standard.string(forKey: "driftAddress") ?? ""
    @Published var code = ""
    @Published var status = "Open Drift on TV, then choose the same transfer mode here."
    @Published var progress = 0.0
    @Published var busy = false
    @Published var peers: [DriftPeer] = []
    @Published var connectedName = ""

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
    private var token = ""

    override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: .main)
    }

    private var endpoint: URL? {
        let host = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !host.isEmpty, !host.contains("/"), !host.contains(":"),
              host.range(of: #"^[A-Za-z0-9.\-]+$"#, options: .regularExpression) != nil else { return nil }
        return URL(string: "http://\(host):45788")
    }

    func pairWiFi() async {
        guard let url = endpoint?.appendingPathComponent("pair") else { status = "Enter Drift's TV IPv4 address"; return }
        guard code.count == 6, code.allSatisfy(\.isNumber) else { status = "Enter the six-digit Drift code shown on TV"; return }
        busy = true; defer { busy = false }
        do {
            var req = URLRequest(url: url)
            req.httpMethod = "POST"
            req.setValue(code, forHTTPHeaderField: "X-Drift-Code")
            let (body, response) = try await URLSession.shared.data(for: req)
            try validate(response, body)
            guard let result = String(data: body, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines), !result.isEmpty else {
                throw DriftError.response("Invalid pairing response")
            }
            token = result
            UserDefaults.standard.set(address, forKey: "driftAddress")
            status = "Paired for this Drift session. Select any file to send."
        } catch { status = "Drift pairing failed: \(error.localizedDescription)" }
    }

    func sendWiFi(_ url: URL) async {
        guard let base = endpoint, !token.isEmpty else { status = "Pair with Drift on TV first"; return }
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
            status = "Waiting for your TV to accept \(filename)…"
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
            status = String(data: result, encoding: .utf8) ?? "Received on TV"
        } catch { status = "Wi-Fi transfer failed: \(error.localizedDescription)" }
    }

    private func validate(_ response: URLResponse, _ body: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw DriftError.response("No HTTP response") }
        guard http.statusCode == 200 else { throw DriftError.response(String(data: body, encoding: .utf8) ?? "HTTP \(http.statusCode)") }
    }

    func scanBluetooth() {
        guard central.state == .poweredOn else { status = "Enable Bluetooth and allow access to nearby devices"; return }
        peers = []; discovered = [:]
        central.scanForPeripherals(withServices: [service], options: [CBCentralManagerScanOptionAllowDuplicatesKey: false])
        status = "Looking for a TV running Drift's Bluetooth receiver…"
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
                throw DriftError.response("TV's Bluetooth characteristics cannot receive files")
            }
            bluetoothData = bytes; bluetoothOffset = 0; awaitingApproval = true
            busy = true; progress = 0
            status = "Accept \(url.lastPathComponent) on TV to start Bluetooth transfer"
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
            status = "Verifying saved Bluetooth file on TV…"
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
        peers.append(DriftPeer(id: device.identifier, name: device.name ?? "Drift TV (\(RSSI) dBm)"))
    }
    func centralManager(_ central: CBCentralManager, didConnect device: CBPeripheral) {
        connectedName = device.name ?? "Drift TV"
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
        else if text.hasPrefix("DONE:") { progress = 1; busy = false; bluetoothData = Data(); status = "Bluetooth transfer complete. SHA-256: \(text.dropFirst(5))" }
        else if text == "DECLINED" || text == "CANCELLED" || text.hasPrefix("ERROR:") {
            busy = false; awaitingApproval = false; bluetoothData = Data(); status = "TV: \(text)"
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
