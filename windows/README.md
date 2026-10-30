# Drift Windows Companion

Native Windows 11/10 x64 companion for the existing `soum91/drift` iPhone and Android TV apps.

## Features

- Receive files from iPhone over a private Wi-Fi network, with six-digit session pairing and a Yes/No prompt for each file. Saves in `Downloads\Drift` and computes SHA-256.
- Send files to Drift's Android TV receiver or another Drift Windows Companion using the same `/pair`, `/offer`, `/upload` protocol.
- Queue files on Windows to be downloaded from the standalone iPhone Drift Companion, which uses its own native sharing sheet to export them.
- Two-way **text clipboard / pasteboard sync** with the iPhone companion. Both Windows permissions are **OFF by default**. iPhone can manually send/pull or poll every three seconds **only while Drift is open in the foreground**. iOS may display paste permission prompts.
- Runs on Wi-Fi and on an existing Wi-Fi Direct group when both devices have joined the same IPv4 LAN. Does not require cloud storage or an account.
- Automatically selects the most likely physical Wi-Fi or Ethernet adapter, starts receiving when Drift opens, and advertises a Bonjour `_drift._tcp` service for the iPhone to discover. Once paired, the iPhone saves a Windows-protected trust key and renews sessions automatically while Drift is open on the phone. For startup after signing into Windows, enable **Launch Drift when I sign in to Windows**.
- Optional **Bluetooth LE** receiver using the same encrypted GATT transfer protocol as Android TV Drift. Windows must have a Bluetooth adapter and driver that support peripheral advertising; accept incoming small files (up to 8 MiB) on the Windows prompt. This does not provide native AirDrop.
- Optional hyperlink to [PulseIt](https://github.com/gsl0001/windows-airdrop) for experimental **native** AirDrop interoperability. PulseIt is not bundled, and this app does **not** replace its radio/driver implementation.

## Install

Open the Windows release ZIP, extract the contents, and run `Drift.Windows.exe`. The GitHub Actions workflow publishes a self-contained portable Windows x64 build, **unsigned** (Windows SmartScreen may warn). Review the public source before running. No admin privileges should be needed for Drift, but Windows Defender Firewall may request permission to listen on the chosen private network. This is distinct from PulseIt's optional driver setup.

Latest Windows releases: https://github.com/soum91/drift/releases?q=windows-v

## How to transfer

1. Connect Windows and iPhone to the **same trusted Wi-Fi** or an existing Wi-Fi Direct network; there is no internet requirement. Drift Windows starts receiving automatically and prioritizes real Wi-Fi or Ethernet instead of a virtual adapter. Check the IP and select the correct interface if needed.
2. Open Drift on iPhone, choose **Wi-Fi**, enter the PC address and six-digit code, then pair. This one-time pairing saves a per-PC trust key; next time, Bonjour discovers the PC and reconnects when the iPhone app is in the foreground. You do **not** need to press the Windows-side **Pair** button, which is for sending to a TV or another receiver.
3. Send a file from iPhone; accept on Windows. For Windows-to-iPhone, choose **Share file with iPhone** on PC, then tap **Refresh shared files** and select it in iPhone Drift. Use the iOS share sheet to save to Files.
4. For clipboard sync, turn on **Let paired iPhone read Windows clipboard** and **Accept clipboard copied from paired iPhone** as desired. Tap the appropriate clipboard button on iPhone or enable **Auto sync while Drift is open**. Session renewal is automatic for a previously paired iPhone while Drift is in the foreground.
5. To send from Windows to Android TV, start the TV Drift receiver, enter its address and pairing code in the Windows companion and choose a file. Approve on the TV.
6. For small-file Bluetooth transfer, ensure Windows Bluetooth is enabled and **Start Bluetooth** has successfully changed to **Stop Bluetooth**. On iPhone, choose **Bluetooth**, find the Drift receiver, connect, choose a file (8 MiB maximum), and accept the Windows prompt. If the log says peripheral unavailable, the Bluetooth radio or driver lacks support; use Wi-Fi.

## Security and limitations

- Local plain HTTP on port **45788** is secured by a random temporary bearer token obtained through a six-digit pairing code. **It is not network-layer encryption.** Use only a trusted private network, never forward that port onto the internet or use public Wi-Fi with sensitive files/clipboards.
- Pairing expires after 15 minutes or 10 bad codes. Receiving files always requires explicit Windows acceptance. The Windows clipboard endpoints are OFF by default, independently controlled, and accept text up to 64 KiB. Files are limited to 8 GiB; up to 50 files are queued.
- Initial pairing sends the trust key only once over local HTTP. Its copy is kept under the Windows user account encrypted with DPAPI, and in iOS Keychain. Subsequent sessions use a one-time challenge and HMAC-SHA256 rather than re-sending that key. This is still **not TLS**, so pair on your trusted LAN only. Use **Forget trusted phones** on Windows and **Forget Windows pairing** on iPhone to revoke it.
- For clipboard sync, foreground iOS clipboard reads depend on Apple's privacy rules. Background or system-level Universal Clipboard is **not** implemented.
- Windows can remain running in its tray and optionally auto-launch at sign-in, but **iOS cannot hold an unlimited background connection** once the system suspends Drift. Discovery, reconnection, and clipboard polling resume while Drift is foregrounded.
- Windows built-in Wi-Fi Direct is **not** a substitute for AWDL. Appearing inside the iPhone's **native AirDrop picker** is **not a feature of Drift Windows itself**.
- The optional GPL-3.0 PulseIt code is not copied into Drift. Its developers document native-AirDrop test success with a *TP-Link Archer T3U USB VID:PID 2357:012d*, a virtual interface, working Bluetooth and administrator driver setup. It is a developer preview, not a dependable generic driver.
- Windows and Android TV both provide Drift BLE GATT receivers in current source, but hardware behavior has not yet been validated on the user's Windows PC or Sony TV. The BLE receive option fails gracefully on unsupported Windows radios.

## Build

Requires .NET 10 SDK with Windows Forms workload (provided with Windows SDK). Run on Windows:

```powershell
dotnet publish windows/Drift.Windows/Drift.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o release/windows-x64
```

GitHub workflow: `.github/workflows/windows.yml`. The project uses WinForms, .NET ASP.NET Core shared framework, Windows Runtime Bluetooth APIs, Makaretu multicast DNS and Windows DPAPI.

## Changelog

### Windows auto-discovery and Bluetooth update

- Automatically start Wi-Fi receive and prefer real Wi-Fi/Ethernet over virtual network adapters.
- Bonjour discovery, remembered pairing, HMAC reconnect without repeating the six-digit code.
- Optional autostart at Windows login, trusted phone revocation and clearer outbound/inbound pairing labels.
- Windows encrypted BLE GATT receiver compatible with iPhone Drift's existing BLE service UUIDs and small-file protocol, pending hardware verification.
- Companion foreground auto-reconnect, Bluetooth Windows device support and improved transfer checksum feedback.

### Windows v0.1.0, initial companion

- New Windows tray app with local-IP selector, six-digit pairing code, transfer log and file picker.
- Compatible Wi-Fi transfers to TV and incoming file receiver for iPhone.
- Native iPhone pull-download queue.
- Two-way opt-in clipboard controls, plus foreground iPhone polling and manual send/pull.
- Windows release workflow and a clear optional experimental AirDrop research route.
