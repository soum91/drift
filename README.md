# Drift: Android TV file transfer and native AirDrop research

The long-term research goal is the Sony A80L appearing in the iPhone's **native AirDrop picker**. That is **not implemented**. iOS does not expose its Apple peer-to-peer AirDrop/Wi-Fi protocol to third-party Android apps. The previous build 2 was a read-only USB radio diagnostic. The new build 3 source adds a practical, app-to-app file receiver and preserves the read-only radio research screen.

## Build 3 source (not yet signed and released)

- **Ad hoc Wi-Fi**: TV creates an Android Wi-Fi Direct group; iPhone joins the displayed network in Settings. The standalone **Drift Companion** sends any file using the six-digit code shown on the TV. No router needed. TV prompts for each file.
- **Local Wi-Fi or Ethernet**: TV listens on the selected local interface at TCP port `45788`; same companion pairing and file transfer, without creating a direct group. HTTP is permitted only on a private trusted network; this is not an internet-facing service. Transfer code expires after 15 minutes and locks after ten bad attempts.
- **Bluetooth LE**: separate small-file protocol using encrypted GATT writes and explicit TV approval. The TV must support BLE peripheral advertising; many Android TV devices do not. Max file size is 8 MiB. This is not classic Bluetooth OBEX or AirDrop.
- Optional Safari fallback remains available, but is no longer the primary flow.
- Received files stay inside Drift's private storage until opened or deleted. APKs use Android's installer permission prompt; none are silently installed.
- The radio diagnostic remains reachable from **Radio research** in the TV app. It does not detach or initialize the Wi-Fi radio.

## Standalone iPhone companion

`companion/` is a separate native SwiftUI iPhone app, not a feature inside EmberTV Companion. It has its own icon and bundle ID `dev.soum91.drift.companion`, independent Bluetooth and Wi-Fi transfer screens, a Files picker, and local connection instructions. Its public AltStore/FlareStore-style feed is at:

https://raw.githubusercontent.com/soum91/drift/main/source.json

The `.github/workflows/companion.yml` workflow builds an **unsigned** `Drift-Companion.ipa`, publishes a GitHub Release, and adds it to that feed. Sign the IPA with your normal iOS signer. The TV receiver and iPhone companion must both be running for transfer.

## Standalone Windows companion

`windows/` contains a **separate native Windows x64 app** with its own GitHub Actions release. It sends to the existing TV protocol and receives files from iPhone with per-file acceptance. It can share files for iPhone download and optionally sync text clipboards in both directions while the iPhone app is foregrounded. Clipboard sharing and clipboard receiving on the Windows side are OFF by default. See [Windows setup, limitations, and changelog](windows/README.md).

The Windows update adds Bonjour discovery, iPhone trusted pairing/reconnect, automatic Wi-Fi receive, Windows login autostart and a native BLE GATT receiver for small-file transfers on compatible PC radios. Remembered pairing works when the iPhone Drift app is active; unrestricted iOS background clipboard syncing is not possible.

Windows builds: https://github.com/soum91/drift/releases?q=windows-v

