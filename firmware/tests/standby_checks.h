// Actual runtime coverage, included by runtime_tests.c. No USB or physical storage APIs.
static void make_v4(uint8_t *config) {
    make_v2(config); put16(config+4,4); put16(config+12,IG_SEQUENCE_EVENTS);
    config[26]=1; put16(config+27,10); config[29]=0; config[30]=10;
}
static void standby_checks(void) {
    uint8_t config[IG_MAX_SIZE], bad[IG_MAX_SIZE];
    reset(); make_v4(config);
    check(validate_config(config,IG_MAX_SIZE),"schema4 permits canonical empty palette and standby");
    memcpy(bad,config,sizeof(bad)); put16(bad+4,3); memset(bad+26,0,6);
    check(!validate_config(bad,IG_MAX_SIZE),"empty sequence pool remains noncanonical in schema3");
    const uint8_t offsets[]={26,27,27,29,30,30,31};
    const uint16_t values[]={2,9,3601,3,0,101,1};
    for(unsigned i=0;i<sizeof(offsets);i++) {
        memcpy(bad,config,sizeof(bad)); if(offsets[i]==27) put16(bad+27,values[i]); else bad[offsets[i]]=values[i];
        check(!validate_config(bad,IG_MAX_SIZE),"invalid standby field rejected before storage");
    }
    for(unsigned timeout=10;timeout<=3600;timeout++) { put16(config+27,timeout); check(validate_config(config,IG_MAX_SIZE),"every allowed whole-second timeout validates"); }
    put16(config+27,10); install_size(config,IG_MAX_SIZE);
    check(command(1,NULL,0)==0 && (response[15]&0xF8)==0xF8 && u16(response+18)==IG_SEQUENCE_EVENTS && u16(response+30)==4,"INFO exposes standby and reduced event capacity");
    check_readback(config,IG_MAX_SIZE);
    unsigned before=writes; uint32_t generation=active_generation, crc=active_crc;
    mock_now=lighting_last_activity+9999; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(config[IG_RGB_OFFSET]) && !lighting_idle,"standby cannot activate early");
    mock_now++; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!rendered[0].r && !rendered[RGB_MATRIX_LED_COUNT-1].b && lighting_idle && brightness==255,"timeout turns all LEDs off without changing runtime dimmer");
    mock_now=lighting_last_activity; check(lighting_scale(mock_now)==0,"idle latch survives a complete timer wrap");
    uint8_t desired=128; check(command(10,&desired,1)==0 && command(9,NULL,0)==0 && response[8]==128 && lighting_idle,"runtime relay cannot wake standby or observe idle as slider brightness");
    keyrecord_t physical={.event={.pressed=true,.type=1,.key={0,0}}};
    check(process_record_user(KC_NO,&physical),"wake does not consume physical key even when remapped to no action");
    check(!lighting_idle && lighting_scale(mock_now)==128,"physical key restores previous runtime brightness");
    for(unsigned type=2;type<=3;type++) {
        mock_now=lighting_last_activity+10000; check(lighting_scale(mock_now)==0,"idle before encoder test");
        keyrecord_t encoder={.event={.pressed=true,.type=(int)type}};
        check(process_record_user(KC_NO,&encoder) && !lighting_idle && lighting_scale(mock_now)==128,"both encoder directions wake without swallowing action");
    }
    pressed_rows[0]=1; mock_now+=15000; housekeeping_task_user();
    check(lighting_scale(mock_now)==128 && !lighting_idle,"held physical key prevents standby");
    pressed_rows[0]=0; physical.event.pressed=false; process_record_user(KC_NO,&physical);
    mock_now+=9999; check(lighting_scale(mock_now)==128,"release restarts full idle interval");
    mock_now++; check(lighting_scale(mock_now)==0,"idle starts after release timeout");
    keyrecord_t synthetic={.event={.pressed=true,.type=4}}; process_record_user(KC_NO,&synthetic);
    check(lighting_idle,"synthetic events are not physical wake events");
    check(command(1,NULL,0)==0 && command(9,NULL,0)==0 && lighting_idle,"INFO and runtime reads do not prevent standby");
    check(writes==before && active_generation==generation && active_crc==crc,"idle, wake and dimmer relay never persist configuration");

    // Dim multiplies the existing runtime dimmer and affects every LED, including side LEDs.
    config[29]=1;
    for(unsigned percent=1;percent<=100;percent++) {
        config[30]=percent; install_size(config,IG_MAX_SIZE); brightness=128; mock_now=lighting_last_activity+10000;
        check(lighting_scale(mock_now)==128*percent/100,"every dim percentage multiplies existing runtime scale");
    }
    config[30]=10; install_size(config,IG_MAX_SIZE); brightness=128; mock_now=lighting_last_activity+10000;
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(ig_effect_scale(config[IG_RGB_OFFSET],12)) && rendered[RGB_MATRIX_LED_COUNT-1].b==reference_pwm(ig_effect_scale(220,12)),"dim applies to keys and sides after effect brightness");
    uint8_t cached=effect_frame_brightness; process_record_user(KC_NO,&physical);
    rgb_matrix_indicators_advanced_user(1,RGB_MATRIX_LED_COUNT);
    check(effect_frame_brightness==cached,"partial frame keeps one brightness snapshot when activity occurs midway");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(effect_frame_brightness==128,"next complete frame returns to normal brightness");

    // Wrap-safe initial deadline, suspend always off, and resume restarts the configured interval.
    config[29]=0; install_size(config,IG_MAX_SIZE); brightness=88;
    mock_now=UINT32_MAX-5000; lighting_activity(); mock_now+=9999;
    check(lighting_scale(mock_now)==88,"idle deadline crosses uint32 wrap without early sleep");
    mock_now++; check(lighting_scale(mock_now)==0,"wrapped deadline expires exactly");
    suspend_power_down_user(); before=writes;
    process_record_user(KC_NO,&physical); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(lighting_suspended && !rendered[0].r && brightness==88,"USB suspend overrides activity while preserving dimmer");
    suspend_wakeup_init_user(); check(!lighting_suspended && !lighting_idle && lighting_scale(mock_now)==88,"USB resume restores normal output and restarts idle interval");
    check(writes==before,"USB suspend and resume never touch configuration storage");
    before=writes; generation=active_generation; crc=active_crc; keyboard_post_init_user(); check(writes==before && active_generation==generation && active_crc==crc && active_u16(4)==4,"schema4 restart preserves stored slot without rewrite");
    check_readback(config,IG_MAX_SIZE);

    // Standby remains disabled for retained legacy schemas and disabled schema4 settings.
    config[26]=0; install_size(config,IG_MAX_SIZE); mock_now+=4000000; check(lighting_scale(mock_now)==255,"disabled schema4 never idles");
    for(unsigned schema=1;schema<=3;schema++) {
        if(schema==1) make_config(config,0xC0); else if(schema==2) make_v2(config); else make_v3(config);
        install_size(config,schema==1?IG_SIZE:IG_MAX_SIZE); before=writes; crc=active_crc; generation=active_generation;
        mock_now+=4000000; check(lighting_scale(mock_now)==255,"legacy schema lighting remains awake");
        keyboard_post_init_user(); check(writes==before && active_crc==crc && active_generation==generation && active_u16(4)==schema,"legacy startup stays byte-preserving and write-free");
        suspend_power_down_user(); check(lighting_scale(mock_now)==0,"USB suspend turns off legacy config lighting too"); suspend_wakeup_init_user();
    }
