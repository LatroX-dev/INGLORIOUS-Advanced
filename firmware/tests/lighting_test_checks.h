// Exercises the actual protocol/rendering module; no device or OS calls.
static void lighting_test_checks(void) {
    static const uint8_t expected_positions[10][2] = {
#if IG_BOARD == 1
        {6,0},{5,5},{4,3},{7,2},{7,0},{4,0},{6,0},{7,0},{5,0},{2,2}
#else
        {3,0},{3,1},{3,2},{2,0},{2,1},{2,2},{1,0},{1,1},{1,2},{4,0}
#endif
    };
    // Independently mapped from reference/qmk layouts and RGB matrix entries.
    static const uint8_t expected_leds[10] = {
#if IG_BOARD == 1
        46,38,31,53,52,25,46,52,41,15
#else
        11,12,13,8,9,10,4,5,6,15
#endif
    };
    // Actual reference/qmk physical layout coordinates, identical to the pinned
    // QMK board JSON. Deliberately unrelated to the MCU matrix row/column order.
    static const point_t physical_points[] = {
#if IG_BOARD == 1
        {0,0},{0,15},{4,26},{5,38},{2,49},{2,61},{18,0},{14,15},{22,26},{25,38},{33,49},{20,61},{33,0},{29,15},{36,26},{40,38},{47,49},{38,61},{47,0},{43,15},{51,26},{54,38},{61,49},{61,0},{58,15},{65,26},{69,38},{76,49},{79,0},{72,15},{79,26},{83,38},{90,49},{92,61},{94,0},{87,15},{94,26},{98,38},{105,49},{108,0},{101,15},{108,26},{112,38},{119,49},{123,0},{116,15},{123,26},{126,38},{134,49},{145,61},{141,0},{130,15},{137,26},{141,38},{148,49},{159,61},{155,0},{145,15},{152,26},{155,38},{163,49},{170,0},{159,15},{166,26},{170,38},{173,61},{184,0},{18,49},{0,8},{224,8},{202,0},{0,15},{224,15},{224,15},{0,21},{224,21},{224,26},{0,28},{224,28},{173,15},{220,64},{0,35},{224,35},{224,49},{0,42},{224,42},{195,15},{224,38},{0,48},{224,48},{181,26},{182,49},{0,55},{224,55},{206,52},{184,38},{191,64},{201,26},{206,64}
#else
        {45,0},{90,0},{134,0},{179,0},{45,16},{90,16},{134,16},{179,24},{45,32},{90,32},{134,32},{45,48},{90,48},{134,48},{179,56},{67,64},{134,64},{0,0},{0,11},{0,21},{0,32},{0,43},{0,53},{0,64},{224,0},{224,11},{224,21},{224,32},{224,43},{224,53},{224,64}
#endif
    };
    _Static_assert(sizeof(physical_points)/sizeof(physical_points[0])==RGB_MATRIX_LED_COUNT,"Every real physical LED is present in the test fixture");
    uint8_t config[IG_MAX_SIZE], backing_before[sizeof(mock_backing)];
    uint8_t action = 0, set_brightness = 11;
    reset();
    check(command(11,NULL,0)==0 && response[6]==8 && u16(response+8)==1 && u16(response+10)==3 && u16(response+12)==21000 && u16(response+14)==0,"extension discovery reports version, both capabilities, bounded duration, reserved zeros");
    check(command(11,&action,1)==1,"extension discovery rejects nonempty request");
    check(command(12,NULL,0)==1,"light test requires exactly one action byte");
    uint8_t excess[2]={1,0}; check(command(12,excess,2)==1,"light test rejects extra payload");
    action=3; check(command(12,&action,1)==2 && !light_test_active,"unknown test action cannot start overlay");
    uint8_t packet[32]={'I','G',1,12,1,0,1,0,1}; packet[31]=1; raw_hid_receive(packet,32);
    check(response[5]==1 && !light_test_active,"nonzero padding cannot start overlay");
    action=0; check(command(12,&action,1)==0 && response[6]==4 && !response[8] && !response[9] && !u16(response+10),"inactive status is canonical zero");
    action=2; check(command(12,&action,1)==0 && !light_test_active,"stop is idempotent before starting");
    make_v2(config); put16(config+4,4); put16(config+12,IG_SEQUENCE_EVENTS); config[26]=1; put16(config+27,10); config[29]=0; config[30]=10;
    memset(config+IG_RGB_OFFSET,0,IG_LAYERS*RGB_MATRIX_LED_COUNT*3); // Stored black, asleep, and runtime dimmer zero must still permit a visible test.
    install_size(config,IG_MAX_SIZE); brightness=0; mock_now=lighting_last_activity+10000;
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(lighting_idle && rendered[0].r==0,"black sleeping fixture really starts dark");
    for(uint8_t i=0;i<10;i++) g_led_config.matrix_co[expected_positions[i][0]][expected_positions[i][1]]=expected_leds[i];
    memcpy(g_led_config.point,physical_points,sizeof(physical_points));
    unsigned initial_writes=writes, initial_reloads=reloads, initial_taps=volume_taps, initial_midi=midi_messages, initial_jumps=boot_jumps;
    uint32_t initial_generation=active_generation, initial_crc=active_crc, initial_activity=lighting_last_activity;
    memcpy(backing_before,mock_backing,sizeof(backing_before));
    action=1; check(command(12,&action,1)==0 && response[8]==1 && response[9]==IG_BOARD && u16(response+10)==21000,"explicit start returns board pattern and exact maximum remaining time");
    uint32_t start=mock_now;
    check(command(12,&action,1)==3 && light_test_started==start,"duplicate start never extends the autonomous deadline");
    check(begin_size(config,IG_MAX_SIZE,active_generation)==3 && !stage_open,"overlay blocks beginning a configuration upload");
    check(commit_size(config,IG_MAX_SIZE,active_generation)==3,"overlay blocks commit without accessing persistent state");
    check(command(8,NULL,0)==3 && !mock_reconnect_pending,"overlay blocks USB restart");
    check(command(10,&set_brightness,1)==3 && brightness==0,"overlay blocks incoming dimmer writes");
    check(command(9,NULL,0)==0 && response[8]==0,"brightness read exposes unchanged original scale during overlay");
    for(uint16_t elapsed=0;elapsed<21000;elapsed++) {
        mock_now=start+elapsed;
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        ig_rgb_t whole_frame[RGB_MATRIX_LED_COUNT]; memcpy(whole_frame,rendered,sizeof(whole_frame));
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT/2);
        mock_now+=19; // Even across a phase/timeout boundary, chunks share the frame timestamp.
        rgb_matrix_indicators_advanced_user(RGB_MATRIX_LED_COUNT/2,RGB_MATRIX_LED_COUNT);
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
            check(!memcmp(&rendered[led],&whole_frame[led],sizeof(ig_rgb_t)),"partial batches equal a full frame at every millisecond and every phase boundary");
            if(elapsed<2000) check(rendered[led].r==reference_pwm(160) && rendered[led].g==reference_pwm(160) && rendered[led].b==reference_pwm(160),"first two seconds test every LED white");
            else if(elapsed<2500) check(!rendered[led].r && !rendered[led].g && !rendered[led].b,"half-second gap is entirely dark");
            else if(elapsed<12500) {
                uint16_t step=elapsed-2500;
                bool lit=step%1000<700 && led==expected_leds[step/1000];
                check(rendered[led].r==(lit?reference_pwm(160):0) && !rendered[led].g && !rendered[led].b,"each physical character is red for700ms then dark for300ms");
            } else if(elapsed<18800) check(rendered[led].r<=reference_pwm(224) && !rendered[led].g && !rendered[led].b,"drops and accumulating pool are exclusively bounded red");
            else check(rendered[led].r==reference_pwm(160) && !rendered[led].g && !rendered[led].b,"pool fills the whole board before final two-second red hold");
        }
    }
    // Physical downward travel, dark interval before the first pool, and a
    // monotonically rising pool (including the side strips) are independent of
    // the matrix indexing and need no accumulated frame state.
    for(uint8_t y=0;y<=64;y++) {
        uint16_t arrival=(uint16_t)y*1000/80;
        if((uint32_t)arrival*80/1000<y) arrival++;
        check(light_test_drop_red(arrival,0,y)==224,"first drop head reaches successive physical Y coordinates from top to bottom");
        if(y) check(light_test_drop_red(arrival-1,0,y)==0,"a drop cannot illuminate a lower point before its head arrives");
    }
    check(light_test_drop_red(1000,0,64)==0,"pool does not prefill the bottom before the first droplets arrive");
    check(light_test_drop_red(1100,0,64)==160 && light_test_drop_red(1100,0,0)==0,"first droplets leave a bottom pool while top remains unfilled");
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
        uint16_t fills_at=1000+((uint32_t)(65-physical_points[led].y)*5300+64)/65;
        for(uint16_t t=fills_at;t<6500;t+=37) check(light_test_drop_red(t,physical_points[led].x,physical_points[led].y)==160,"filled physical positions stay red through later drops");
    }
    mock_now=start+21000; housekeeping_task_user();
    action=0; check(command(12,&action,1)==0 && !response[8] && !response[9] && !u16(response+10),"timeout ends autonomously without stop or further host traffic");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) check(!rendered[led].r && !rendered[led].g && !rendered[led].b,"normal black standby rendering restored automatically");
    check(brightness==0 && active_generation==initial_generation && active_crc==initial_crc && lighting_last_activity==initial_activity,"test does not change dimmer, generation, CRC or standby activity");
    check(writes==initial_writes && reloads==initial_reloads && !memcmp(backing_before,mock_backing,sizeof(backing_before)),"test performs no EEPROM write, reload or backing mutation");
    check(volume_taps==initial_taps && midi_messages==initial_midi && boot_jumps==initial_jumps && !macro_running,"test produces no media, MIDI, macro or bootloader action");
    for(uint16_t i=0;i<256;i++) check(!registered[i],"test generates no key reports");
    check_readback(config,IG_MAX_SIZE);
    // Busy prerequisites are checked before mutation; status/stop remain usable.
    action=1; stage_open=true; check(command(12,&action,1)==3 && !light_test_active,"upload blocks overlay start"); stage_open=false;
    macro_running=true; check(command(12,&action,1)==3 && !light_test_active,"macro blocks overlay start"); macro_running=false;
    pressed_rows[0]=1; check(command(12,&action,1)==3 && !light_test_active,"held key blocks overlay start"); pressed_rows[0]=0;
    mock_reconnect_pending=true; check(command(12,&action,1)==3 && !light_test_active,"pending USB restart blocks overlay start"); mock_reconnect_pending=false;
    lighting_suspended=true; check(command(12,&action,1)==3 && !light_test_active,"suspended USB blocks overlay start"); lighting_suspended=false;
    check(command(12,&action,1)==0,"start after temporary busy condition"); action=2;
    check(command(12,&action,1)==0 && !light_test_active,"explicit stop ends overlay immediately");
    check(command(12,&action,1)==0,"repeated stop stays idempotent");
    action=1; check(command(12,&action,1)==0,"start for suspend test"); suspend_power_down_user();
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) check(!rendered[led].r && !rendered[led].g && !rendered[led].b,"USB suspend always cancels overlay and renders darkness");
    suspend_wakeup_init_user(); check(!light_test_active,"USB resume never resurrects an old test");
    for(uint8_t kind=1;kind<=2;kind++) {
        action=1; check(command(12,&action,1)==0,"start for physical cancellation");
        keyrecord_t event={.event={.type=kind,.pressed=true,.key={0,0}}};
        check(process_record_user(4,&event) && !light_test_active,"physical key or encoder cancels only overlay while preserving its normal action");
    }
