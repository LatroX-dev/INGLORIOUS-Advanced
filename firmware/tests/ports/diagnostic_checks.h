// SPDX-License-Identifier: GPL-2.0-or-later
// Runs against the actual common runtime and pinned physical geometry.
static void check_diagnostics(void) {
    boot(false);
    check(command(11,NULL,0)==0 && response[8]==1,"extension format");
#if IG_BOARD == 16
    check(u16(response+10)==7,"diagnostic capability advertised");
    uint8_t before[sizeof(backing)], action=0, overlay[5]={0};
    memcpy(before,backing,sizeof(before));
    uint32_t generation=active_generation, crc=active_crc;
    check(command(14,&action,1)==0 && response[6]==24 && response[8]==1 && !response[9] && response[10]==14 && response[11]==8,"inactive physical diagnostic status");
    check(command(15,overlay,5)==4,"overlay cannot start a diagnostic session");
    check(command(14,NULL,0)==1,"diagnostic request length");
    action=3; check(command(14,&action,1)==8 && !diagnostic_active,"refresh-only cannot start an inactive session");
    action=4; check(command(14,&action,1)==2,"diagnostic action range");
    action=1;
    for(unsigned busy=0;busy<6;busy++) {
        stage_open=busy==0; macro_running=busy==1; reconnect=busy==2;
        light_test_active=busy==3; lighting_suspended=busy==4;
        if(light_test_active) light_test_started=now;
        if(busy==5) pressed[reference_recovery[0][0]]=(matrix_row_t)1<<reference_recovery[0][1];
        check(command(14,&action,1)==3 && !diagnostic_active,"busy runtime rejects session start");
        stage_open=macro_running=reconnect=light_test_active=lighting_suspended=false;
        memset(pressed,0,sizeof(pressed));
    }
    check(command(14,&action,1)==0 && response[9] && u16(response+30)==10000,"session starts with bounded watchdog");
    uint8_t expected[14]={0};
    // Scan capture records the physical Fn position even when no host report exists.
    for(uint8_t row=0;row<MATRIX_ROWS;row++) {
        pressed[row]=255; matrix_scan_user(); pressed[row]=0;
        for(uint8_t col=0;col<MATRIX_COLS;col++) if(reference_physical[row*MATRIX_COLS+col]) expected[row]|=(uint8_t)1<<col;
    }
    action=0; check(command(14,&action,1)==0 && !memcmp(response+12,expected,14),"latched scan bits include all physical keys and exclude matrix holes");
    action=2; check(command(14,&action,1)==0 && !response[9],"stop ends session");
    action=1; check(command(14,&action,1)==0,"new session clears previous observations");
    memset(expected,0,sizeof(expected));
    // A quick press and release entirely between polls must survive; mapping is irrelevant.
    for(uint8_t row=0;row<MATRIX_ROWS;row++) for(uint8_t col=0;col<MATRIX_COLS;col++) {
        keyrecord_t key={.event={.pressed=true,.type=1,.key={row,col}}};
        check(pre_process_record_user(KC_NO,&key),"diagnostic keeps normal key processing enabled");
        key.event.pressed=false; check(pre_process_record_user(0x7700,&key),"release processing remains enabled");
        if(reference_physical[row*MATRIX_COLS+col]) expected[row]|=(uint8_t)1<<col;
    }
    keyrecord_t encoder={.event={.pressed=true,.type=ENCODER_CW_EVENT,.key={255,255}}};
    check(pre_process_record_user(KC_NO,&encoder),"unmapped clockwise encoder observed");
    encoder.event.pressed=false; pre_process_record_user(KC_NO,&encoder);
    encoder.event.pressed=true; encoder.event.type=ENCODER_CCW_EVENT;
    pre_process_record_user(0x7700,&encoder); pre_process_record_user(0x5221,&encoder);
    action=3; now+=4000; check(command(14,&action,1)==0 && !memcmp(response+12,expected,14) && u16(response+26)==1 && u16(response+28)==2 && u16(response+30)==10000,"refresh-only preserves quick taps and both encoder directions");
    diagnostic_cw=UINT16_MAX; encoder.event.type=ENCODER_CW_EVENT; pre_process_record_user(0,&encoder);
    action=0; check(command(14,&action,1)==0 && u16(response+26)==0,"encoder counter wrap is uint16");
    uint8_t begin_payload[10]={0},commit_payload[8]={0},brightness[1]={120};
    check(command(4,begin_payload,10)==3 && command(5,commit_payload,8)==3 && command(8,NULL,0)==3 && command(10,brightness,1)==3,"active test excludes staging, persistence, restart and brightness mutation");
    action=1; check(command(12,&action,1)==3,"old lighting test cannot overlap diagnostics");
    overlay[0]=1; overlay[2]=90; overlay[3]=140; overlay[4]=210;
    check(command(15,overlay,5)==0 && response[6]==8 && !memcmp(response+8,overlay,5) && !response[13],"all-LED overlay response");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) check(pixels[led].r==ig_srgb_to_pwm(90) && pixels[led].g==ig_srgb_to_pwm(140) && pixels[led].b==ig_srgb_to_pwm(210),"all LEDs use temporary color with one output conversion");
    overlay[0]=2;
    for(uint8_t selected=0;selected<RGB_MATRIX_LED_COUNT;selected++) {
        overlay[1]=selected; check(command(15,overlay,5)==0,"every physical and side LED can be selected");
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) check(pixels[led].r==(led==selected?ig_srgb_to_pwm(90):0) && pixels[led].g==(led==selected?ig_srgb_to_pwm(140):0) && pixels[led].b==(led==selected?ig_srgb_to_pwm(210):0),"single LED selection blacks other LEDs temporarily");
    }
    overlay[1]=RGB_MATRIX_LED_COUNT; check(command(15,overlay,5)==2,"out-of-range LED rejected");
    overlay[0]=1; overlay[1]=1; check(command(15,overlay,5)==2,"all-LED reserved index rejected");
    overlay[0]=0; overlay[1]=0; check(command(15,overlay,5)==2,"clear reserved color rejected");
    memset(overlay,0,sizeof(overlay)); check(command(15,overlay,5)==0 && diagnostic_active && !diagnostic_mode,"clear restores profile without ending session");
    now+=9999; overlay[0]=1; overlay[2]=255; check(command(15,overlay,5)==0 && u16(response+14)==1,"overlay never refreshes watchdog");
    action=0; check(command(14,&action,1)==0 && response[9] && u16(response+30)==1,"last live status before watchdog expiry");
    now++; action=3; check(command(14,&action,1)==8 && !diagnostic_active && !diagnostic_mode,"refresh arriving after last live status cannot restart an expired session");
    housekeeping_task_user(); check(!diagnostic_active && !diagnostic_mode,"watchdog restores normal output without another host request");
    now=UINT32_MAX-20U; action=1; check(command(14,&action,1)==0,"watchdog starts near clock wrap");
    check(command(15,overlay,5)==0,"overlay active before wrapped expiry");
    now+=10000; housekeeping_task_user(); check(!diagnostic_active,"watchdog expires across clock wrap");
    action=1; check(command(14,&action,1)==0 && command(15,overlay,5)==0,"restart session before suspend");
    suspend_power_down_user(); check(!diagnostic_active && !diagnostic_mode,"USB suspend clears diagnostic session");
    suspend_wakeup_init_user(); check(!diagnostic_active,"USB resume does not restart diagnostics");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); ig_rgb_t restored[RGB_MATRIX_LED_COUNT]; memcpy(restored,pixels,sizeof(restored));
    action=1; check(command(14,&action,1)==0 && command(15,overlay,5)==0,"restart session before explicit stop");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    action=2; check(command(14,&action,1)==0 && !diagnostic_active && !diagnostic_mode,"explicit stop clears overlay");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(!memcmp(restored,pixels,sizeof(restored)),"stopping restores normal profile rendering at the same effect time");
    check(!writes && active_generation==generation && active_crc==crc && !memcmp(before,backing,sizeof(before)),"all diagnostics preserve complete stored profile and generation without writes");
#else
    check(u16(response+10)==3,"older boards do not advertise diagnostics");
    uint8_t action=1, overlay[5]={1,0,255,0,0};
    check(command(14,&action,1)==9 && command(15,overlay,5)==9 && !writes,"unsupported diagnostics cannot mutate older boards");
#endif
}
