// SPDX-License-Identifier: GPL-2.0-or-later
// Actual runtime plus pure bank codec; every hardware/storage boundary is mocked.
#define main unused_legacy_port_main
#include "ports/port_checks.c"
#undef main

static uint8_t profile_flash[IG_PB_BANK_SIZE], profile_identity[64];
static unsigned profile_writes, profile_fail_at, profile_replays;
static bool profile_ready = true, profile_open;
const uint8_t *ig_profile_flash_bytes(void) { return profile_ready ? profile_flash : NULL; }
const uint8_t *ig_profile_flash_identity(void) { return profile_ready ? profile_identity : NULL; }
bool ig_profile_flash_ready(void) { return profile_ready; }
bool ig_profile_flash_begin(void) { if (!profile_ready || profile_open) return false; profile_open = true; return true; }
void ig_profile_flash_end(void) { profile_open = false; }
static bool profile_write(void *context, size_t offset, const uint8_t *page) {
    (void)context;
    check(profile_open && offset % 256 == 0 && offset <= sizeof(profile_flash) - 256, "physical callback confined to one complete page inside the bank");
    // WB32 first erases to zero, then programs the full page.
    memset(profile_flash + offset, 0, 256); profile_writes++;
    if (profile_writes == profile_fail_at) return false;
    memcpy(profile_flash + offset, page, 256); return true;
}
static bool profile_empty(void *context, size_t offset) { uint8_t page[256]; memset(page,255,256); return profile_write(context,offset,page); }
const ig_pb_io *ig_profile_flash_io(void) { static const ig_pb_io io={NULL,profile_empty,profile_write}; return &io; }