#ifdef IG_HAS_SLIDER
    prime_slider(512);
    action=1; check(command(12,&action,1)==0,"start for slider isolation"); mock_now+=20; slider_task(); check(light_test_active,"slider's unchanged sample does not stop test");
    analog_sample=525; mock_now+=20; slider_task(); check(light_test_active,"single small ADC step cannot stop explicit overlay");
    analog_sample=0; mock_now+=20; slider_task(); check(light_test_active,"single zero ADC sample cannot stop explicit overlay");
    sample_slider(1000,3); check(light_test_active && !lighting_idle && lighting_last_activity==mock_now,"confirmed slider movement wakes standby without cancelling explicit overlay");
    action=2; check(command(12,&action,1)==0 && !light_test_active,"explicit stop remains available after slider movement");
#endif
    static const uint16_t abort_times[]={500,2200,5000,15000,20000};
    for(uint8_t i=0;i<sizeof(abort_times)/sizeof(abort_times[0]);i++) {
        action=1; check(command(12,&action,1)==0,"start for cancellation in each phase"); mock_now+=abort_times[i];
        action=2; check(command(12,&action,1)==0 && !light_test_active && !response[8] && !response[9] && !u16(response+10),"explicit stop in every phase returns canonical inactive state");
        action=1; check(command(12,&action,1)==0,"start for suspend in each phase"); mock_now+=abort_times[i];
        suspend_power_down_user(); check(!light_test_active,"suspend ends every phase immediately"); suspend_wakeup_init_user();
        check(!light_test_active,"resume does not restart any phase");
    }
    mock_now=0xFFFFFFF0UL; action=1; check(command(12,&action,1)==0,"overlay starts close to timer wrap");
    mock_now=0xFFFFFFF0UL+20999UL; action=0; check(command(12,&action,1)==0 && response[8]==1 && u16(response+10)==1,"timer wrap preserves exact last millisecond");
    mock_now=0xFFFFFFF0UL+21000UL; housekeeping_task_user(); check(!light_test_active,"timer wrap still expires at the fixed deadline");
    check(writes==initial_writes && !memcmp(backing_before,mock_backing,sizeof(backing_before)),"all cancellation and failure paths preserve persistent bytes");
}

