import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @StateObject private var model = DriftModel()
    @Environment(\.scenePhase) private var scenePhase
    @State private var method = Method.wifi
    @State private var chooseFile = false
    @FocusState private var editingAddress: Bool

    private enum Method: String, CaseIterable, Identifiable {
        case wifi = "Wi-Fi"
        case bluetooth = "Bluetooth"
        var id: String { rawValue }
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    VStack(alignment: .leading, spacing: 7) {
                        HStack(spacing: 8) {
                            Image(systemName: "arrow.left.arrow.right")
                                .font(.title2.weight(.bold)).foregroundStyle(Color(red: 0.44, green: 0.89, blue: 0.74))
                            Text("Drift").font(.system(size: 34, weight: .bold, design: .rounded))
                        }
                        Text("Files and clipboard across iPhone, Windows and Android TV.")
                            .foregroundStyle(.secondary)
                        Text("No account. No cloud upload.")
                            .font(.footnote).foregroundStyle(Color(red: 0.44, green: 0.89, blue: 0.74))
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.vertical, 12)
                }

                Section("Connection") {
                    Picker("Method", selection: $method) {
                        ForEach(Method.allCases) { option in Text(option.rawValue).tag(option) }
                    }
                    .pickerStyle(.segmented)

                    if method == .wifi {
                        TextField("TV or Windows IPv4 address", text: $model.address)
                            .keyboardType(.numbersAndPunctuation).textInputAutocapitalization(.never)
                            .autocorrectionDisabled().focused($editingAddress)
                        TextField("Six-digit code on receiver", text: $model.code)
                            .keyboardType(.numberPad)
                        Button {
                            editingAddress = false
                            Task { await model.pairWiFi() }
                        } label: {
                            Label("Pair with Drift receiver", systemImage: "link")
                        }.disabled(model.busy)
                    } else {
                        Button { model.scanBluetooth() } label: {
                            Label("Find nearby Drift receivers", systemImage: "dot.radiowaves.left.and.right")
                        }.disabled(model.busy)
                        ForEach(model.peers) { peer in
                            Button("Connect to \(peer.name)") { model.connectBluetooth(peer.id) }
                                .disabled(model.busy)
                        }
                        if !model.connectedName.isEmpty {
                            Label("Connected: \(model.connectedName)", systemImage: "checkmark.circle.fill")
                                .foregroundStyle(.green)
                        }
                    }
                }

                Section("Transfer") {
                    Button { chooseFile = true } label: {
                        HStack(spacing: 12) {
                            Image(systemName: "doc.badge.arrow.up").font(.title2)
                            VStack(alignment: .leading, spacing: 4) {
                                Text("Choose file to send").font(.headline)
                                Text(method == .wifi ? "Any file, up to 8 GiB" : "Small files, up to 8 MiB")
                                    .font(.footnote).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Image(systemName: "chevron.right").foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 6)
                    }.disabled(model.busy)
                    if model.busy { ProgressView(value: model.progress) }
                }

                if method == .wifi {
                    Section("Windows pasteboard") {
                        Button {
                            Task { await model.sendClipboardToWindows() }
                        } label: {
                            Label("Send iPhone clipboard to PC", systemImage: "doc.on.clipboard")
                        }.disabled(model.busy || !model.isWiFiPaired)
                        Button {
                            Task { await model.copyWindowsClipboard() }
                        } label: {
                            Label("Copy Windows clipboard to iPhone", systemImage: "clipboard")
                        }.disabled(model.busy || !model.isWiFiPaired)
                        Toggle("Auto sync while Drift is open", isOn: $model.clipboardSyncEnabled)
                            .disabled(!model.isWiFiPaired)
                        Text("Enable clipboard sharing and receiving on Windows first. iOS may ask permission to paste. No background clipboard access.")
                            .font(.footnote).foregroundStyle(.secondary)
                    }

                    Section("Get files from Windows") {
                        Button { Task { await model.refreshWindowsInbox() } } label: {
                            Label("Refresh shared files", systemImage: "arrow.clockwise")
                        }.disabled(model.busy || !model.isWiFiPaired)
                        ForEach(model.inboxFiles) { file in
                            Button { Task { await model.downloadFromWindows(file) } } label: {
                                HStack {
                                    Image(systemName: "arrow.down.doc")
                                    Text(file.name).lineLimit(1)
                                    Spacer()
                                    Text(ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
                                        .foregroundStyle(.secondary)
                                }
                            }.disabled(model.busy)
                        }
                        ForEach(model.downloadedFiles, id: \.absoluteString) { file in
                            ShareLink(item: file) {
                                Label("Share saved \(file.lastPathComponent)", systemImage: "square.and.arrow.up")
                            }
                        }
                    }
                }

                Section("Status") {
                    Text(model.status).font(.callout).textSelection(.enabled)
                }

                Section("How to connect") {
                    if method == .wifi {
                        Label("Open Drift on your Android TV or Windows PC. Start receiving.", systemImage: "1.circle")
                        Label("Join the receiver's Wi-Fi network, or connect both devices to the same router.", systemImage: "2.circle")
                        Label("Enter the receiver's IPv4 address and code. Windows can exchange files and clipboard text.", systemImage: "3.circle")
                    } else {
                        Label("On TV, start Drift's Bluetooth LE receiver.", systemImage: "1.circle")
                        Label("Find your TV above and connect. Approve Bluetooth pairing if requested.", systemImage: "2.circle")
                        Label("Choose a small file and accept it using the TV remote.", systemImage: "3.circle")
                    }
                }

                Section {
                    Text("Transfer requires Drift to be running on the receiver. Bluetooth availability depends on TV hardware. Wi-Fi traffic uses local HTTP; use a trusted network. Clipboard sync is optional and active only while iPhone Drift is open.")
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Drift Companion")
            .navigationBarTitleDisplayMode(.inline)
            .scrollDismissesKeyboard(.interactively)
        }
        .tint(Color(red: 0.44, green: 0.89, blue: 0.74))
        .fileImporter(isPresented: $chooseFile, allowedContentTypes: [.item]) { result in
            switch result {
            case .success(let file):
                if method == .wifi { Task { await model.sendWiFi(file) } }
                else { model.sendBluetooth(file) }
            case .failure(let error): model.status = error.localizedDescription
            }
        }
        .task(id: model.clipboardSyncEnabled && scenePhase == .active) {
            while model.clipboardSyncEnabled && scenePhase == .active && !Task.isCancelled {
                await model.syncClipboardOnce()
                try? await Task.sleep(for: .seconds(3))
            }
        }
    }
}