static uint8_t profile_begin(uint8_t slot,const uint8_t *data,uint16_t length,uint32_t token) {
    uint8_t p[24]={slot,0}; put16(p+2,length); ig_profile_put64(p+4,ig_profile_views[slot-1].generation);
    put32(p+12,ig_profile_crcs[slot-1]); put32(p+16,crc_buffer(data,length)); put32(p+20,token);
    uint8_t result=command(20,p,24);
    if(!result) check(response[6]==24 && !memcmp(response+8,p,24),"BEGIN echoes exact explicit destination, generation, CRCs and owner token");
    return result;
}
static void profile_upload(uint8_t slot,const uint8_t *data,uint16_t length,uint32_t token) {
    check(profile_begin(slot,data,length,token)==0,"explicit profile begin");
    for(uint16_t offset=0;offset<length;) {
        uint8_t n=length-offset>16?16:length-offset,p[24]={0};
        put32(p,token);p[4]=slot;p[5]=n;put16(p+6,offset);memcpy(p+8,data+offset,n);
        check(command(21,p,8+n)==0 && response[6]==8 && u16(response+14)==offset+n,"explicit sequential chunk");
        check(command(21,p,8+n)==0,"identical repeated chunk is idempotent");
        p[8]^=1;check(command(21,p,8+n)==2,"conflicting duplicate rejected");
        offset+=n;
    }
}
static uint8_t profile_commit(uint8_t slot,uint32_t token) {
    uint8_t p[20]={0};put32(p,token);p[4]=slot;ig_profile_put64(p+8,ig_profile_expected_generation);put32(p+16,stage_crc);
    return command(22,p,20);
}
static void profile_abort(uint8_t slot,uint32_t token) {
    uint8_t p[8]={0};put32(p,token);p[4]=slot;check(command(23,p,8)==0 && response[6]==8,"owner abort with read-only replay");profile_replays++;
}
static void profile_select(uint8_t slot) {
    pressed[11]&=~0x80U;pressed[12]&=~0x80U;pressed[13]&=~0x80U;
    if(slot)pressed[10+slot]|=0x80U;
    housekeeping_task_user();now+=30;housekeeping_task_user();
}
static void profile_status(uint8_t slot) { check(command(18,&slot,1)==0 && response[6]==24,"profile status succeeds"); }
static void profile_finish_auto(void) {
    now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();now+=100;housekeeping_task_user();housekeeping_task_user();
}
static void profile_usb_guard(unsigned reason,bool busy) {
    if(reason==0){if(busy)pressed[0]|=1;else pressed[0]=0;}
    if(reason==1){macro_running=busy;macro_deadline=now+100000U;}
    if(reason==2)knob_pending=busy;
    if(reason==3)lighting_suspended=busy;
    if(reason==4){light_test_active=busy;light_test_started=now;}
    if(reason==5){diagnostic_active=busy;diagnostic_refreshed=now;}
    if(reason==6){
        if(busy)check(profile_begin(3,ig_profile_defaults,IG_SIZE,151)==0,"start upload to defer automatic USB change");
        else profile_abort(3,151);
    }
    if(reason==7)mock_usb_active=!busy;
}
static void check_profile_usb_activation(void) {
    memset(profile_flash,255,sizeof(profile_flash));mock_usb_active=true;boot(true);
    uint8_t config[IG_MAX_SIZE]={0};memcpy(config,ig_profile_defaults,IG_SIZE);
    put16(config+4,2);config[24]=8;config[25]=7;
    for(unsigned layer=0;layer<4;layer++)config[IG_SIZE+layer*8+3]=3;
    for(unsigned at=IG_OVERRIDE_OFFSET;at<IG_MAX_SIZE;at+=3)config[at]=255;
    check(validate_config(config,IG_MAX_SIZE),"real schema2 timing payload validates before bank save");
    profile_select(1);profile_upload(2,config,IG_MAX_SIZE,131);check(profile_commit(2,131)==0,"save inactive eight-ms profile");
    unsigned writes_before=profile_writes,restarts=mock_reconnect_count,requests=mock_reconnect_requests;
    boot(false);mock_usb_active=false;profile_select(2);
    check(ig_profile_active==2 && active_interval==1 && mock_saved_interval==8 && ig_profile_interval_state==1 && !reconnect,
          "cold boot in profile2 waits for USB_ACTIVE after reading its stored polling rate");
    for(unsigned i=0;i<5;i++){now+=200;housekeeping_task_user();}
    check(mock_reconnect_count==restarts && mock_reconnect_requests==requests,"unconfigured USB is never restarted");
    mock_usb_active=true;now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();
    check(reconnect && ig_profile_interval_state==2 && mock_reconnect_requests==requests+1,"stable cold-boot profile schedules exactly one USB change");
    now+=99;housekeeping_task_user();check(active_interval==1 && reconnect,"existing USB grace interval is preserved");
    now++;housekeeping_task_user();check(active_interval==8 && !reconnect && mock_reconnect_count==restarts+1,"cold-boot selected rate applies without a host command");
    for(unsigned i=0;i<20;i++){now+=100;housekeeping_task_user();}
    check(mock_reconnect_requests==requests+1 && mock_reconnect_count==restarts+1,"completed activation never causes a reconnect loop");
    // Exercise each guard both before scheduling and immediately before execution.
    for(unsigned reason=0;reason<8;reason++){
        mock_usb_active=true;profile_select(1);profile_finish_auto();
        check(active_interval==1 && !reconnect,"return to profile1 rate before guard case");
        mock_usb_active=false;profile_select(2);check(ig_profile_interval_state==1 && !reconnect,"new activation remains a deferred attempt");
        requests=mock_reconnect_requests;restarts=mock_reconnect_count;
        mock_usb_active=true;profile_usb_guard(reason,true);now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();
        check(!reconnect && mock_reconnect_requests==requests,"unsafe state prevents automatic scheduling");
        profile_usb_guard(reason,false);housekeeping_task_user();
        if(reason==6){
            check(!ig_profile_interval_state && !reconnect,"explicit upload owns and cancels the waiting automatic plan");
            profile_select(1);profile_select(2);now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();
        }
        check(reconnect && mock_reconnect_requests==requests+1,"ending guard schedules the still-current activation once");
        if(reason==6){
            // A pending USB restart itself rejects a new upload. Test that
            // boundary rather than fabricating an impossible successful BEGIN.
            check(profile_begin(3,config,IG_MAX_SIZE,152)==0 && !reconnect,"explicit BEGIN takes over the pending automatic reconnect");
            profile_abort(3,152);
            check(!ig_profile_interval_state,"ending explicit upload does not silently re-arm the cancelled automatic plan");
            profile_select(1);profile_select(2);now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();
        }else profile_usb_guard(reason,true);
        now+=100;housekeeping_task_user();
        if(reason!=6){
            check(active_interval==1 && reconnect && mock_reconnect_count==restarts,"final execution guard defers USB while operation is busy");
            profile_usb_guard(reason,false);housekeeping_task_user();
        }
        check(active_interval==8 && !reconnect && mock_reconnect_count==restarts+1,"deferred USB execution resumes once when safe");
    }
    // A physical race discards the old activation plan, including bounce back.
    profile_select(1);profile_finish_auto();
    profile_select(2);now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();check(reconnect,"second profile's reconnect is scheduled");
    requests=mock_reconnect_requests;restarts=mock_reconnect_count;
    pressed[12]=0;pressed[11]=0x80U;housekeeping_task_user();
    check(!reconnect && !ig_profile_interval_state && ig_profile_active==2,"contact transition cancels the old bound plan before execution");
    pressed[11]=0;pressed[12]=0x80U;housekeeping_task_user();now+=30;housekeeping_task_user();now+=200;housekeeping_task_user();
    check(ig_profile_active==2 && active_interval==1 && mock_reconnect_requests==requests && mock_reconnect_count==restarts,
          "bounce back does not automatically retry a discarded activation");
    profile_select(1);profile_select(2);now+=IG_PROFILE_USB_GRACE_MS;housekeeping_task_user();check(reconnect,"a genuinely new stable activation can plan again");
    // Check a changed snapshot right at execution, not just at scheduling.
    ig_profile_crcs[1]^=1;now+=100;housekeeping_task_user();
    check(!reconnect && active_interval==1,"changed snapshot cancels pending USB action");
    ig_profile_crcs[1]^=1;now+=100;housekeeping_task_user();
    check(!reconnect && active_interval==1,"restored snapshot alone does not re-arm the discarded attempt");
    // Equal rates require no USB action even on a genuine profile change.
    profile_select(1);requests=mock_reconnect_requests;profile_select(3);now+=100;housekeeping_task_user();
    check(ig_profile_active==3 && active_interval==1 && !reconnect && requests==mock_reconnect_requests,"same-rate profile switch avoids USB disruption");
    check(profile_writes==writes_before && !writes,"boot, all guards, races and USB activation never write flash");
    // Reading an explicit inactive slot remains coherent while the physical
    // selector activates another slot. Only real payload reads renew this lease.
    for(unsigned i=12;i<28;i++)profile_identity[i]=(uint8_t)i;
    put32(profile_identity+60,ig_pb_crc32(profile_identity,60));
    uint8_t read[16]={1,12};put32(read+12,ig_profile_crcs[0]);
    now=UINT32_MAX-2000U;check(command(19,read,16)==0,"start inactive-slot snapshot before timer wrap");
    profile_select(2);requests=mock_reconnect_requests;restarts=mock_reconnect_count;
    for(unsigned i=0;i<10;i++){
        now+=1000;check(command(19,read,16)==0 && !memcmp(response+20,ig_profile_defaults,12),"snapshot bytes stay bound to explicit slot across physical switch and timer wrap");
        profile_status(0);check(command(24,NULL,0)==0,"normal status and identity polling during snapshot");housekeeping_task_user();
    }
    check(active_interval==1 && !reconnect && requests==mock_reconnect_requests,"ongoing explicit snapshot prevents automatic USB disconnection");
    uint32_t read_at=ig_profile_read_activity;
    read[12]^=1;now+=4999;check(command(19,read,16)==8 && ig_profile_read_activity==read_at,"failed snapshot request cannot extend read lease");read[12]^=1;
    profile_status(0);check(command(24,NULL,0)==0 && ig_profile_read_activity==read_at,"status and identity monitor never renews read lease");housekeeping_task_user();
    check(!reconnect,"automatic USB change waits until successful snapshot activity is quiet");
    now++;housekeeping_task_user();check(reconnect && requests+1==mock_reconnect_requests,"read lease expires across timer wrap");
    now+=100;housekeeping_task_user();check(active_interval==8 && mock_reconnect_count==restarts+1,"selected profile rate applies after snapshot completion");housekeeping_task_user();
    // A COMMIT of the current slot never disconnects USB during the host's full
    // readback. Only its explicitly bound RAW8 applies the newly saved rate.
    config[24]=4;profile_upload(2,config,IG_MAX_SIZE,161);check(profile_commit(2,161)==0,"save different rate to current slot");
    writes_before=profile_writes;requests=mock_reconnect_requests;restarts=mock_reconnect_count;
    housekeeping_task_user();check(mock_saved_interval==4 && active_interval==8 && !ig_profile_interval_state && !reconnect,"same-slot commit refresh has no automatic reconnect plan");
    memset(read,0,sizeof(read));read[0]=2;ig_profile_put64(read+4,ig_profile_views[1].generation);put32(read+12,ig_profile_crcs[1]);
    for(uint16_t offset=0;offset<IG_MAX_SIZE;){
        uint8_t n=IG_MAX_SIZE-offset>12?12:IG_MAX_SIZE-offset;read[1]=n;put16(read+2,offset);
        now+=10;check(command(19,read,16)==0 && !memcmp(response+20,config+offset,n),"complete post-COMMIT snapshot matches exact saved bytes");
        profile_status(0);check(command(24,NULL,0)==0,"post-COMMIT monitor remains available");housekeeping_task_user();offset+=n;
    }
    check(!reconnect && active_interval==8 && mock_reconnect_requests==requests && mock_reconnect_count==restarts,
          "no automatic restart during or after complete current-slot readback");
    uint8_t bind[16]={2};ig_profile_put64(bind+4,ig_profile_views[1].generation);put32(bind+12,ig_profile_crcs[1]);
    check(command(8,bind,16)==0 && reconnect,"explicit bound RAW8 can follow successful readback immediately");
    now+=100;housekeeping_task_user();check(active_interval==4 && mock_reconnect_count==restarts+1,"explicit acknowledged reconnect ignores completed read lease");
    // A fault discards an automatic plan and does not create retry loops.
    profile_select(1);profile_finish_auto();storage_ok=false;profile_select(2);
    requests=mock_reconnect_requests;now+=5000;housekeeping_task_user();
    check(active_interval==1 && !reconnect && !ig_profile_interval_state,"storage fault prevents automatic USB change");
    storage_ok=true;now+=5000;housekeeping_task_user();
    check(!reconnect && requests==mock_reconnect_requests,"clearing fault does not retry discarded activation");
    check(profile_writes==writes_before && !writes,"read leases, explicit USB update and faults never change flash");
}