static void lighting_slider_checks(void) {
#ifdef IG_HAS_SLIDER
    // Exercise the actual RAW12/housekeeping path for an entire manual test.
    // Include a single-sample conversion-error value, small jitter and full travel.
    static const int16_t samples[]={512,511,518,525,512,0,512,1023,0,1000};
    uint8_t config[IG_SIZE], backing_before[sizeof(mock_backing)];
    for(uint8_t mode=0;mode<=3;mode++) {
        reset(); make_config(config,0x80); config[16]=mode; install(config);
        mock_now=UINT32_MAX-560; prime_slider(512);
        unsigned initial_writes=writes, initial_reloads=reloads;
        uint32_t generation=active_generation, crc=active_crc;
        memcpy(backing_before,mock_backing,sizeof(backing_before));
        uint8_t action=1;
        check(command(12,&action,1)==0 && response[8] && u16(response+10)==21000,"every slider mode starts the full manual test");
        uint32_t started=mock_now;
        for(uint16_t elapsed=20;elapsed<21000;elapsed+=20) {
            mock_now=started+elapsed;
            analog_sample=elapsed>=20940?1023:samples[(elapsed/20-1)%(sizeof(samples)/sizeof(samples[0]))];
            housekeeping_task_user();
            action=0;
            check(command(12,&action,1)==0 && response[8] && u16(response+10)==21000-elapsed,"slider jitter and full travel never shorten or extend explicit test deadline across timer wrap");
            rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
            if(elapsed<2000) for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++)
                check(rendered[led].r==reference_pwm(160) && rendered[led].g==reference_pwm(160) && rendered[led].b==reference_pwm(160),"manual white frame is independent of live slider brightness");
            else if(elapsed<2500) for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++)
                check(!rendered[led].r && !rendered[led].g && !rendered[led].b,"manual dark pause is independent of live slider brightness");
        }
        mock_now=started+21000; analog_sample=1023; housekeeping_task_user(); action=0;
        check(command(12,&action,1)==0 && !response[8] && !response[9] && !u16(response+10),"slider movement cannot postpone autonomous21second expiration");
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        check(brightness==255 && rendered[0].r==reference_pwm(config[IG_RGB_OFFSET]) && rendered[0].g==reference_pwm(config[IG_RGB_OFFSET+1]) && rendered[0].b==reference_pwm(config[IG_RGB_OFFSET+2]),"normal saved colors return with the retained live dimmer after test");
        check(writes==initial_writes && reloads==initial_reloads && active_generation==generation && active_crc==crc && !memcmp(backing_before,mock_backing,sizeof(backing_before)),"all slider modes and noisy samples preserve persistent configuration byte for byte");
        check((mode==1?midi_messages>0:midi_messages==0) && (mode==2?volume_taps>0:volume_taps==0),"manual overlay does not suppress or invent configured slider actions");
        check_readback(config,IG_SIZE);
    }
