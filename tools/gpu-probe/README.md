# Drift GPU version probe v1

This standalone ARM32 EABI executable asks `/dev/mali0` for the installed Mali
kernel driver's version. It is a diagnostic for the Sony A80L native AirDrop
investigation. It does not root the TV or test for a vulnerability.

It uses Linux syscalls directly, so it needs neither an Android dynamic linker
nor a glibc installation. It can run in the known 32-bit userspace on the TV's
ARM64 compatibility layer. It does not replace or update the Drift APK.

## Operations

1. Open `/dev/mali0` with `O_RDWR | O_CLOEXEC` because driver ioctls require a
   device descriptor. No filesystem creation flags are used.
2. Issue `KBASE_IOCTL_VERSION_CHECK` (0xc0048000) with a zero-initialized version
   pair to request the kernel's UK ABI. Stop unless it is Job Manager major 11.
3. Issue `KBASE_IOCTL_SET_FLAGS` (0x40048001) with only
   `BASE_CONTEXT_SYSTEM_MONITOR_SUBMIT_DISABLED` (2). This creates or configures
   a temporary context with job submission disabled.
4. Issue `KBASE_IOCTL_GET_DDK_VERSION` (0x4010800d) with a zero-initialized,
   bounded 512-byte output buffer. Validate and sanitize the returned string.
5. Close the descriptor, releasing the temporary kernel context.

The executable has no GPU job submission, mmap, GPU memory allocation/JIT,
kernel writes, root escalation, SELinux changes, network access, USB access,
Wi-Fi changes, or persistent settings/file writes. It only writes its report
to stdout. Normal transient kernel bookkeeping can occur when opening and
initializing a device context. Unix mode bits alone do not prove SELinux will
permit the open or ioctls. A permission denial is reported without retrying as
another user or invoking any exploit.

## Run from the already connected Alpine ADB host

```sh
wget -O drift-gpu-probe-arm32 https://raw.githubusercontent.com/soum91/drift/main/tools/gpu-probe/drift-gpu-probe-arm32
echo '185121b66d2032ff199c08440783cf5c295887af6b32d76722a8acc838b113cc  drift-gpu-probe-arm32' | sha256sum -c -
adb push drift-gpu-probe-arm32 /data/local/tmp/drift-gpu-probe
adb shell chmod 700 /data/local/tmp/drift-gpu-probe
adb shell /data/local/tmp/drift-gpu-probe
```

Share the complete short output, including an error if one occurs. Successful
output includes `KERNEL_UK_ABI`, `KERNEL_DDK_VERSION` and `DEVICE_CLOSED=OK`.
Exit status 0 means the version was read, 2 indicates a failed operation, and
3 indicates an unsupported ABI or malformed response. The DDK string is an
identification lead, not proof of Sony's security patch/backport status.

Remove afterward if desired:

```sh
adb shell rm /data/local/tmp/drift-gpu-probe
```

## Build and validation

Requires a GNU ARM EABI cross-compiler and binutils. Run `sh build.sh` in this
directory; set `CC` if the compiler is not named `arm-linux-gnueabi-gcc`.
`PROBE_CC_EXTRA` can supply toolchain search flags for a relocated compiler.
Release v1 was built with GCC 13.3.0, without libc or runtime dependencies.

The host driver model checks ten cases, enforcing the exact ioctl allowlist,
submit-disabled flags, output bounds/sanitization and descriptor cleanup on
all opened-device exit paths. It includes permission denial, unsupported ABI,
failed queries, oversized/unterminated responses and interrupted/partial stdout:

```sh
gcc -std=c11 -O2 -Wall -Wextra -Werror -fsanitize=address,undefined test.c -o /tmp/drift-gpu-probe-test
ASAN_OPTIONS=detect_leaks=0 /tmp/drift-gpu-probe-test
```

The release ELF's ARM syscall/output/error path was also executed under QEMU
8.2.2; without a real GPU it correctly reported ENOENT and exited. ELF checks
confirmed ARM EABI5, static linkage, no interpreter or unresolved imports, and
a non-executable stack. The successful driver path is model-tested, not
hardware-tested on the Sony TV. No root exploit was run.

## Interface references

- Mali ioctl layouts: https://chromium.googlesource.com/chromiumos/third_party/kernel/+/chromeos-4.4/drivers/gpu/arm/midgard/mali_kbase_ioctl.h
- Driver version-query and initialization handling: https://android.googlesource.com/kernel/google-modules/gpu/+/refs/heads/android-gs-raviole-5.10-android13/mali_kbase/mali_kbase_core_linux.c
- Submit-disabled flag: https://nest-open-source.googlesource.com/manifest_repos/mali-driver/+/0f8397eced2de6bc649a9cc32d0fae77a1dc34dc/bifrost/r44p0/kernel/include/uapi/gpu/arm/midgard/mali_base_common_kernel.h

The small standalone implementation is MIT licensed; it does not bundle Arm
driver source or a third-party exploit.
