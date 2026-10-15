// SPDX-License-Identifier: GPL-2.0-or-later
// Actual patched QMK wear-leveling algorithm plus real cache-borrow extension.
// Only its lowest-level backing-store primitives are replaced with byte arrays.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include "../qmk_firmware/quantum/wear_leveling/wear_leveling.c"

// Independent numeric oracle for the two persisted layouts, not header aliases.
#define TEST_BASE 1024U
#if IG_BOARD >= 10 && IG_BOARD <= 16
#define TEST_SLOT_BYTES 7680U
#define TEST_OTHER_PAYLOAD1 8224U
#else
#define TEST_SLOT_BYTES 7168U
#define TEST_OTHER_PAYLOAD1 8736U
#endif
#define TEST_SLOT1 (TEST_BASE + TEST_SLOT_BYTES)
#define TEST_PAYLOAD_BYTES (TEST_SLOT_BYTES - 32U)
#define TEST_PAYLOAD0 (TEST_BASE + 32U)
#define TEST_PAYLOAD1 (TEST_SLOT1 + 32U)
#define TEST_MARKER1 (TEST_PAYLOAD1 - 4U)

static uint8_t flash[WEAR_LEVELING_BACKING_SIZE];
static uint8_t initial_cache[WEAR_LEVELING_LOGICAL_SIZE], initial_flash[WEAR_LEVELING_BACKING_SIZE];
static unsigned checks, reads, writes, erases, unlocks, locks;
static unsigned fail_read, fail_write;
static bool fail_after_write, fail_unlock, fail_lock, fail_erase;
static bool verify_invalid_consolidation;
static uint32_t observed_payload;
static void check(bool ok,const char *message) {
    ++checks;
    if(!ok) { fprintf(stderr,"FAIL width%d check%u: %s\n",BACKING_STORE_WRITE_SIZE,checks,message); exit(1); }
}
static void bytes_equal(const uint8_t *a,const uint8_t *b,size_t length,const char *message) {
    for(size_t i=0;i<length;i++) check(a[i]==b[i],message);
}
bool backing_store_init(void) { return true; }
bool backing_store_unlock(void) { ++unlocks; return !fail_unlock; }
bool backing_store_lock(void) { ++locks; return !fail_lock; }
bool backing_store_erase(void) {
    ++erases;
    if(verify_invalid_consolidation) {
        check(ig_storage_borrowed,"consolidation still owns stage");
        for(unsigned i=0;i<4;i++) check(wear_leveling.cache[observed_payload-4+i]==0,"target marker invalid before consolidation");
        bytes_equal(wear_leveling.cache+TEST_BASE,initial_cache+TEST_BASE,TEST_SLOT_BYTES,"active slot unchanged during consolidation");
    }
    if(fail_erase) return false;
    memset(flash,0,sizeof(flash)); return true;
}
bool backing_store_write(uint32_t address,backing_store_int_t value) {
    check(address % BACKING_STORE_WRITE_SIZE==0 && address<=sizeof(flash)-sizeof(value),"physical write aligned and bounded");
    ++writes;
    if(writes==fail_write && !fail_after_write) return false;
    memcpy(flash+address,&value,sizeof(value));
    return writes!=fail_write;
}
bool backing_store_read(uint32_t address,backing_store_int_t *value) {
    check(address % BACKING_STORE_WRITE_SIZE==0 && address<=sizeof(flash)-sizeof(*value),"physical read aligned and bounded");
    ++reads; if(reads==fail_read) return false;
    memcpy(value,flash+address,sizeof(*value)); return true;
}
static void counters(void) { reads=writes=erases=unlocks=locks=0; fail_read=fail_write=0; fail_after_write=fail_unlock=fail_lock=fail_erase=false; }
static void reset(void) {
    memset(flash,0,sizeof(flash)); memset(&wear_leveling,0,sizeof(wear_leveling));
    ig_storage_borrowed=ig_storage_fault=ig_storage_read_only_reload=false;
    ig_storage_phase=0; verify_invalid_consolidation=false;
    counters(); check(wear_leveling_init()!=WEAR_LEVELING_FAILED,"empty EEPROM boot");
    uint8_t data[TEST_SLOT_BYTES];
    for(size_t i=0;i<sizeof(data);i++) data[i]=(uint8_t)(i*29+17);
    check(wear_leveling_write(TEST_BASE,data,sizeof(data))!=WEAR_LEVELING_FAILED,"seed active slot through actual log");
    for(size_t i=0;i<sizeof(data);i++) data[i]=(uint8_t)(i*13+23);
    check(wear_leveling_write(TEST_SLOT1,data,sizeof(data))!=WEAR_LEVELING_FAILED,"seed inactive slot through actual log");
    const uint8_t marker[4]={0xA5,0x5A,0x5A,0xA5};
    check(wear_leveling_write(TEST_MARKER1,marker,4)!=WEAR_LEVELING_FAILED,"old inactive marker valid");
    check(wear_leveling_init()!=WEAR_LEVELING_FAILED,"seed readback");
    memcpy(initial_cache,wear_leveling.cache,sizeof(initial_cache)); memcpy(initial_flash,flash,sizeof(initial_flash)); counters();
}
static uint8_t *begin(size_t length) {
    uint8_t *stage=ig_storage_stage_begin(TEST_PAYLOAD1,length);
    check(stage==wear_leveling.cache+TEST_PAYLOAD1,"borrow is exactly inactive cache view");
    for(size_t i=0;i<length;i++) { check(stage[i]==0,"borrow starts empty"); stage[i]=(uint8_t)(i*7+63); }
    return stage;
}
static wear_leveling_status_t phase_write(unsigned phase,uint8_t *stage,size_t length) {
    const uint8_t zero[4]={0}, marker[4]={0xA5,0x5A,0x5A,0xA5};
    uint8_t header[28]; memset(header,0x35,sizeof(header));
    switch(phase) {
        case 0:return ig_storage_stage_write(TEST_MARKER1,zero,4);
        case 1:return ig_storage_stage_write(TEST_PAYLOAD1,stage,length);
        case 2:return ig_storage_stage_write(TEST_SLOT1,header,28);
        default:return ig_storage_stage_write(TEST_MARKER1,marker,4);
    }
}
static void abort_checks(void) {
    const size_t lengths[]={1,3780,4184,5140,5944,5968,6336,6360,7032,7056,TEST_PAYLOAD_BYTES
#if IG_BOARD >= 10
        ,7136,7137,7392,7647
#endif
    };
    for(unsigned n=0;n<sizeof(lengths)/sizeof(*lengths);n++) {
        reset(); uint8_t *stage=begin(lengths[n]); uint8_t one=1;
        check(ig_storage_stage_begin(TEST_PAYLOAD0,1)==NULL,"second borrow rejected");
        check(wear_leveling_write(0,&one,1)==WEAR_LEVELING_FAILED,"ordinary write blocked");
        check(wear_leveling_erase()==WEAR_LEVELING_FAILED,"ordinary erase blocked");
        check(wear_leveling_init()==WEAR_LEVELING_FAILED,"ordinary init blocked while borrowed");
        check(ig_storage_stage_write(TEST_PAYLOAD1,stage,lengths[n])==WEAR_LEVELING_FAILED,"payload cannot precede invalidation");
        check(!writes&&!erases&&!unlocks&&!locks&&!reads,"upload and invalid operations are RAM only");
        bytes_equal(flash,initial_flash,sizeof(flash),"physical backing unchanged before commit");
        bytes_equal(wear_leveling.cache+TEST_BASE,initial_cache+TEST_BASE,TEST_SLOT_BYTES,"active slot remains readable during upload");
        check(ig_storage_stage_finish(false)!=WEAR_LEVELING_FAILED,"abort replays original backing");
        check(!writes&&!erases&&!unlocks&&!locks,"abort performs zero flash write/erase/unlock");
        bytes_equal(wear_leveling.cache,initial_cache,sizeof(initial_cache),"abort restores every cache byte");
        bytes_equal(flash,initial_flash,sizeof(flash),"abort preserves every backing byte");
    }
    reset(); check(!ig_storage_stage_begin(0,1)&&!ig_storage_stage_begin(TEST_PAYLOAD0,0)&&!ig_storage_stage_begin(TEST_PAYLOAD1,(TEST_PAYLOAD_BYTES+1))&&!ig_storage_stage_begin(UINT32_MAX,1),"borrow address and length limits");
    check(!ig_storage_stage_begin(TEST_OTHER_PAYLOAD1,1),"other board family's slot address is rejected");
    check(!ig_storage_stage_begin(TEST_PAYLOAD0,SIZE_MAX)&&!ig_storage_stage_begin(TEST_PAYLOAD1,SIZE_MAX),"oversized size_t cannot wrap bounds");
    for (uint32_t address=0;address<WEAR_LEVELING_LOGICAL_SIZE;address++) {
        if(address==TEST_PAYLOAD0 || address==TEST_PAYLOAD1) continue;
        check(ig_storage_stage_begin(address,1)==NULL,"only the two exact payload starts can be borrowed");
    }
    check(ig_storage_view(16384,0)!=NULL && !ig_storage_view(16384,1) && !ig_storage_view(UINT32_MAX,1),"read-only view range limits");
    reset(); begin(TEST_PAYLOAD_BYTES); fail_read=1;
    check(ig_storage_stage_finish(false)==WEAR_LEVELING_FAILED && ig_storage_fault,"abort read failure latches fault");
    check(!writes&&!erases&&!unlocks&&!locks,"failed abort never repairs by writing");
    uint8_t one=1; check(!ig_storage_stage_begin(TEST_PAYLOAD1,1)&&wear_leveling_write(0,&one,1)==WEAR_LEVELING_FAILED&&wear_leveling_erase()==WEAR_LEVELING_FAILED,"fault prevents dirty cache retries");
    reset(); begin(TEST_PAYLOAD_BYTES); fail_read=WEAR_LEVELING_LOGICAL_SIZE/BACKING_STORE_WRITE_SIZE+1;
    check(ig_storage_stage_finish(false)==WEAR_LEVELING_FAILED && !writes&&!erases,"checksum read error propagates without repair");
    reset(); begin(TEST_PAYLOAD_BYTES);
    // An invalid entry at the existing end would normally trigger recovery erase.
    flash[wear_leveling.write_address]=0xFF;
    check(ig_storage_stage_finish(false)==WEAR_LEVELING_FAILED && !writes&&!erases&&!unlocks,"corrupt log abort is strictly read-only");
    reset(); begin(TEST_PAYLOAD_BYTES);
    // Simulate power loss during upload: discard all RAM, keep physical bytes.
    memset(&wear_leveling,0,sizeof(wear_leveling)); ig_storage_borrowed=ig_storage_fault=false;
    check(wear_leveling_init()!=WEAR_LEVELING_FAILED,"power loss before COMMIT boots previous configuration");
    bytes_equal(wear_leveling.cache,initial_cache,sizeof(initial_cache),"power loss discards every staged byte");
    bytes_equal(flash,initial_flash,sizeof(initial_flash),"power loss during upload never changed backing");
    check(!writes&&!erases,"power loss recovery from clean upload performs no writes");
    reset(); uint8_t *stage=ig_storage_stage_begin(TEST_PAYLOAD0,TEST_PAYLOAD_BYTES);
    check(stage!=NULL,"first-slot payload can be borrowed"); memset(stage,0xB5,TEST_PAYLOAD_BYTES);
    check(ig_storage_stage_finish(false)!=WEAR_LEVELING_FAILED,"first-slot abort restores prior cache");
    bytes_equal(wear_leveling.cache,initial_cache,sizeof(initial_cache),"first-slot borrow restoration exact");
    reset(); begin(TEST_PAYLOAD_BYTES);
    check(ig_storage_stage_finish(true)==WEAR_LEVELING_FAILED && !writes&&!erases&&!unlocks,"commit finish without first write cannot trigger recovery writes");
}
static void commit_checks(void) {
    const size_t lengths[]={3780,4184,5140,5944,5968,6336,6360,7032,7056,TEST_PAYLOAD_BYTES
#if IG_BOARD >= 10
        ,7136,7137,7392,7647
#endif
    };
    for(unsigned n=0;n<sizeof(lengths)/sizeof(*lengths);n++) {
        reset(); size_t length=lengths[n]; uint8_t *stage=begin(length);
        uint8_t expected[TEST_PAYLOAD_BYTES]; memcpy(expected,stage,length);
        check(phase_write(0,stage,length)!=WEAR_LEVELING_FAILED,"marker invalidated first");
        unsigned before=writes;
        check(phase_write(1,stage,length)!=WEAR_LEVELING_FAILED && writes>before,"identical cache still forces real payload write");
        check(phase_write(2,stage,length)!=WEAR_LEVELING_FAILED && phase_write(3,stage,length)!=WEAR_LEVELING_FAILED,"header then marker committed");
        check(ig_storage_stage_finish(true)!=WEAR_LEVELING_FAILED,"committed state reloaded from physical backing");
        bytes_equal(wear_leveling.cache+TEST_PAYLOAD1,expected,length,"entire staged payload survives backing replay");
        bytes_equal(wear_leveling.cache+TEST_BASE,initial_cache+TEST_BASE,TEST_SLOT_BYTES,"active slot not changed by successful commit");
    }
    reset(); uint8_t *stage=begin(TEST_PAYLOAD_BYTES);
    // Deterministically force a consolidation at the first invalidation append.
    wear_leveling.write_address=WEAR_LEVELING_BACKING_SIZE-BACKING_STORE_WRITE_SIZE;
    observed_payload=TEST_PAYLOAD1; verify_invalid_consolidation=true;
    check(phase_write(0,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED && erases==1,"invalidation can safely consolidate staged cache");
    verify_invalid_consolidation=false;
    check(phase_write(1,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED&&phase_write(2,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED&&phase_write(3,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED,"commit continues after invalidated consolidation");
    check(ig_storage_stage_finish(true)!=WEAR_LEVELING_FAILED,"consolidated staged payload reload");
    reset();
    uint8_t zero[4]={0}; check(wear_leveling_write(TEST_MARKER1,zero,4)!=WEAR_LEVELING_FAILED,"prepare already-invalid target marker");
    counters(); stage=begin(TEST_PAYLOAD_BYTES); unsigned before=writes;
    check(phase_write(0,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED && writes==before,"unchanged invalid marker may skip physical write");
    check(phase_write(1,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED && writes>before,"payload flush is forced even after skipped marker");
    check(phase_write(2,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED&&phase_write(3,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED&&ig_storage_stage_finish(true)!=WEAR_LEVELING_FAILED,"skipped marker still yields fully persisted staged slot");
}
static void failures(void) {
    for(unsigned phase=0;phase<4;phase++) for(unsigned after=0;after<2;after++) {
        reset(); uint8_t *stage=begin(TEST_PAYLOAD_BYTES);
        for(unsigned p=0;p<phase;p++) check(phase_write(p,stage,TEST_PAYLOAD_BYTES)!=WEAR_LEVELING_FAILED,"prepare fault phase");
        fail_write=writes+1; fail_after_write=after!=0;
        check(phase_write(phase,stage,TEST_PAYLOAD_BYTES)==WEAR_LEVELING_FAILED,"physical failure propagates in every commit phase");
        unsigned stopped=writes;
        check(phase_write(phase,stage,TEST_PAYLOAD_BYTES)==WEAR_LEVELING_FAILED&&writes==stopped,"failed phase cannot blindly retry dirty cache");
        fail_write=0;
        check(ig_storage_stage_finish(true)==WEAR_LEVELING_FAILED&&ig_storage_fault,"reloaded commit still reports uncertain write failure");
        bytes_equal(wear_leveling.cache+TEST_BASE,initial_cache+TEST_BASE,TEST_SLOT_BYTES,"valid previous slot survives injected log write failure");
        uint8_t one=1; check(wear_leveling_write(0,&one,1)==WEAR_LEVELING_FAILED&&!ig_storage_stage_begin(TEST_PAYLOAD1,1),"post-failure writes remain blocked");
    }
    reset(); uint8_t *stage=begin(TEST_PAYLOAD_BYTES); fail_unlock=true;
    check(phase_write(0,stage,TEST_PAYLOAD_BYTES)==WEAR_LEVELING_FAILED && !writes&&!erases,"unlock failure does not persist dirty cache");
    fail_unlock=false; check(ig_storage_stage_finish(true)==WEAR_LEVELING_FAILED,"unlock failure retained after reload");
    reset(); stage=begin(TEST_PAYLOAD_BYTES); fail_lock=true;
    check(phase_write(0,stage,TEST_PAYLOAD_BYTES)==WEAR_LEVELING_FAILED,"lock failure remains an uncertain result");
    fail_lock=false; check(ig_storage_stage_finish(true)==WEAR_LEVELING_FAILED,"lock failure retained");
    reset(); stage=begin(TEST_PAYLOAD_BYTES); phase_write(0,stage,TEST_PAYLOAD_BYTES); fail_read=reads+1;
    check(ig_storage_stage_finish(true)==WEAR_LEVELING_FAILED&&ig_storage_fault,"post-write readback failure is not success");
}
int main(void) {
    check(IG_STORAGE_BASE==1024 && IG_STORAGE_HEADER_BYTES==32 && IG_STORAGE_SLOT_BYTES==TEST_SLOT_BYTES,"persisted layout matches independent board-family constants");
    check(IG_STORAGE_PAYLOAD_BYTES==TEST_PAYLOAD_BYTES && IG_STORAGE_PAYLOAD0==1056 && IG_STORAGE_PAYLOAD1==TEST_PAYLOAD1,"common header and cache-borrow geometry agree");
    check(TEST_BASE+2*TEST_SLOT_BYTES==(IG_BOARD>=10?16384:15360),"slot end preserves old gap or exactly fills new logical EEPROM");
    abort_checks(); commit_checks(); failures();
    printf("PASS: board%d width%d slot%u, %u actual QMK storage checks; no hardware.\n",IG_BOARD,BACKING_STORE_WRITE_SIZE,TEST_SLOT_BYTES,checks);
    return 0;
}
