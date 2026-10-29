#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
: "${ANDROID_HOME:?Set ANDROID_HOME to an Android SDK with platform 35 and build-tools 35.0.0}"
DRIFT_BUILD_NUMBER=${DRIFT_BUILD_NUMBER:-3}
DRIFT_TOOLS="$ANDROID_HOME/build-tools/35.0.0"
DRIFT_ANDROID_JAR="$ANDROID_HOME/platforms/android-35/android.jar"
mkdir -p build/deps build/classes build/dex out
if [ ! -f build/deps/zxing-core-3.5.3.jar ]; then
  curl -fL --retry 2 -o build/deps/zxing-core-3.5.3.jar https://repo.maven.apache.org/maven2/com/google/zxing/core/3.5.3/core-3.5.3.jar
fi
printf '%s  %s\n' 8d8064c1636fdaef7189dd9055c7d59950a8940a12f2293956446ec3c109fd82 build/deps/zxing-core-3.5.3.jar | sha256sum --check --status
python3 - "$DRIFT_BUILD_NUMBER" <<'PY'
import pathlib, sys, re
build=int(sys.argv[1])
assert 0 < build < 2100000000
manifest=pathlib.Path('app/src/main/AndroidManifest.xml').read_text()
manifest=re.sub(r'android:versionCode="\d+"',f'android:versionCode="{build}"',manifest)
pathlib.Path('build/AndroidManifest.xml').write_text(manifest)
PY
"$DRIFT_TOOLS/aapt2" compile --dir app/src/main/res -o build/resources.zip
"$DRIFT_TOOLS/aapt2" link -o build/base.apk -I "$DRIFT_ANDROID_JAR" --manifest build/AndroidManifest.xml -A app/src/main/assets build/resources.zip
find app/src/main/java -name '*.java' > build/sources.list
javac -encoding UTF-8 -source 8 -target 8 -classpath "$DRIFT_ANDROID_JAR:build/deps/zxing-core-3.5.3.jar" -d build/classes @build/sources.list
jar cf build/classes.jar -C build/classes .
"$DRIFT_TOOLS/d8" --release --min-api 29 --lib "$DRIFT_ANDROID_JAR" --output build/dex build/classes.jar build/deps/zxing-core-3.5.3.jar
cp build/base.apk build/unsigned.apk
(cd build/dex && zip -q ../unsigned.apk classes*.dex)
"$DRIFT_TOOLS/zipalign" -f -p 4 build/unsigned.apk build/aligned.apk
if [ "${DRIFT_UNSIGNED:-0}" = 1 ]; then
  cp build/aligned.apk "out/Drift-0.1.1-build-$DRIFT_BUILD_NUMBER-UNSIGNED.apk"
  echo "Unsigned CI compilation only. This APK cannot update a signed Drift installation."
else
  : "${DRIFT_KEYSTORE:?Set DRIFT_KEYSTORE to the original retained signing keystore}"
  : "${DRIFT_STOREPASS_FILE:?Set DRIFT_STOREPASS_FILE to its password file}"
  "$DRIFT_TOOLS/apksigner" sign --ks "$DRIFT_KEYSTORE" --ks-key-alias drift --ks-pass "file:$DRIFT_STOREPASS_FILE" --out "out/Drift-0.1.1-build-$DRIFT_BUILD_NUMBER.apk" build/aligned.apk
  "$DRIFT_TOOLS/apksigner" verify --verbose "out/Drift-0.1.1-build-$DRIFT_BUILD_NUMBER.apk"
fi