static void check_identity(void) {
    memset(profile_identity,0,64);memcpy(profile_identity,"IGIDENT1",8);profile_identity[8]=1;profile_identity[9]=16;
    for(uint8_t i=12;i<60;i++)profile_identity[i]=i;
    put32(profile_identity+60,ig_pb_crc32(profile_identity,60));
    check(command(24,NULL,0)==0 && response[6]==20 && response[8]==1 && response[9]==16 && !response[10] && !response[11],"validated identity envelope");
    check(!memcmp(response+12,profile_identity+12,16),"only installation GUID is returned");
    for(unsigned i=0;i<64;i++) { profile_identity[i]^=1;check(command(24,NULL,0)==7,"every corrupted identity byte rejected");profile_identity[i]^=1; }
    for(unsigned byte=0;byte<256;byte+=255) {
        memset(profile_identity+12,byte,16);put32(profile_identity+60,ig_pb_crc32(profile_identity,60));
        check(command(24,NULL,0)==7,"zero or FF GUID is rejected even with a correct CRC");
    }
}

int main(int argc,char **argv) {
    check(argc==2,"default serializer fixture supplied");check(load(argv[1],defaults)==IG_SIZE,"default schema1 size");
    memset(profile_flash,255,sizeof(profile_flash));boot(true);
    for(uint16_t i=0;i<IG_SIZE;i++) { if(ig_profile_defaults[i]!=default_byte(i)) fprintf(stderr,"Default mismatch at%u: %u != %u\n",i,ig_profile_defaults[i],default_byte(i)); check(ig_profile_defaults[i]==default_byte(i),"flash fallback exactly matches computed default bytes"); }
    check(validate_config(ig_profile_defaults,sizeof(ig_profile_defaults)),"immutable default is semantically valid");
    check(!writes && !profile_writes && !ig_profile_active,"boot reads only and never guesses profile1");
    check(command(11,NULL,0)==0 && (u16(response+10)&256),"capability explicitly advertises new protocol");
    uint8_t zero=0;check(command(18,&zero,1)==3,"active query before stable switch is busy");
    for(uint8_t slot=1;slot<=3;slot++) {
        profile_status(slot);check(response[14]==slot && (response[13]&3)==3 && !ig_profile_u64(response+16),"every fresh slot reads immutable legacy/default generation0");
    }
    profile_select(3);check(ig_profile_active==3 && !writes && !profile_writes,"cold boot in3 activates3 without flash writes");
    // Verify recovery remains accessible with every real selector position.
    for(uint8_t slot=1;slot<=3;slot++) {
        profile_select(slot); pressed[reference_recovery[0][0]]|=(matrix_row_t)1U<<reference_recovery[0][1];pressed[reference_recovery[1][0]]|=(matrix_row_t)1U<<reference_recovery[1][1];
        check(recovery_chord_held(),"known selector contact cannot block fixed recovery chord");
        pressed[0]|=1;check(!recovery_chord_held(),"additional real key still blocks recovery");pressed[0]&=~1U;
        pressed[2]|=8;check(!recovery_chord_held(),"unverified unused cell is not ignored by recovery");pressed[2]&=~8U;
        pressed[reference_recovery[0][0]]=0;pressed[reference_recovery[1][0]]=0;
    }
    check_identity();
    uint8_t config[IG_SIZE];memcpy(config,defaults,IG_SIZE);config[IG_RGB_OFFSET]=23;
    uint8_t old_legacy[sizeof(backing)];memcpy(old_legacy,backing,sizeof(backing));
    profile_select(1);profile_upload(2,config,IG_SIZE,0x12345678);
    unsigned before=profile_writes;uint16_t received=stage_received;
    // Legacy APIs cannot alter or abort the new owner-bound upload.
    for(uint8_t cmd=3;cmd<=6;cmd++)check(command(cmd,NULL,0)==9 && stage_open && stage_received==received,"legacy implicit mutation is rejected");
    check(profile_begin(3,config,IG_SIZE,99)==3 && stage_received==received && ig_profile_target==2,"another BEGIN cannot replace the upload");
    uint8_t wrong_abort[8]={0};put32(wrong_abort,99);wrong_abort[4]=2;
    check(command(23,wrong_abort,8)==8 && stage_open,"non-owner cannot abort");
    check(profile_commit(2,99)==8 && stage_open,"non-owner cannot commit");
    profile_select(3);check(ig_profile_active==1 && ig_profile_selector.requested==3,"upload defers physical switch");
    check(profile_commit(2,0x12345678)==0 && response[6]==16 && response[8]==2 && ig_profile_u64(response+12)==1,"commit remains fixed to explicit slot2 after physical movement");
    check(profile_writes==before+64 && !writes && !memcmp(old_legacy,backing,sizeof(backing)),"one bank commit preserves legacy backing exactly");
    check(!stage_open && !borrowed && ig_profile_active==1,"commit replays RAM and defers runtime activation to housekeeping");
    housekeeping_task_user();check(ig_profile_active==3 && active_byte(IG_RGB_OFFSET)==255,"pending physical3 activates unchanged slot3");
    profile_select(2);check(active_byte(IG_RGB_OFFSET)==23,"saved slot2 applied with its complete configuration");
    // Read generation/CRC/offset binding is independent of active selection.
    uint8_t read[16]={2,12,0,0};ig_profile_put64(read+4,1);put32(read+12,ig_profile_crcs[1]);
    check(command(19,read,16)==0 && response[6]==24 && !memcmp(response+8,read,12) && !memcmp(response+20,config,12),"explicit snapshot chunk");
    read[4]=0;check(command(19,read,16)==8,"stale generation cannot read");read[4]=1;read[12]^=1;check(command(19,read,16)==8,"stale CRC cannot read");
    // Every safety state defers both selector activation and bank COMMIT.
    for(unsigned reason=0;reason<7;reason++) {
        profile_select(2);profile_upload(1,config,IG_SIZE,41);
        if(reason==0)pressed[0]|=1;
        if(reason==1)macro_running=true;
        if(reason==2)knob_pending=true;
        if(reason==3)reconnect=true;
        if(reason==4)lighting_suspended=true;
        if(reason==5){light_test_active=true;light_test_started=now;}if(reason==6){diagnostic_active=true;diagnostic_refreshed=now;}
        before=profile_writes;check(profile_commit(1,41)==3 && profile_writes==before,"unsafe commit leaves storage untouched");
        pressed[0]=0;macro_running=false;knob_pending=false;reconnect=false;lighting_suspended=false;light_test_active=false;diagnostic_active=false;
        profile_abort(1,41);
        // Same busy conditions block the live selector after upload ends.
        if(reason==0)pressed[0]|=1;
        if(reason==1)macro_running=true;
        if(reason==2)knob_pending=true;
        if(reason==3)reconnect=true;
        if(reason==4)lighting_suspended=true;
        if(reason==5){light_test_active=true;light_test_started=now;}if(reason==6){diagnostic_active=true;diagnostic_refreshed=now;}
        pressed[12]&=~0x80U;pressed[11]|=0x80U;
        ig_profile_task(now);ig_profile_task(now+30);check(ig_profile_active==2,"busy runtime keeps active slot stable");
        pressed[0]=0;macro_running=false;knob_pending=false;reconnect=false;lighting_suspended=false;light_test_active=false;diagnostic_active=false;
        now+=30;ig_profile_task(now);check(ig_profile_active==1,"idle applies latest stable requested slot");
    }
    // Returning from an invalid contact must not apply an earlier stale request.
    profile_select(2);pressed[12]=0;ig_profile_task(now);check(!ig_profile_selector.requested && ig_profile_active==2,"unknown selector retains active profile without fallback");
    // Failure during first save leaves fallback readable and latches writes.
    profile_select(3);profile_upload(3,config,IG_SIZE,51);profile_fail_at=profile_writes+1;
    check(profile_commit(3,51)==7 && !storage_ok && !stage_open && !borrowed,"uncertain first save ends upload and latches failure");
    profile_status(3);check((response[13]&128) && !(response[13]&1) && (response[13]&2),"torn first save reports degraded read-only fallback");
    check(!writes && !memcmp(old_legacy,backing,sizeof(backing)),"all commands preserve immutable legacy storage");
    profile_fail_at=0;boot(false);profile_select(2);
    check(active_byte(IG_RGB_OFFSET)==23 && ig_profile_views[1].generation==1,"saved bank profile survives reboot");
    // Existing legacy configuration remains the immutable generation0 fallback
    // for every never-saved slot, even after borrowing the other legacy half.
    memset(profile_flash,255,sizeof(profile_flash));boot(true);
    uint8_t *header=backing+IG_STORAGE_BASE;
    memcpy(header,"IGS1",4);header[4]=1;header[5]=16;put16(header+6,IG_SIZE);
    put32(header+8,77);put32(header+12,crc_buffer(config,IG_SIZE));put32(header+28,IG_VALID);
    memcpy(header+IG_SLOT_HEADER,config,IG_SIZE);boot(false);profile_select(3);
    memcpy(old_legacy,backing,sizeof(backing));
    for(uint8_t slot=0;slot<3;slot++)check(!ig_profile_views[slot].generation && ig_profile_views[slot].source==IG_PB_SOURCE_LEGACY &&
        !memcmp(ig_profile_views[slot].payload,config,IG_SIZE),"all initial slots share exactly the validated original legacy configuration");
    config[IG_RGB_OFFSET]=55;profile_upload(3,config,IG_SIZE,71);
    check(ig_profile_legacy_slot==0 && staging==cache+IG_STORAGE_PAYLOAD1,"staging borrows only the inactive legacy half");
    check(profile_commit(3,71)==0 && !writes && !memcmp(old_legacy,backing,sizeof(backing)),"explicit bank save never changes legacy fallback");
    check(ig_profile_refresh_pending && active_byte(IG_RGB_OFFSET)==23,"active saved slot waits for safe housekeeping reload");
    check(profile_begin(3,config,IG_SIZE,72)==3,"next upload cannot erase an old still-active record before housekeeping reload");
    housekeeping_task_user();check(!ig_profile_refresh_pending && active_byte(IG_RGB_OFFSET)==55,"safe housekeeping installs the new active-slot record");
    check(ig_profile_views[0].payload[IG_RGB_OFFSET]==23 && !ig_profile_views[0].generation,"other slots retain immutable legacy fallback");
    // Upload ownership expires in RAM, even with held keys or a pending switch.
    before=profile_writes;profile_select(3);
    now=UINT32_MAX-10000U;
    check(profile_begin(1,config,IG_SIZE,91)==0,"lease begins near timer wrap");
    uint32_t lease_start=now;
    uint8_t chunk[24]={0};put32(chunk,91);chunk[4]=1;chunk[5]=16;memcpy(chunk+8,config,16);
    now+=20000U;check(command(21,chunk,24)==0,"new chunk extends lease across timer wrap");
    check(ig_profile_upload_activity==now,"only accepted new bytes advance activity");
    lease_start=now;
    now+=29000U;check(command(21,chunk,24)==0,"identical duplicate is accepted before expiry");
    check(ig_profile_upload_activity==lease_start,"duplicates cannot keep an orphaned transfer alive");
    chunk[8]^=1;check(command(21,chunk,24)==2 && ig_profile_upload_activity==lease_start,"invalid chunks cannot extend lease");
    pressed[0]|=1;pressed[13]&=~0x80U;pressed[11]|=0x80U;
    now=lease_start+29999U;housekeeping_task_user();
    check(stage_open && borrowed && ig_profile_active==3,"live lease preserves staging and key guard");
    now++;housekeeping_task_user();
    check(!stage_open && !borrowed && !staging && !ig_profile_token && !ig_profile_target && !stage_received && !stage_size,
          "lease expiry invalidates owner and borrowed RAM at the boundary");
    check(profile_writes==before && !writes && !memcmp(old_legacy,backing,sizeof(backing)) && ig_profile_active==3,
          "timeout performs no flash write, no auto commit and no switch while keys are held");
    check(profile_commit(1,91)==4 && command(21,chunk,24)==4,"expired owner cannot write or commit");
    pressed[0]=0;now+=30;housekeeping_task_user();check(ig_profile_active==1,"after timeout the pending switch applies only when idle");
    check(profile_begin(1,config,IG_SIZE,92)==0,"new explicit owner can start after timeout");
    now+=IG_PROFILE_UPLOAD_LEASE_MS;
    check(profile_commit(1,92)==4 && !stage_open && !borrowed,"RAW detects expiry before housekeeping can run");
    check(profile_writes==before && !writes,"RAW-triggered timeout is read only");
    // Polling changes bind atomically to one active slot and snapshot. The old
    // empty RAW8 cannot reconnect whichever profile happens to be active later.
    profile_select(1);uint8_t bind[24]={1};
    ig_profile_put64(bind+4,ig_profile_views[0].generation);put32(bind+12,ig_profile_crcs[0]);
    active_interval=1;mock_saved_interval=2;
    for(uint8_t count=0;count<=24;count++)if(count!=16)
        check(command(8,bind,count)==1 && !reconnect,"bank RAW8 rejects every non-bound payload length");
    for(uint8_t i=1;i<=3;i++){bind[i]=1;check(command(8,bind,16)==1 && !reconnect,"bound reconnect reserved bytes must be zero");bind[i]=0;}
    bind[0]=0;check(command(8,bind,16)==2,"bound reconnect rejects unknown slot");
    bind[0]=4;check(command(8,bind,16)==2,"bound reconnect rejects out-of-range slot");
    bind[0]=2;check(command(8,bind,16)==8,"different active slot cannot be reconnected");bind[0]=1;
    bind[11]^=1;check(command(8,bind,16)==8,"full 64-bit generation binds reconnect");bind[11]^=1;
    bind[12]^=1;check(command(8,bind,16)==8,"stale payload CRC cannot reconnect");bind[12]^=1;
    ig_profile_refresh_pending=true;check(command(8,bind,16)==3 && !reconnect,"pending active pointer refresh blocks reconnect");ig_profile_refresh_pending=false;
    check(profile_begin(1,config,IG_SIZE,101)==0,"stage for reconnect guard");
    check(command(8,bind,16)==3 && !reconnect,"upload prevents bound reconnect");profile_abort(1,101);
    for(unsigned reason=0;reason<7;reason++){
        if(reason==0)pressed[0]|=1;
        if(reason==1)macro_running=true;
        if(reason==2)knob_pending=true;
        if(reason==3)reconnect=true;
        if(reason==4)lighting_suspended=true;
        if(reason==5){light_test_active=true;light_test_started=now;}
        if(reason==6){diagnostic_active=true;diagnostic_refreshed=now;}
        check(command(8,bind,16)==3,"every profile safety state blocks bound reconnect");
        pressed[0]=0;macro_running=false;knob_pending=false;reconnect=false;lighting_suspended=false;light_test_active=false;diagnostic_active=false;
    }
    pressed[11]=0;pressed[13]=0x80U;
    check(command(8,bind,16)==3 && !reconnect && !ig_profile_selector.requested,
          "fresh matrix read blocks a selector move before housekeeping notices it");
    pressed[13]=0;pressed[11]=0x80U;
    check(command(8,bind,16)==3 && !reconnect,"returning contact requires a fresh debounce interval");
    now+=29;check(command(8,bind,16)==3 && !reconnect,"bound reconnect waits for full debounce");
    now++;check(command(8,bind,16)==0 && response[6]==4 && response[8]==1 && response[9]==2 && response[10]==1 && !response[11] && reconnect,
               "bound stable active snapshot receives legacy-format ACK and schedules reconnect");
    check(profile_writes==before && !writes && ig_profile_active==1,"reconnect scheduling neither writes nor selects another profile");
    check_profile_usb_activation();
    printf("PASS profile runtime: %u checks; no hardware access; %u explicit mock page operations.\n",checks,profile_writes);
    return 0;
}