#ifdef IG_HAS_SLIDER
    reset(); make_v4(config); install_size(config,IG_MAX_SIZE); before=writes;
    prime_slider(500);
    uint32_t initial_activity=lighting_last_activity;
    check(initial_activity<mock_now && !volume_taps && !midi_messages && brightness==255,"disabled slider baseline neither wakes nor sends an action");
    mock_now=initial_activity+10000; check(lighting_scale(mock_now)==0,"slider standby initial timeout");
    uint8_t test_action=1; check(command(12,&test_action,1)==0 && light_test_active,"explicit test can begin while standby is latched");
    sample_slider(512,3); check(lighting_idle && light_test_active,"subthreshold slider noise neither wakes standby nor stops manual test");
    sample_slider(536,3);
    check(!lighting_idle && lighting_scale(mock_now)==255 && light_test_active && !volume_taps && !midi_messages && writes==before,"actual movement wakes even disabled slider without cancelling manual test, action or persistence");
    test_action=2; check(command(12,&test_action,1)==0 && !light_test_active && !lighting_idle,"explicit stop retains the idle timer reset by slider movement");
#endif
    // Schema4 can use the same sequence pool and pixel path as schema3.
    reset(); make_v3(config); put16(config+4,4); config[26]=0; put16(config+27,300); config[29]=0; config[30]=10;
    check(validate_config(config,IG_MAX_SIZE),"schema4 color-sequence combination valid"); install_size(config,IG_MAX_SIZE); mock_now=100;
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==reference_pwm(127) && rendered[0].b==reference_pwm(127),"schema4 color sequences render identically to schema3");
}

