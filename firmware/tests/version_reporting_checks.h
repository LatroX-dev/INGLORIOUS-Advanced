// Exercises the actual RAW13 handler. No device, OS or flash APIs.
static void version_reporting_checks(void) {
    static const uint8_t expected[16] = {1,0,IG_BOARD,0,0,0,11,0,0,0,0,0,1,0,11,0};
    uint8_t config[IG_SIZE], cache_before[sizeof(mock_cache)], backing_before[sizeof(mock_backing)];
    uint8_t scratch[IG_MAX_SIZE], staging_before[IG_MAX_SIZE], info_before[32], extensions_before[32];
    reset(); make_config(config,0x71); install(config);
    check(command(1,NULL,0)==0,"INFO baseline for version query"); memcpy(info_before,response,sizeof(response));
    check(command(11,NULL,0)==0,"extension baseline for version query"); memcpy(extensions_before,response,sizeof(response));
    memcpy(cache_before,mock_cache,sizeof(mock_cache)); memcpy(backing_before,mock_backing,sizeof(mock_backing));
    staging=scratch; memset(staging,0xA5,sizeof(scratch)); memcpy(staging_before,staging,sizeof(scratch));
    stage_received=17; stage_size=IG_SIZE; stage_generation=active_generation; stage_crc=0x87654321;
    uint32_t generation=active_generation, crc=active_crc, activity=lighting_last_activity;
    uint16_t size=active_size;
    unsigned initial_writes=writes, initial_reloads=reloads;
    brightness=37; registered[7]=1; mock_now=12345; pressed_rows[0]=1;
    for(uint8_t state=0;state<64;state++) {
        stage_open=(state&1)!=0; macro_running=(state&2)!=0; lighting_suspended=(state&4)!=0;
        mock_reconnect_pending=(state&8)!=0; storage_ok=(state&16)!=0; light_test_active=(state&32)!=0;
        check(command(13,NULL,0)==0 && response[6]==16 && !memcmp(response+8,expected,16),"RAW13 exact version payload for board, including stable flags, reserved and build ID");
        for(uint8_t offset=24;offset<32;offset++) check(response[offset]==0,"RAW13 response unused padding is zero");
        check(stage_open==((state&1)!=0) && macro_running==((state&2)!=0) && lighting_suspended==((state&4)!=0) && mock_reconnect_pending==((state&8)!=0) && storage_ok==((state&16)!=0) && light_test_active==((state&32)!=0),"version query neither blocks on nor mutates runtime busy/fault states");
        check(stage_received==17 && stage_size==IG_SIZE && stage_generation==generation && stage_crc==0x87654321 && !memcmp(staging_before,staging,sizeof(staging_before)),"version query preserves in-progress upload bytes and metadata");
        check(active_generation==generation && active_crc==crc && active_size==size && writes==initial_writes && reloads==initial_reloads && !memcmp(cache_before,mock_cache,sizeof(mock_cache)) && !memcmp(backing_before,mock_backing,sizeof(mock_backing)),"version query preserves persisted configuration, cache and storage counters");
        check(brightness==37 && lighting_last_activity==activity && mock_now==12345 && registered[7] && pressed_rows[0]==1 && !boot_jumps && !mock_reconnects && !volume_taps && !midi_messages,"version query creates no input, dimmer, wake, reset or peripheral action");
    }
    stage_open=macro_running=lighting_suspended=mock_reconnect_pending=light_test_active=false; storage_ok=true; stage_received=0;
    check(command(1,NULL,0)==0 && !memcmp(info_before,response,sizeof(response)),"existing INFO remains byte-exact after version queries");
    check(command(11,NULL,0)==0 && !memcmp(extensions_before,response,sizeof(response)),"existing extension record remains byte-exact after version queries");
    uint8_t extra[24]={0};
    for(uint8_t count=1;count<=24;count++) check(command(13,extra,count)==1 && response[6]==0,"RAW13 rejects every nonempty request, including zero bytes");
    uint8_t packet[32]={'I','G',1,13,71,0,0,0};
    for(uint8_t offset=8;offset<32;offset++) {
        packet[offset]=1; raw_hid_receive(packet,sizeof(packet));
        check(response[5]==1 && response[6]==0,"RAW13 rejects each nonzero request-padding byte"); packet[offset]=0;
    }
    packet[5]=1; raw_hid_receive(packet,sizeof(packet)); check(response[5]==1,"RAW13 rejects request status byte"); packet[5]=0;
    packet[7]=1; raw_hid_receive(packet,sizeof(packet)); check(response[5]==1,"RAW13 rejects reserved envelope byte"); packet[7]=0;
    packet[6]=25; raw_hid_receive(packet,sizeof(packet)); check(response[5]==1,"RAW13 rejects oversized payload count"); packet[6]=0;
    unsigned before=replies; raw_hid_receive(packet,31); check(replies==before,"RAW13 short report is ignored");
    packet[2]=2; raw_hid_receive(packet,sizeof(packet)); check(replies==before,"RAW13 unknown envelope version is ignored"); packet[2]=1;
    runtime_ready=false; check(command(13,NULL,0)==3,"RAW13 shares INFO initialization gate"); runtime_ready=true;
    check(command(14,NULL,0)==9,"new version command does not enable adjacent unknown commands");
    check(writes==initial_writes && reloads==initial_reloads && !memcmp(backing_before,mock_backing,sizeof(mock_backing)),"malformed version requests remain read-only");
    staging=NULL;
}
