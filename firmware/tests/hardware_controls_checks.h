// SPDX-License-Identifier: GPL-2.0-or-later
// Executes the common runtime, including key lookup, staging and housekeeping.
static void check_hardware_controls(void) {
    boot(true);
    check(command(11,NULL,0)==0,"discover hardware controls");
#if IG_HAS_OS_SWITCH
    check((u16(response+10)&128) && IG_BOARD>=10 && IG_BOARD<=16,"only verified GMMK3 targets advertise native OS control");
    check(IG_OS_SWITCH_PIN==A13 && switch_input==A13,"verified OS GPIO uses input pull-up");
    check(IG_OS_MAC_LEVEL==(IG_BOARD==12 ? 0 : 1),"75 ANSI retains its different pinned manufacturer polarity");
    static const ig_os_binding_t expected[]={{9,0,0xE3,0xE2},{9,3,0xE2,0xE3},{9,5,0xE6,0xE7},{8,4,0x65,0xE6}};
    unsigned binding_count=IG_BOARD>=14 ? 4 : 3;
    check(sizeof(ig_os_bindings)/sizeof(ig_os_bindings[0])==binding_count,"exact manufacturer modifier set for each physical size");
    for(unsigned i=0;i<binding_count;i++) {
        const ig_os_binding_t *actual=&ig_os_bindings[i], *ref=&expected[i];
        check(actual->row==ref->row && actual->col==ref->col && actual->windows==ref->windows && actual->mac==ref->mac,"generated physical mapping matches independent manufacturer audit");
    }
    unsigned reads=switch_reads;
    check(command(17,NULL,0)==0 && response[6]==8 && response[8]==1 && response[9]==IG_BOARD && response[10]==1 && !response[11] && !response[12] && !response[13] && !response[14] && !response[15],"Windows status has exact format, board, capability and reserved bytes");
    uint8_t extra[24]={0};
    for(uint8_t count=1;count<=24;count++) check(command(17,extra,count)==1,"OS status rejects every nonempty payload");
    check(switch_reads==reads && !writes,"read-only status does not sample GPIO or write storage");

    uint8_t config[IG_SIZE];
    for(uint16_t at=0;at<IG_SIZE;at++) config[at]=default_byte(at);
    for(uint8_t layer=0;layer<4;layer++) for(unsigned i=0;i<binding_count;i++)
        put16(config+IG_KEYS_OFFSET+2*((layer*MATRIX_ROWS+expected[i].row)*MATRIX_COLS+expected[i].col),expected[i].windows);
    // A real macro deliberately contains GUI. It must keep its explicit codes.
    put16(config+14,3); put16(config+IG_MACRO_OFFSET+2,3);
    config[IG_EVENT_OFFSET]=1; put16(config+IG_EVENT_OFFSET+2,0xE3);
    config[IG_EVENT_OFFSET+4]=3; put16(config+IG_EVENT_OFFSET+6,100);
    config[IG_EVENT_OFFSET+8]=2; put16(config+IG_EVENT_OFFSET+10,0xE3);
    upload(config,IG_SIZE); check(commit(config,IG_SIZE)==0,"install all-layer native modifier fixture");
    unsigned initial_writes=writes; uint32_t generation=active_generation,crc=active_crc;
    uint8_t saved[sizeof(backing)]; memcpy(saved,backing,sizeof(saved));
    reads=switch_reads;
    for(uint8_t state=0;state<64;state++) {
        stage_open=(state&1)!=0; macro_running=(state&2)!=0; lighting_suspended=(state&4)!=0;
        reconnect=(state&8)!=0; knob_pending=(state&16)!=0; storage_ok=(state&32)!=0;
        check(command(17,NULL,0)==0 && response[6]==8 && response[10]==1,"read-only OS status remains available during busy and fault states");
        check(stage_open==((state&1)!=0) && macro_running==((state&2)!=0) && lighting_suspended==((state&4)!=0) && reconnect==((state&8)!=0) && knob_pending==((state&16)!=0) && storage_ok==((state&32)!=0),"OS status preserves every busy and fault state");
    }
    stage_open=macro_running=lighting_suspended=reconnect=knob_pending=false; storage_ok=true;
    check(switch_reads==reads,"status queries never drive direct GPIO sampling");
    layer_state=4; default_layer_state=1;
    for(uint8_t layer=0;layer<4;layer++) for(unsigned i=0;i<binding_count;i++)
        check(keycode_at_keymap_location(layer,expected[i].row,expected[i].col)==expected[i].windows,"Windows lookup on all four layers");

    // A switch change while a modifier is held must release the old modifier.
    pressed[9]=(matrix_row_t)1<<0;
    uint16_t pressed_code=keycode_at_keymap_location(0,9,0); register_code(pressed_code);
    switch_level=IG_OS_MAC_LEVEL; housekeeping_task_user();
    now+=29; housekeeping_task_user();
    check(command(17,NULL,0)==0 && response[11]==1 && !response[12] && response[13]==1,"changed raw input is pending during debounce");
    now++; housekeeping_task_user();
    check(!ig_os_effective_mac && registered[0xE3],"held modifier delays switching after debounce");
    // QMK releases events before calling housekeeping.
    pressed[9]=0;
    uint16_t released_code=keycode_at_keymap_location(0,9,0); unregister_code(released_code);
    check(released_code==pressed_code && !registered[0xE3] && !registered[0xE2],"release resolves original modifier without a stuck report");
    housekeeping_task_user();
    check(ig_os_effective_mac && command(17,NULL,0)==0 && response[12]==1 && !response[13],"idle applies stable Mac switch");
    for(uint8_t layer=0;layer<4;layer++) for(unsigned i=0;i<binding_count;i++)
        check(keycode_at_keymap_location(layer,expected[i].row,expected[i].col)==expected[i].mac,"Mac conversion covers exactly native positions on all four layers");
    check(layer_state==4 && default_layer_state==1,"OS switch never changes user layers");
    init_runtime(); check(ig_os_effective_mac,"configuration reload retains hardware mode");
    static const uint16_t custom[]={0,1,4,0xE0,0xE1,0xE7,0x0104,0x081D,0x5221,0x5262,0x7700,IG_ZOOM_IN,IG_UNDO_MAC};
    for(unsigned i=0;i<binding_count;i++) for(unsigned k=0;k<sizeof(custom)/sizeof(custom[0]);k++)
        check(ig_hardware_keycode(expected[i].row,expected[i].col,custom[k])==custom[k],"custom keys, chords, transparent/layer keys and macros are preserved");
    for(uint8_t row=0;row<MATRIX_ROWS;row++) for(uint8_t col=0;col<MATRIX_COLS;col++)
        for(unsigned i=0;i<binding_count;i++) {
            bool native=false;
            for(unsigned j=0;j<binding_count;j++) if(expected[j].row==row && expected[j].col==col && expected[j].windows==expected[i].windows) native=true;
            if(!native) check(ig_hardware_keycode(row,col,expected[i].windows)==expected[i].windows,"same modifier assigned elsewhere stays unchanged");
        }
    start_macro(0,false); macro_task();
    check(macro_running && registered[0xE3] && !registered[0xE2],"explicit macro GUI remains GUI in Mac mode");
    switch_level=!IG_OS_MAC_LEVEL; housekeeping_task_user(); now+=30; housekeeping_task_user();
    check(ig_os_effective_mac,"running macro delays effective switch");
    now+=100; housekeeping_task_user(); housekeeping_task_user(); housekeeping_task_user();
    check(!macro_running && !registered[0xE3] && !ig_os_effective_mac,"macro releases safely before pending OS mode applies");

    // Contact bounce and 32-bit clock wrap use elapsed time, not absolute order.
    now=UINT32_MAX-10U; switch_level=IG_OS_MAC_LEVEL; housekeeping_task_user();
    now+=20; switch_level=!IG_OS_MAC_LEVEL; housekeeping_task_user();
    now+=20; switch_level=IG_OS_MAC_LEVEL; housekeeping_task_user();
    now+=29; housekeeping_task_user(); check(!ig_os_effective_mac,"bouncing switch restarts debounce across timer wrap");
    now++; housekeeping_task_user(); check(ig_os_effective_mac,"stable contact applies across timer wrap");

    for(unsigned busy=0;busy<5;busy++) {
        switch_level=IG_OS_MAC_LEVEL; ig_hardware_init();
        if(busy==0) check(begin(config,IG_SIZE)==0,"stage while testing OS guard");
        if(busy==1) { knob_pending=true; knob_cancelled=true; knob_started=now; }
        if(busy==2) { reconnect=true; mock_saved_interval=active_interval; }
        if(busy==3) lighting_suspended=true;
        if(busy==4) { light_test_active=true; light_test_started=now; }
        // Re-arm the mock's instantaneous reconnect before the second sample.
        switch_level=!IG_OS_MAC_LEVEL;
        housekeeping_task_user(); now+=30;
        if(busy==2) reconnect=true;
        housekeeping_task_user();
        check(ig_os_effective_mac,"staging, knob press, USB transition, suspend and lighting test defer switching");
        if(busy==0) check(command(6,NULL,0)==0,"abort test staging");
        stage_open=false; knob_pending=false; reconnect=false; lighting_suspended=false; light_test_active=false;
        housekeeping_task_user(); check(!ig_os_effective_mac,"pending OS mode applies when operation ends");
    }
#if IG_BOARD == 16
    uint8_t action=1; check(command(14,&action,1)==0,"start device diagnostics for OS switch feedback");
    switch_level=IG_OS_MAC_LEVEL; housekeeping_task_user(); now+=30; housekeeping_task_user();
    check(ig_os_effective_mac && diagnostic_active,"device diagnostic session allows native OS switch testing");
    action=2; check(command(14,&action,1)==0,"finish OS diagnostic session");
#endif
    check(writes==initial_writes && active_generation==generation && active_crc==crc && !memcmp(saved,backing,sizeof(saved)),"all switch actions preserve stored configuration, generation and EEPROM exactly");
    switch_level=IG_OS_MAC_LEVEL; keyboard_pre_init_user(); keyboard_post_init_user();
    check(ig_os_effective_mac && keycode_at_keymap_location(0,9,0)==0xE2,"boot in Mac position is coherent before first key");
    boot(false);
#else
    check(!(u16(response+10)&128) && command(17,NULL,0)==9,"unsupported boards advertise no hardware control and reject RAW17");
    check(switch_input==-1 && !switch_reads && !writes,"older boards never configure or read the OS switch GPIO");
#endif
}