#endif
}

static void screensaver_checks(void) {
    uint8_t config[IG_MAX_SIZE], backing_before[sizeof(mock_backing)], action=0;
    reset(); make_v4(config); config[29]=2;
    check(validate_config(config,IG_MAX_SIZE),"schema4 accepts autonomous screensaver mode2");
    install_size(config,IG_MAX_SIZE);
    unsigned initial_writes=writes, initial_reloads=reloads;
    uint32_t generation=active_generation, crc=active_crc;
    memcpy(backing_before,mock_backing,sizeof(backing_before));
    uint32_t idle_at=lighting_last_activity+10000;
    mock_now=idle_at-1; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!lighting_idle && rendered[0].r==reference_pwm(config[IG_RGB_OFFSET]),"screensaver never begins before configured timeout");
    static const uint16_t phases[]={0,1999,2000,2499,2500,3199,3200,12499,12500,13500,18800,19000,20999};
    for(uint8_t cycle=0;cycle<4;cycle++) for(uint8_t i=0;i<sizeof(phases)/sizeof(phases[0]);i++) {
        mock_now=idle_at+(uint32_t)cycle*21000+phases[i];
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT/2);
        mock_now+=19; rgb_matrix_indicators_advanced_user(RGB_MATRIX_LED_COUNT/2,RGB_MATRIX_LED_COUNT);
        check(lighting_idle && !light_test_active,"automatic loop never acquires manual test state");
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
            ig_rgb_t expected=reference_output(light_test_pixel(phases[i],led));
            check(!memcmp(&rendered[led],&expected,sizeof(expected)),"autonomous loops use identical full sequence and one frame timestamp");
        }
        action=0; check(command(12,&action,1)==0 && !response[8] && !response[9] && !u16(response+10),"RAW12 status stays inactive throughout automatic screensaver");
    }
    check(command(1,NULL,0)==0 && command(11,NULL,0)==0 && command(13,NULL,0)==0,"read-only discovery remains available during screensaver");
    uint8_t scale=17; check(command(10,&scale,1)==0 && brightness==17 && lighting_idle,"runtime dimmer write is allowed without waking or persisting screensaver");
    mock_now=idle_at+4*21000; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(160) && rendered[0].g==reference_pwm(160),"complete test sequence remains visible while saved runtime dimmer is retained");
    mock_now=idle_at+4*21000+2200; action=1; check(command(12,&action,1)==0,"explicit manual test can override an automatic dark gap");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(160) && rendered[0].g==reference_pwm(160) && light_test_active,"manual test starts at white regardless of automatic loop phase");
    action=2; check(command(12,&action,1)==0,"explicit manual stop while idle"); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!rendered[0].r && lighting_idle,"manual stop resumes the unchanged automatic phase");
    for(uint8_t kind=1;kind<=3;kind++) {
        keyrecord_t physical={.event={.type=kind,.pressed=true,.key={0,0}}};
        check(process_record_user(KC_NO,&physical) && !lighting_idle && !light_test_active,"key and both encoder directions cancel screensaver without swallowing normal action");
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        check(rendered[0].r==reference_pwm(ig_effect_scale(config[IG_RGB_OFFSET],17)),"physical wake restores retained normal color and dimmer");
        mock_now=lighting_last_activity+10000; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(lighting_idle,"new full idle interval restarts screensaver");
    }
