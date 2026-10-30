import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @StateObject private var model = DriftModel()
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
                        Text("Send files from iPhone directly to your Android TV.")
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
                        TextField("TV IP address", text: $model.address)
                            .keyboardType(.numbersAndPunctuation).textInputAutocapitalization(.never)
                            .autocorrectionDisabled().focused($editingAddress)
                        TextField("Six-digit code on TV", text: $model.code)
                            .keyboardType(.numberPad)
                        Button {
                            editingAddress = false
                            Task { await model.pairWiFi() }
                        } label: {
                            Label("Pair with Drift on TV", systemImage: "tv.and.hifispeaker.fill")
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

                Section("Status") {
                    Text(model.status).font(.callout).textSelection(.enabled)
                }

                Section("How to connect") {
                    if method == .wifi {
                        Label("Open Drift on your Android TV. Choose existing Wi-Fi or ad hoc Wi-Fi.", systemImage: "1.circle")
                        Label("For ad hoc Wi-Fi, join the TV's network in iPhone Settings first.", systemImage: "2.circle")
                        Label("Enter the TV IP address and code, then choose a file. Accept it on TV.", systemImage: "3.circle")
                    } else {
                        Label("On TV, start Drift's Bluetooth LE receiver.", systemImage: "1.circle")
                        Label("Find your TV above and connect. Approve Bluetooth pairing if requested.", systemImage: "2.circle")
                        Label("Choose a small file and accept it using the TV remote.", systemImage: "3.circle")
                    }
                }

                Section {
                    Text("Transfer requires Drift on the TV to remain open. Bluetooth availability depends on TV hardware. Wi-Fi traffic uses local HTTP; use a trusted network.")
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
    }
}
