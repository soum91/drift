// SPDX-License-Identifier: MIT
/* Drift GPU version probe v1. No libc, Android linker, or root required.
 * Only opens /dev/mali0; negotiates the Job Manager ABI; creates/marks a
 * temporary job-submission-disabled context; queries DDK version; closes.
 * No GPU jobs, memory mappings, JIT operations, privilege changes or files
 * are written. Kernel bookkeeping is released when the device is closed.
 */
typedef unsigned short u16;
typedef unsigned int u32;
typedef unsigned long long u64;
typedef unsigned long word;

enum { SC_WRITE=4, SC_CLOSE=6, SC_IOCTL=54, SC_OPENAT=322 };
#define VERSION_CHECK 0xc0048000UL
#define SET_FLAGS     0x40048001UL
#define DDK_VERSION   0x4010800dUL
#define SUBMIT_DISABLED 2U
struct abi_version { u16 major, minor; };
struct context_flags { u32 flags; };
struct ddk_request { u64 buffer; u32 size, padding; };
_Static_assert(sizeof(struct abi_version)==4, "version ABI");
_Static_assert(sizeof(struct context_flags)==4, "flags ABI");
_Static_assert(sizeof(struct ddk_request)==16, "DDK ABI");
_Static_assert(__builtin_offsetof(struct ddk_request,size)==8, "pointer ABI");

#ifdef PROBE_TEST
extern long probe_syscall4(word,word,word,word,word);
#define sc4 probe_syscall4
#else
#if !defined(__arm__) || defined(__aarch64__)
#error Build the release for ARM EABI32
#endif
// GCC may lower fixed-size zero initialization to memset even freestanding.
void *memset(void *p,int value,__SIZE_TYPE__ n) {
    volatile unsigned char *b=p;
    while(n--) *b++=(unsigned char)value;
    return p;
}
static inline long sc4(word number,word a,word b,word c,word d) {
    register word r7 __asm__("r7")=number;
    register word r0 __asm__("r0")=a;
    register word r1 __asm__("r1")=b;
    register word r2 __asm__("r2")=c;
    register word r3 __asm__("r3")=d;
    __asm__ volatile("svc 0" : "+r"(r0) : "r"(r7),"r"(r1),"r"(r2),"r"(r3) : "memory","cc");
    return (long)r0;
}
__attribute__((naked,noreturn,used)) void _start(void) {
    __asm__ volatile("bl probe_main\nmov r7, #248\nsvc 0\nb .\n");
}
#endif

static void output(const char *s,word n) {
    while(n) {
        long r=sc4(SC_WRITE,1,(word)s,n,0);
        if(r==-4) continue;
        if(r<=0 || (word)r>n) return;
        s+=r; n-=(word)r;
    }
}
static void text(const char *s) { word n=0; while(s[n]) ++n; output(s,n); }
static void number(u32 v) {
    static const u32 places[]={1000000000,100000000,10000000,1000000,
        100000,10000,1000,100,10,1};
    int started=0;
    for(word i=0;i<10;++i) {
        char digit='0';
        while(v>=places[i]) { v-=places[i]; ++digit; }
        if(started || digit!='0' || i==9) { output(&digit,1); started=1; }
    }
}
static void failure(const char *stage,long result) {
    text("ERROR="); text(stage); text(" errno="); number((u32)-result);
    if(result==-1 || result==-13) text(" (permission denied)");
    if(result==-25) text(" (ioctl not supported)");
    text("\n");
}
static int finish(word fd,int status) {
    long r=sc4(SC_CLOSE,fd,0,0,0);
    if(r<0) { failure("close",r); return 2; }
    text("DEVICE_CLOSED=OK\n"); return status;
}

__attribute__((used,noinline)) int probe_main(void) {
    struct abi_version version={0,0};
    struct context_flags flags={SUBMIT_DISABLED};
    char version_buffer[512]={0};
    struct ddk_request request={(u64)(word)version_buffer,sizeof(version_buffer),0};
    long fd, r;
    text("DRIFT_GPU_PROBE=1 ARM32\n");
    text("PURPOSE=driver version query; no exploitation\n");
    // AT_FDCWD=-100; O_RDWR | O_CLOEXEC. No O_CREAT, no persistent writes.
    fd=sc4(SC_OPENAT,(word)-100,(word)"/dev/mali0",0x80002,0);
    if(fd<0) { failure("open /dev/mali0",fd); return 2; }
    text("DEVICE_OPEN=OK\n");
    r=sc4(SC_IOCTL,(word)fd,VERSION_CHECK,(word)&version,0);
    if(r<0) { failure("VERSION_CHECK",r); return finish((word)fd,2); }
    text("KERNEL_UK_ABI="); number(version.major); text("."); number(version.minor); text("\n");
    if(version.major!=11) {
        text("STOP=unsupported ABI; expected Job Manager major 11\n");
        return finish((word)fd,3);
    }
    r=sc4(SC_IOCTL,(word)fd,SET_FLAGS,(word)&flags,0);
    if(r<0) { failure("SET_FLAGS submit-disabled",r); return finish((word)fd,2); }
    text("CONTEXT=job submission disabled\n");
    r=sc4(SC_IOCTL,(word)fd,DDK_VERSION,(word)&request,0);
    if(r<0) { failure("GET_DDK_VERSION",r); return finish((word)fd,2); }
    if(r<1 || r>(long)sizeof(version_buffer) || version_buffer[r-1]!=0) {
        text("STOP=invalid DDK version response\n"); return finish((word)fd,3);
    }
    text("KERNEL_DDK_VERSION=");
    for(word i=0;i<(word)r && version_buffer[i];++i) {
        char c=version_buffer[i];
        if((unsigned char)c<32 || (unsigned char)c>126) c='?';
        output(&c,1);
    }
    text("\nRESULT=version read; vulnerability status is unknown\n");
    return finish((word)fd,0);
}