#ifdef IG_HAS_SLIDER
    prime_slider(512);
    check(lighting_idle,"disabled slider baseline does not wake screensaver");
    sample_slider(1000,3); check(!lighting_idle && brightness==17,"confirmed disabled-slider movement wakes without changing retained dimmer");
    mock_now=lighting_last_activity+10000; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    action=1; check(command(12,&action,1)==0 && light_test_active && lighting_idle,"manual test starts over autonomous screensaver before slider wake");
    sample_slider(512,3);
    check(light_test_active && !lighting_idle && lighting_last_activity==mock_now,"slider wakes automatic screensaver while leaving explicit test running");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(160) && rendered[0].g==reference_pwm(160),"manual test retains rendering priority after automatic wake");
    action=2; check(command(12,&action,1)==0,"explicit stop after automatic slider wake");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(ig_effect_scale(config[IG_RGB_OFFSET],17)) && !lighting_idle,"normal rendering resumes after explicit stop with newly reset standby interval");
#endif
    suspend_power_down_user(); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) check(!rendered[led].r && !rendered[led].g && !rendered[led].b,"USB suspend forces every screensaver LED off");
    suspend_wakeup_init_user(); check(!lighting_idle,"USB resume starts normal idle interval, not a stale screensaver phase");
    mock_now=UINT32_MAX-5000; lighting_activity(); mock_now+=10000;
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(lighting_idle && rendered[0].r==reference_pwm(160) && rendered[0].g==reference_pwm(160),"screensaver enters phase zero across unsigned timer wrap");
    mock_now+=21000; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==reference_pwm(160) && rendered[0].g==reference_pwm(160),"wrapped automatic timer repeats the full21second loop");
    check(writes==initial_writes && reloads==initial_reloads && active_generation==generation && active_crc==crc && !memcmp(backing_before,mock_backing,sizeof(backing_before)),"all autonomous loops and wake paths are entirely volatile");
    check_readback(config,IG_MAX_SIZE);
    check(begin_size(config,IG_MAX_SIZE,generation)==0 && stage_open && lighting_idle,"screensaver does not block starting an upload");
    stage_size_all(config,IG_MAX_SIZE); check(commit_size(config,IG_MAX_SIZE,generation)==0,"screensaver does not block verified configuration commit");
    check(!lighting_idle && lighting_last_activity==mock_now && brightness==255,"successful commit restarts the idle deadline using existing commit semantics");
    check_readback(config,IG_MAX_SIZE);
    unsigned persisted_writes=writes; keyboard_post_init_user();
    check(active_u16(4)==4 && active_byte(29)==2 && !lighting_idle && writes==persisted_writes,"reboot loads retained screensaver mode without rewriting it");
    config[26]=0; install_size(config,IG_MAX_SIZE); mock_now+=4000000; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!lighting_idle && rendered[0].r==reference_pwm(config[IG_RGB_OFFSET]),"disabled standby keeps mode2 stored but never starts the automatic pattern");
}