Native AirDrop remains an optional, **hardware-specific research** integration: [PulseIt](https://github.com/gsl0001/windows-airdrop) has documented actual iPhone-to-Windows AirDrop transfers using a supported TP-Link Archer T3U `2357:012d` USB adapter. Drift does not bundle PulseIt or claim that Windows Wi-Fi Direct can implement Apple's AWDL. Android TV's native AirDrop picker goal also remains unimplemented.

**Signing continuity:** `dev.soum91.drift` retains its application ID and build 3 increments `versionCode` to 3. The original signing keystore is *not* in GitHub. The regular test workflow compiles an explicitly `UNSIGNED` APK for review; do not attempt to install it over build 2. The separate `Drift Android TV signed release` workflow publishes an installable APK only when the original signing secrets are configured and its certificate matches build 2 (SHA-256 `80449c61bd0ecef4560eef65a575111e1884a465fb443859bba64cbf9943d022`). EmberTV should not advertise an unsigned diagnostic artifact as an update.

## Purpose

A possible route is to operate a compatible internal USB Wi-Fi radio through Android's USB permission API plus a userspace driver, then implement AWDL and the AirDrop service above it. OpenIPC devourer documents Android userspace access without root for supported USB adapters. This is evidence for an architecture, not evidence that Sony's internal radio is accessible or compatible.

Gates, in order:

1. Confirm the installed TV's radio VID/PID and exact chip/driver. Sony parts documentation points to the DHUR-SY63 USB Wi-Fi/BT module family; FCC photos show Realtek silicon. The exact installed revision is not established.
2. Determine whether Android UsbManager enumerates the internal Wi-Fi USB device and can open it after permission. This build tests that gate only. Visibility via sysfs alone is not sufficient.
3. If accessible and chip-compatible, adapt the userspace driver for the Sony USB identity, Android 32-bit ABI, and board initialization. Test detachment/restoration separately; the diagnostic build does not detach anything.
4. Demonstrate raw AWDL frame receive/transmit, channel timing, and ACK behavior on actual hardware.
5. Add a userspace IPv6/TCP/TLS/mDNS stack as needed: existing OWL's TUN/raw-socket path still requires kernel privileges and cannot simply be dropped into an unprivileged app.
6. Implement AirDrop discovery/Discover/Ask/Upload, TV acceptance, and verify the actual iPhone picker plus a completed transfer.

Steps 3-6 are unimplemented and require hardware verification. Root might be avoidable only if the userspace USB approach works. No promise of success on the A80L is made.

## Use build 2

- Install the build-2 APK over build 1, if present. Stable ID: `dev.soum91.drift`; original signing certificate retained; versionCode increased from 1 to 2.
- Launch Drift from the native Google TV launcher. It opens **NATIVE AIRDROP RADIO INVESTIGATION**.
- Photograph the diagnostic text, scrolling down if needed. It lists device/ABI/kernel, app-visible USB IDs, interface classes/endpoints, and readable driver/sysfs identities. It does not collect serial numbers, MAC addresses, or network credentials.
- Choose **Test USB access**, select the Wi-Fi-like device if listed, and grant the system USB prompt. The test opens the device, reads raw USB descriptors, and closes it. It never claims an interface, sends vendor commands, initializes a radio, changes Wi-Fi settings, or emits RF.
- Photograph the resulting `OPEN OK` or failure message.
- If no devices are listed, that means no USB devices are exposed to this app, not that no internal USB module exists. ADB can then be used for deeper read-only USB/driver diagnostics.
- Diagnostic log tag for ADB: `DriftRadio`. App capabilities are not ADB shell capabilities.

## Research completed

- Inspected Sony's source distribution explicitly covering XR-55/65/77/83A80L, package `bravia-kernel.tar.bz2`, published MD5 `1f7ea95e478136d757ee4bfa9d60ec76` (download verified). That package's kernel Makefile identifies 4.19.75; it is a published older source release, **not proof of the user's current running kernel**.
- In that source, `net/wireless/nl80211.c` protects SET_INTERFACE and NEW_INTERFACE with GENL_UNS_ADMIN_PERM; `net/netlink/genetlink.c` checks CAP_NET_ADMIN. `net/packet/af_packet.c` checks CAP_NET_RAW. Ordinary ADB permission grants do not automatically satisfy this OWL path.
- Examined `drivers/usb/core/devio.c`: userspace USB driver-disconnect operations exist, but actual Android permission/visibility and SELinux policy remain device-dependent. This build deliberately does not exercise disconnection.
- Inspected OWL's `daemon/netutils.c`: it sets monitor mode with NL80211 and requests active monitoring. OWL also requires packet injection; generic Wi-Fi Direct is insufficient.
- Inspected mosey-bada's README and control script: the port uses KernelSU root, privileged Java components, a special wonder kernel driver, and external OnePlus/Pixel binaries; its documented build uses aarch64. It is not directly usable as a 32-bit standalone Sony APK.
- Tarish advertises AirDrop support, but its linked tarish-app/tarish-daemon/tarish-link repositories returned 404 to the tools available during this investigation. Its source implementation was **not audited**.
- OpenIPC devourer documents userspace USB radio RX/TX, ACK support, and Android non-root use in PixelPilot; RTL8822CU is listed but its own row is not benchmarked. No Android ARM32 build or Sony test of devourer was performed here.

## Build

The existing `build.sh` remains usable with JDK 17+, Android SDK platform 35/build-tools 35.0.0, Python 3, curl, zip, and sha256sum:

```bash
export ANDROID_HOME=/path/to/android-sdk
export DRIFT_KEYSTORE=/private/path/drift.jks
export DRIFT_STOREPASS_FILE=/private/path/password.txt
export DRIFT_BUILD_NUMBER=3
bash build.sh
```

Keep the original signing key; never generate another one for updates. The separate signing backup from build 1 remains authoritative. No secrets are included in source archives. This build-2 source snapshot is published at https://github.com/soum91/drift. The original signing key and password remain private and are not stored in this repository.

For GitHub's signed Android release job, configure repository Actions secrets `DRIFT_KEYSTORE_BASE64` (base64 of the original JKS), `DRIFT_KEYSTORE_PASSWORD`, and, if different from the store password, `DRIFT_KEY_PASSWORD`. Set `DRIFT_KEY_ALIAS` only if the key's alias is not `drift`. The workflow checks the actual APK signing certificate against build 2 before publishing `Drift-AndroidTV.apk` under an `android-v*` release. If these secrets are absent, it publishes nothing and reports why; the existing unsigned compilation artifact remains available for source review.

`test.sh` covers the discontinued file-transfer server, not radio access. Build 2 was checked by compiling the Android sources, verifying the APK signature, validating its launcher/package/version metadata, comparing its signing certificate to build 1, and statically verifying that the diagnostic class makes no interface-claim, control-transfer, bulk-transfer, or configuration calls. TV execution remains untested.

## Primary references

- Sony source release: https://oss.sony.net/Products/Linux/TV/XR-83A80CL_USA_Ver67240.html
- FCC module internal photographs: https://fcc.report/FCC-ID/AK8DHURSY63/4370956.pdf
- Sony service manual covering A80L family: https://research.encompass.com/SON/rr/XR65A80CL.pdf
- OpenIPC devourer: https://github.com/OpenIPC/devourer
- PixelPilot: https://github.com/OpenIPC/PixelPilot
- Android USB API: https://developer.android.com/reference/android/hardware/usb/UsbDeviceConnection
- OWL: https://github.com/seemoo-lab/owl
- Android Mosey port inspected: https://github.com/FeelLiao/mosey-bada

The old file-transfer implementation is retained as inactive source for provenance. Its activity is not exported and is no longer the launcher. No transfer listener or direct Wi-Fi group starts in build 2.
