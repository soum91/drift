# Drift

Experimental iPhone-to-Android-TV file transfer over the TV's own Wi-Fi Direct group. No router, cloud service, account, or manual IP entry. This is a direct-network prototype, not native Apple AirDrop interoperability.

## First hardware test: Sony A80L

1. Install `Drift-0.1.0-build-1.apk` and launch **Drift** from the Google TV launcher.
2. Choose **Start direct transfer**. Grant the requested Wi-Fi/location permission. On Android 12, enable the system Location switch if prompted. Drift never requests location coordinates.
3. If Sony permits creating a group, Drift shows the actual group name and password plus two QR codes.
4. Scan **1 / Join TV Wi-Fi** with iPhone Camera and accept joining the network. A “No Internet” indication is expected.
5. Scan **2 / Send files**, open Safari, and choose files. The TV asks you to accept each file with the remote.
6. Keep Safari and Drift open until the receipt appears. Choose **Received files** to open, install APKs, or delete files. APK installation requires allowing installation from Drift in Android settings and confirming the system installer.
7. Choose **Stop and disconnect** when finished. Reconnect the TV to its usual Wi-Fi if necessary.

If **Start** fails, open **Diagnostics**. The screen reports feature availability, ABI, service initialization, and Android callback failure codes. No connection is presented as ready until there is a confirmed owner group, credentials, assigned P2P address, and listening receiver.

## What is verified

- Android sources compile against API 35; minimum Android 10/API 29.
- No native libraries: compatible with 32-bit ARM runtimes, including the intended A80L target.
- APK signature verified with Android `apksigner`.
- Host-side integration tests cover consent rejection, bearer authentication, file integrity, collision handling, invalid names, size limits, interrupted uploads, partial-file cleanup, one-use approvals, cancellation, and zero-byte files.

**Not hardware-tested:** Wi-Fi Direct group creation on the user's Sony firmware, iPhone association/QR joining, TV focus layout, and installer flow. Sony originally specified Wi-Fi Direct but later firmware notes report removal from Settings. Public P2P API availability must be tested on the TV. There is no automatic fallback to a shared LAN.

## Transfer and storage details

- Receiver binds exclusively to the confirmed Wi-Fi Direct group owner address, on a dynamic port; it does not bind `0.0.0.0`.
- Link encryption is WPA2. The local application protocol is HTTP, **not HTTPS or end-to-end encryption**. Use the generated private group; do not treat it as a hardened public-network receiver.
- A random 192-bit session bearer token is in the transfer QR's URL fragment. The page removes that fragment from history and sends the token in the Authorization header. No external scripts, resources, or tracking.
- Pairing expires in 15 minutes; each approved file has a one-use, two-minute upload ticket. Each file requires remote-control consent. No auto-accept setting.
- One file uploads at a time. Incoming data streams to a hidden temporary file, is SHA-256 hashed, synced, and moved to a final filename only on completion. The sender receives the TV-computed digest; the prototype does not independently hash large files in Safari.
- Up to 8 GiB per file, limited further by free internal storage. Duplicate names are suffixed rather than overwritten. Cancel/interruption discards partial data.
- Received files are in Drift's private internal storage. They remain across updates signed with the same key, but **uninstalling Drift deletes them**. No arbitrary filesystem permissions are requested. A read-only ContentProvider grants the chosen receiving app temporary access when opening files.
- Foreground-only prototype. No background receiver, automatic discovery, transfer resume, native iOS app/share extension, TV-to-phone sending, or AWDL/AirDrop support is implemented.

## Build and test

Requires JDK 17+, Android SDK platform 35/build-tools 35.0.0, Python 3, curl, zip, sha256sum. No Gradle installation is needed. SDK licenses must be accepted in your build environment.

```bash
bash test.sh
export ANDROID_HOME=/path/to/android-sdk
export DRIFT_KEYSTORE=/private/path/drift.jks
export DRIFT_STOREPASS_FILE=/private/path/password.txt
export DRIFT_BUILD_NUMBER=2
bash build.sh
```

The application ID is permanently `dev.soum91.drift`. Retain the original signing key, alias `drift`, and password. Increment `DRIFT_BUILD_NUMBER` for each released update. Never generate a replacement key for an existing installation. The signing backup is a separate private deliverable and is excluded from this source archive and git.

The included GitHub Actions workflow refuses to build without the retained signing secrets. It uses build numbers starting above 1000. Configure `DRIFT_KEYSTORE_BASE64` (base64 of the original keystore) and `DRIFT_STORE_PASSWORD` before running it. No GitHub repository has been created or modified for this prototype.

## References

- Sony A80L specifications: https://www.sony.com/electronics/support/televisions-projectors-oled-tvs-android-/xr-55a80l/specifications
- Sony direct iPhone connection guide: https://www.sony.com/electronics/support/articles/00015505
- Sony L-series firmware history: https://www.sony.com/electronics/support/lcd-tvs-android-x90l_x91l-series/xr-75x90l/software/00355753
- Android Wi-Fi Direct API: https://developer.android.com/develop/connectivity/wifi/wifip2p

QR encoding uses ZXing Core 3.5.3, Apache License 2.0. Its license is included in `ZXING-LICENSE.txt`; the build verifies the downloaded artifact's SHA-256.