static void slider_filter_checks(void) {
#ifdef IG_HAS_SLIDER
    uint8_t config[IG_MAX_SIZE], backing_before[sizeof(mock_backing)];
    static const int16_t spikes[]={0,1023,-1};
    // A bad first/second/third sample must never establish a false startup baseline.
    for(uint8_t mode=0;mode<=3;mode++) for(uint8_t fault=0;fault<3;fault++) for(uint8_t at=0;at<3;at++) {
        reset(); make_v4(config); config[16]=mode; install_size(config,IG_MAX_SIZE);
        uint32_t activity=lighting_last_activity; unsigned initial_writes=writes;
        for(uint8_t i=0;i<3;i++) {
            sample_slider(i==at?spikes[fault]:512,1);
            if(i<2) check(slider_last<0 && brightness==255,"startup waits for three real ADC samples before baseline or initial dimmer");
        }
        check(slider_last==63 && slider_activity_position==63 && slider_sample_count==3 && !slider_activity_direction,"startup median rejects one bad sample in every position");
        check(lighting_last_activity==activity && !volume_taps && !midi_messages && writes==initial_writes,"startup priming cannot wake, emit relative actions or write storage");
        check(brightness==(mode==3?126:255),"initial dimmer uses the primed median only in brightness mode");
    }
    for(uint8_t mode=0;mode<=3;mode++) for(uint8_t standby=0;standby<=2;standby++) {
        reset(); make_v4(config); config[16]=mode; config[29]=standby; install_size(config,IG_MAX_SIZE);
        mock_now=UINT32_MAX-5000; lighting_activity(); prime_slider(512);
        uint32_t activity=lighting_last_activity, generation=active_generation, crc=active_crc;
        unsigned initial_writes=writes, initial_reloads=reloads;
        memcpy(backing_before,mock_backing,sizeof(backing_before));
        uint8_t initial_brightness=brightness;
        for(uint8_t fault=0;fault<3;fault++) {
            sample_slider(spikes[fault],1); sample_slider(512,3);
            check(!volume_taps && !midi_messages && brightness==initial_brightness && lighting_last_activity==activity,"isolated endpoint or negative spike has no action or activity in any mode");
        }
        // More than two complete screensaver loops, including the uint32 clock boundary.
        for(unsigned tick=0;tick<2700;tick++) {
            int16_t value=tick%31==0?0:tick%31==10?1023:tick%31==20?-1:(tick&1)?512:525;
            sample_slider(value,1); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
            check(lighting_last_activity==activity,"small jitter and isolated spikes never refresh the idle timestamp");
            check(lighting_idle==((uint32_t)(mock_now-activity)>=10000),"standby deadline and screensaver remain active despite long noisy input");
        }
        sample_slider(512,3); // Settle the unchanged baseline before counting the deliberate step.
        unsigned taps=volume_taps, midi=midi_messages;
        uint32_t moved_at=mock_now;
        sample_slider(800,1); check(lighting_idle,"first deliberate sample is not mistaken for confirmed activity");
        sample_slider(800,1); check(lighting_idle,"first out-of-band median still awaits confirmation");
        sample_slider(800,1); check(!lighting_idle && lighting_last_activity==mock_now && mock_now-moved_at==60,"sustained movement wakes every mode and standby setting within60ms");
        check((mode==1?midi_messages==midi+1:midi_messages==midi) && (mode==2?volume_taps==taps+1:volume_taps==taps),"median preserves one configured relative action for a sustained step");
        // Gradual motion must not disappear into the more sensitive action baseline.
        for(uint8_t reverse=0;reverse<2;reverse++) {
            prime_slider(512); mock_now=lighting_last_activity+10000; (void)lighting_scale(mock_now);
            uint32_t ramp_at=mock_now;
            for(uint8_t step=1;step<=5;step++) {
                sample_slider(reverse?512-step*8:512+step*8,1);
                check(lighting_idle==(step<5),"slow ramp accumulates against independent wake anchor in both directions");
            }
            check(mock_now-ramp_at==100 && lighting_last_activity==mock_now,"deliberate one-step-per-sample ramp wakes within100ms");
        }
        check(writes==initial_writes && reloads==initial_reloads && active_generation==generation && active_crc==crc && !memcmp(backing_before,mock_backing,sizeof(backing_before)),"filter and all wake decisions leave persisted bytes unchanged");
    }
    // Exactly two normalized steps cannot wake; three require repeated median evidence.
    reset(); make_v4(config); install_size(config,IG_MAX_SIZE); prime_slider(512);
    mock_now=lighting_last_activity+10000; (void)lighting_scale(mock_now);
    sample_slider(525,4); check(lighting_idle && !slider_activity_direction,"positive two-step boundary remains inside wake deadband");
    sample_slider(512,3); sample_slider(495,4); check(lighting_idle && !slider_activity_direction,"negative two-step boundary remains inside wake deadband");
    sample_slider(512,3);
    for(unsigned i=0;i<30;i++) sample_slider(i&1?536:488,1);
    check(lighting_idle,"alternating out-of-band directions cannot confirm wake");
    sample_slider(512,3); check(!slider_activity_direction && lighting_idle,"return inside deadband clears pending wake");
    sample_slider(536,2); check(lighting_idle && slider_activity_direction==1,"three-step median begins pending wake without waking early");
    sample_slider(536,1); check(!lighting_idle && !slider_activity_direction && slider_activity_position==66,"confirmed three-step movement establishes new wake anchor");

    // Fresh configuration and reboot must discard old samples and pending movement.
    for(uint8_t mode=0;mode<=3;mode++) for(uint8_t change=0;change<3;change++) {
        reset(); make_v4(config); config[16]=mode; install_size(config,IG_MAX_SIZE); prime_slider(512);
        sample_slider(1000,2); check(slider_activity_direction==1,"reset fixture has an unconfirmed old movement");
        if(change==0) { config[16]=(mode+1)%4; install_size(config,IG_MAX_SIZE); }
        else if(change==1) { config[IG_RGB_OFFSET]^=1; install_size(config,IG_MAX_SIZE); }
        else keyboard_post_init_user();
        check(slider_sample_count==0 && slider_sample_index==0 && slider_last<0 && !slider_activity_direction,"mode, generation and reboot clear all filter validity and pending state");
        uint32_t activity=lighting_last_activity; unsigned taps=volume_taps, midi=midi_messages, initial_writes=writes;
        uint8_t dimmer=brightness;
        sample_slider(0,1); sample_slider(512,1);
        check(slider_last<0 && brightness==dimmer,"reset does not reuse old sample or act on its first two new readings");
        sample_slider(512,1);
        check(slider_last==63 && slider_activity_position==63 && !slider_activity_direction && lighting_last_activity==activity,"reset re-primes from three actual values without false wake");
        check(volume_taps==taps && midi_messages==midi && writes==initial_writes,"re-priming never emits relative action or persistent write");
    }
    // Clamping and a narrow but valid calibration preserve physical endpoints.
    for(uint8_t narrow=0;narrow<2;narrow++) for(uint8_t reverse=0;reverse<2;reverse++) {
        reset(); make_v4(config); if(narrow){put16(config+18,500);put16(config+20,540);} install_size(config,IG_MAX_SIZE);
        prime_slider(reverse?1023:0); mock_now=lighting_last_activity+10000; (void)lighting_scale(mock_now);
        sample_slider(reverse?0:1023,3);
        check(!lighting_idle && slider_activity_position==(reverse?0:127),"filtered full travel wakes with clamped endpoints under either calibration");
    }
#endif
}
