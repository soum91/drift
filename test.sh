#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build/test
javac -d build/test app/src/main/java/dev/soum91/drift/TransferServer.java tests/TransferServerTest.java
java -cp build/test TransferServerTest
