# Drift Windows Companion

Native Windows 11/10 x64 companion for the existing `soum91/drift` iPhone and Android TV apps.

## Features

- Receive files from iPhone over a private Wi-Fi network, with six-digit session pairing and a Yes/No prompt for each file. Saves in `Downloads\Drift` and computes SHA-256.
- Send files to Drift's Android TV receiver or another Drift Windows Companion using the same `/pair`, `/offer`, `/upload` protocol.
- Queue files on Windows to be downloaded from the standalone iPhone Drift Companion, which uses its own native sharing sheet to export them.
- Two-way **text clipboard / pasteboard sync** with the iPhone companion. Both Windows permissions are **OFF by default**. iPhone can manually send/pull or poll every three seconds **only while Drift is open in the foreground**. iOS may display paste permission prompts.
- Runs on Wi-Fi and on an existing Wi-Fi Direct group when both devices have joined the same IPv4 LAN. Does not require cloud storage or an account.
- Optional hyperlink to [PulseIt](https://github.com/gsl0001/windows-airdrop) for experimental **native** AirDrop interoperability. PulseIt is not bundled, and this app does **not** replace its radio/driver implementation.

## Install

Open the Windows release ZIP, extract the contents, and run `Drift.Windows.exe`. The GitHub Actions workflow publishes a self-contained portable Windows x64 build, **unsigned** (Windows SmartScreen may warn). Review the public source before running. No admin privileges should be needed for Drift, but Windows Defender Firewall may request permission to listen on the chosen private network. This is distinct from PulseIt's optional driver setup.

Latest Windows releases: https://github.com/soum91/drift/releases?q=windows-v

## How to transfer

1. Connect Windows and iPhone to the **same trusted Wi-Fi** or an ad hoc Wi-Fi Direct network; there is no internet requirement. Choose the PC's private IPv4 address in Drift Windows and click **Start receiving**.
2. Open Drift on iPhone, choose **Wi-Fi**, enter the PC address and six-digit code, then pair.
3. Send a file from iPhone; accept on Windows. For Windows-to-iPhone, choose **Share file with iPhone** on PC, then tap **Refresh shared files** and select it in iPhone Drift. Use the iOS share sheet to save to Files.
4. For clipboard sync, turn on **Let paired iPhone read Windows clipboard** and **Accept clipboard copied from paired iPhone** as desired. Tap the appropriate clipboard button on iPhone or enable **Auto sync while Drift is open**. Restart Windows receiving for a fresh session after the 15-minute token expires.
5. To send from Windows to Android TV, start the TV Drift receiver, enter its address and pairing code in the Windows companion and choose a file. Approve on the TV.

## Security and limitations

- Local plain HTTP on port **45788** is secured by a random temporary bearer token obtained through a six-digit pairing code. **It is not network-layer encryption.** Use only a trusted private network, never forward that port onto the internet or use public Wi-Fi with sensitive files/clipboards.
- Pairing expires after 15 minutes or 10 bad codes. Receiving files always requires explicit Windows acceptance. The Windows clipboard endpoints are OFF by default, independently controlled, and accept text up to 64 KiB. Files are limited to 8 GiB; up to 50 files are queued.
- For clipboard sync, foreground iOS clipboard reads depend on Apple's privacy rules. Background or system-level Universal Clipboard is **not** implemented.
- Windows built-in Wi-Fi Direct is **not** a substitute for AWDL. Appearing inside the iPhone's **native AirDrop picker** is **not a feature of Drift Windows itself**.
- The optional GPL-3.0 PulseIt code is not copied into Drift. Its developers document native-AirDrop test success with a *TP-Link Archer T3U USB VID:PID 2357:012d*, a virtual interface, working Bluetooth and administrator driver setup. It is a developer preview, not a dependable generic driver.
- Bluetooth LE direct file transfer is currently between Drift iPhone and compatible Android TV GATT peripherals. This first Windows app uses Wi-Fi for Drift transfers.

## Build

Requires .NET 10 SDK with Windows Forms workload (provided with Windows SDK). Run on Windows:

```powershell
dotnet publish windows/Drift.Windows/Drift.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o release/windows-x64
```

GitHub workflow: `.github/workflows/windows.yml`. There are no NuGet dependencies: the project uses WinForms and the .NET ASP.NET Core shared framework.

## Changelog

### Windows v0.1.0, initial companion

- New Windows tray app with local-IP selector, six-digit pairing code, transfer log and file picker.
- Compatible Wi-Fi transfers to TV and incoming file receiver for iPhone.
- Native iPhone pull-download queue.
- Two-way opt-in clipboard controls, plus foreground iPhone polling and manual send/pull.
- Windows release workflow and a clear optional experimental AirDrop research route.
