// SPDX-License-Identifier: MIT
// Model only the documented driver handshake. No real GPU operations.
#include <assert.h>
#include <stdio.h>
#include <string.h>
#define PROBE_TEST
#include "probe.c"
static int scenario, ioctls, closes, writes;
static char logbuf[4096];
static size_t loglen;
long probe_syscall4(word n,word a,word b,word c,word d) {
    if(n==SC_WRITE) {
        assert(a==1 && d==0);
        if(scenario==8 && writes++==0) return -4; // interrupted stdout
        if(scenario==8 && c>3) c=3;             // partial stdout
        assert(loglen+c<sizeof(logbuf)); memcpy(logbuf+loglen,(void*)b,c);
        loglen+=c; logbuf[loglen]=0; return (long)c;
    }
    if(n==SC_OPENAT) {
        assert(a==(word)-100 && !strcmp((char*)b,"/dev/mali0"));
        assert(c==0x80002 && d==0);
        return scenario==1 ? -13 : 7;
    }
    if(n==SC_CLOSE) { assert(a==7); ++closes; return 0; }
    assert(n==SC_IOCTL && a==7 && d==0);
    ++ioctls;
    if(ioctls==1) {
        struct abi_version *v=(void*)c;
        assert(b==VERSION_CHECK && v->major==0 && v->minor==0);
        if(scenario==2) return -25;
        v->major=scenario==3 ? 1 : 11; v->minor=28; return 0;
    }
    if(ioctls==2) {
        assert(b==SET_FLAGS && ((struct context_flags*)c)->flags==2);
        return scenario==4 ? -1 : 0;
    }
    assert(ioctls==3 && b==DDK_VERSION);
    struct ddk_request *q=(void*)c;
    assert(q->size==512 && q->padding==0);
    if(scenario==5) return -75;
    if(scenario==6) return 513;
    if(scenario==7) { memset((void*)(word)q->buffer,'X',512); return 512; }
    const char *v=scenario==9 ? "r32p1\ncontrol" : "r32p1-test";
    memcpy((void*)(word)q->buffer,v,strlen(v)+1); return (long)strlen(v)+1;
}
int main(void) {
    for(scenario=0;scenario<10;++scenario) {
        ioctls=closes=writes=0; loglen=0; logbuf[0]=0;
        int r=probe_main();
        assert(closes==(scenario==1?0:1));
        if(scenario==0 || scenario>=8) {
            assert(r==0 && ioctls==3);
            assert(strstr(logbuf,"RESULT=version read"));
        } else assert(r!=0);
        if(scenario==1) assert(ioctls==0 && strstr(logbuf,"permission denied"));
        if(scenario==2 || scenario==3) assert(ioctls==1);
        if(scenario==4) assert(ioctls==2);
        if(scenario==9) assert(strstr(logbuf,"KERNEL_DDK_VERSION=r32p1?control\n"));
    }
    puts("PASS: ten scenarios; only three permitted driver ioctls; cleanup on every opened-device path");
}
