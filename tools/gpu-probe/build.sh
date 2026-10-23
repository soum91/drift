#!/bin/sh
set -eu
cd "$(dirname "$0")"
CC=${CC:-arm-linux-gnueabi-gcc}
"$CC" ${PROBE_CC_EXTRA:-} -std=c11 -Os -Wall -Wextra -Werror \
  -march=armv7-a -marm -mfloat-abi=soft -ffreestanding -fno-builtin \
  -fno-stack-protector -fno-unwind-tables -fno-asynchronous-unwind-tables \
  -nostdlib -static -no-pie -Wl,-e,_start,-z,noexecstack,--build-id=none \
  probe.c -o drift-gpu-probe-arm32
sha256sum drift-gpu-probe-arm32 > SHA256SUMS
