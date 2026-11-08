// Executes the actual runtime handlers. Bootloader and USB are mocked; no flash
// writer, operating-system device API or physical keyboard is used.
#if IG_APP_BOOTLOADER_ENABLE
static void app_bootloader_request(uint8_t *request, uint8_t nonce) {
    memcpy(request, ig_fw_identity, 16);
    for (uint8_t i = 16; i < 24; ++i) request[i] = nonce + i;
}
static void app_bootloader_test_busy(uint8_t reason, bool busy) {
    switch (reason) {
        case 0: stage_open = busy; break;
        case 1: macro_running = busy; break;
        case 2: pressed_rows[0] = busy ? 1 : 0; break;
        case 3: knob_pending = busy; break;
        case 4: lighting_suspended = busy; break;
        case 5: light_test_active = busy; light_test_started = mock_now; break;
        case 6: mock_reconnect_pending = busy; break;
        case 7: mock_usb_active = !busy; break;
        case 8: macro_held[0] = busy ? 1 : 0; break;
        case 9: held_layer_keys[0] = busy ? 1 : 0; break;
        case 10: storage_ok = !busy; break;
    }
}
#endif
static void app_bootloader_checks(void) {
    uint8_t request[24] = {0};
    reset();
    check(command(11,NULL,0)==0 && ((u16(response+10)&512)!=0)==(IG_APP_BOOTLOADER_ENABLE!=0),
          "app bootloader capability is absent unless explicitly built");
#if !IG_APP_BOOTLOADER_ENABLE
    for (uint8_t cmd = 25; cmd <= 27; ++cmd)
        check(command(cmd,request,24)==9 && !boot_jumps && !writes,
              "ordinary firmware exposes no app bootloader command");
#else
    uint8_t altered[24], before_cache[sizeof(mock_cache)], before_backing[sizeof(mock_backing)];
    app_bootloader_request(request,1);
    check(command(26,request,24)==8 && command(27,request,24)==8 && !boot_jumps,
          "commit or cancel without preparation is stale and cannot reset");
    for (uint8_t cmd=25; cmd<=27; ++cmd) {
        for (uint8_t length=0; length<24; ++length)
            check(command(cmd,request,length)==1 && !app_bootloader_state,"all short payloads are rejected");
        memcpy(altered,request,24); memset(altered+16,0,8);
        check(command(cmd,altered,24)==1 && !app_bootloader_state,"all-zero nonces are invalid");
        for (uint8_t i=0; i<16; ++i) {
            memcpy(altered,request,24); altered[i]^=1;
            check(command(cmd,altered,24)==8 && !app_bootloader_state,"every identity byte must match the installed firmware");
        }
    }
    for (uint8_t cmd=25; cmd<=27; ++cmd) {
        uint8_t frame[32]={'I','G',1,cmd,9,0,24,0}; memcpy(frame+8,request,24);
        frame[5]=1; raw_hid_receive(frame,32); check(response[5]==1 && !app_bootloader_state,"request status byte is rejected"); frame[5]=0;
        frame[7]=1; raw_hid_receive(frame,32); check(response[5]==1 && !app_bootloader_state,"reserved envelope byte is rejected"); frame[7]=0;
        frame[6]=25; raw_hid_receive(frame,32); check(response[5]==1 && !app_bootloader_state,"oversized count is rejected"); frame[6]=24;
        unsigned prior=replies; raw_hid_receive(frame,31); check(replies==prior && !app_bootloader_state,"short reports are ignored");
        frame[2]=2; raw_hid_receive(frame,32); check(replies==prior && !app_bootloader_state,"unknown envelope protocol is ignored");
    }
    memcpy(before_cache,mock_cache,sizeof(mock_cache)); memcpy(before_backing,mock_backing,sizeof(mock_backing));
    unsigned old_writes=writes, old_reloads=reloads;
    check(command(25,request,24)==0 && response[6]==24 && !memcmp(response+8,request,24) && app_bootloader_state==1 && !boot_jumps,
          "prepare echoes full identity and nonce without a reset");
    for (uint8_t i=16; i<24; ++i) {
        memcpy(altered,request,24); altered[i]^=1;
        check(command(26,altered,24)==8 && app_bootloader_state==1 && !boot_jumps,"every nonce byte binds commit to preparation");
        check(command(27,altered,24)==8 && app_bootloader_state==1,"a mismatched cancellation does not cancel the active request");
    }
    static const uint8_t blocked[]={3,4,5,6,8,10,12,14,15,20,21,22,23};
    for(uint8_t i=0;i<sizeof(blocked);++i)
        check(command(blocked[i],NULL,0)==3 && app_bootloader_state==1,"configuration and other actions are blocked during the transition");
    static const uint8_t reads[]={1,9,11,13,16};
    for(uint8_t i=0;i<sizeof(reads);++i) check(command(reads[i],NULL,0)==0 && app_bootloader_state==1,"read-only status stays available");
    check(command(26,request,24)==0 && response[6]==24 && !memcmp(response+8,request,24) && app_bootloader_state==2 && !boot_jumps,
          "commit queues the full bound ACK before scheduling a jump");
    check(command(26,request,24)==8 && app_bootloader_state==2,"duplicate commits never reschedule");
    check(command(27,request,24)==3,"already acknowledged commits cannot be retried through cancellation");
    mock_now+=149; housekeeping_task_user(); check(!boot_jumps,"bootloader jump waits for the ACK delay");
    ++mock_now; housekeeping_task_user(); check(boot_jumps==1 && !app_bootloader_state,"bootloader jump executes once after150ms");
    mock_now+=1000; housekeeping_task_user(); check(boot_jumps==1,"a returning bootloader stub is never retried");
    check(command(26,request,24)==8 && command(25,request,24)==8,"retired request cannot be replayed in the same boot");
    check(writes==old_writes && reloads==old_reloads && !memcmp(before_cache,mock_cache,sizeof(mock_cache)) &&
          !memcmp(before_backing,mock_backing,sizeof(mock_backing)),"entry protocol never writes or reloads configuration storage");

    reset(); app_bootloader_request(request,2);
    check(command(25,request,24)==0,"cancel scenario prepared");
    check(command(27,request,24)==0 && response[6]==24 && !memcmp(response+8,request,24) && !app_bootloader_state,"matching cancellation is echoed");
    mock_now+=6000; housekeeping_task_user(); check(!boot_jumps,"cancelled preparation never jumps");
    check(command(26,request,24)==8 && command(25,request,24)==8,"cancelled nonce remains retired");
    app_bootloader_request(request,3); check(command(25,request,24)==0,"a fresh nonce can start another explicit attempt");
    uint32_t prepared=mock_now;
    mock_now+=4000; check(command(25,request,24)==0 && app_bootloader_prepared_at==prepared,"prepare retry does not renew the deadline");
    mock_now=prepared+5000; check(command(26,request,24)==8 && !app_bootloader_state,"prepare lease expires exactly at5seconds");
    uint8_t dim=19; check(command(10,&dim,1)==0,"ordinary configuration actions resume after expiry");

    // Refuse every busy condition at preparation, between phases and after ACK.
    for(uint8_t reason=0;reason<=10;++reason) for(uint8_t phase=0;phase<3;++phase) {
        reset(); memset(macro_held,0,sizeof(macro_held)); app_bootloader_request(request,4);
        if(phase) check(command(25,request,24)==0,"busy-race scenario prepared");
        if(phase==2) check(command(26,request,24)==0,"busy-race scenario committed");
        app_bootloader_test_busy(reason,true);
        if(phase<2) check(command(phase==0?25:26,request,24)==(reason==10?7:3),"busy or unhealthy device refuses bootloader entry");
        else { mock_now+=150; housekeeping_task_user(); check(!boot_jumps && !app_bootloader_state,"new activity after ACK cancels instead of delaying a hidden reset"); }
        app_bootloader_test_busy(reason,false); mock_now+=6000; housekeeping_task_user();
        check(!boot_jumps && !app_bootloader_state,"clearing a busy condition never resurrects a cancelled transition");
    }
    for(uint8_t reason=0;reason<3;++reason) for(uint8_t phase=0;phase<2;++phase) {
        reset(); app_bootloader_request(request,5); check(command(25,request,24)==0,"snapshot scenario prepared");
        if(phase) check(command(26,request,24)==0,"snapshot scenario committed");
        if(reason==0)++active_generation; else if(reason==1)active_crc^=1; else ++active_size;
        if(!phase)check(command(26,request,24)==8,"configuration change invalidates a prepared token");
        mock_now+=150; housekeeping_task_user(); check(!boot_jumps && !app_bootloader_state,"changed configuration cannot enter bootloader");
    }
    for(uint8_t type=1;type<=3;++type) {
        reset(); app_bootloader_request(request,6); check(command(25,request,24)==0 && command(26,request,24)==0,"input-race scenario committed");
        keyrecord_t event={.event={.pressed=true,.type=type,.key={0,0}}};
        process_record_user(4,&event); event.event.pressed=false; process_record_user(4,&event);
        mock_now+=150; housekeeping_task_user(); check(!boot_jumps && !app_bootloader_state,"brief physical/encoder events cancel even if released before housekeeping");
    }
    reset(); app_bootloader_request(request,7); check(command(25,request,24)==0 && command(26,request,24)==0,"suspend scenario committed");
    suspend_power_down_user(); lighting_suspended=false; mock_now+=150; housekeeping_task_user();
    check(!boot_jumps && !app_bootloader_state,"suspend cancels even when resumed before the next task");

#ifdef IG_HAS_SLIDER
    reset(); prime_slider(0); app_bootloader_request(request,11);
    check(command(25,request,24)==0 && command(26,request,24)==0,"slider scenario committed");
    sample_slider(1023,4); mock_now+=150; housekeeping_task_user();
    check(!boot_jumps && !app_bootloader_state,"deliberate slider movement cancels the entry request");
#endif

    reset(); app_bootloader_request(request,12); check(command(25,request,24)==0,"runtime-reload scenario prepared");
    init_runtime(); check(command(26,request,24)==8,"runtime reload cancels preparation even when generation and CRC are unchanged");
    mock_now+=6000; housekeeping_task_user(); check(!boot_jumps,"reloaded runtime cannot execute an old prepared jump");

    reset(); app_bootloader_request(request,8); mock_now=UINT32_MAX-70U;
    check(command(25,request,24)==0 && command(26,request,24)==0,"timer-wrap scenario committed");
    mock_now+=149; housekeeping_task_user(); check(!boot_jumps,"wrapped timer retains149ms delay");
    ++mock_now; housekeeping_task_user(); check(boot_jumps==1,"wrapped timer jumps at150ms");
    reset(); app_bootloader_request(request,9); check(command(25,request,24)==0,"near-expiry scenario prepared");
    mock_now+=4999; check(command(26,request,24)==0,"commit within prepare lease is accepted");
    mock_now+=150; housekeeping_task_user(); check(boot_jumps==1,"acknowledged commit gets its full bounded delay");
    reset(); app_bootloader_request(request,10); check(command(25,request,24)==0 && command(26,request,24)==0,"stalled-task scenario committed");
    mock_now+=1000; housekeeping_task_user(); check(!boot_jumps && !app_bootloader_state,"stalled task discards commit instead of resetting much later");
    reset(); memset(macro_held,0,sizeof(macro_held));
#endif
}
