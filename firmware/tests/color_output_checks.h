// Actual runtime/output checks. The oracle evaluates the sRGB formula independently
// in floating point; no hardware, driver library or stored-profile rewrite is used.
static void color_output_checks(void) {
    static const uint8_t vectors[][2] = {
        {0,0},{6,0},{7,1},{10,1},{11,1},{32,4},{66,14},{71,16},
        {127,54},{128,55},{160,90},{199,146},{212,168},{240,222},{255,255}
    };
    for(unsigned i=0;i<sizeof(vectors)/sizeof(vectors[0]);i++)
        check(ig_srgb_to_pwm(vectors[i][0])==vectors[i][1],"published sRGB threshold, mixed-color and endpoint vectors are exact");
    for(unsigned value=0;value<256;value++) {
        uint8_t expected=reference_pwm((uint8_t)value);
        check(ig_srgb_to_pwm(value)==expected,"all256 production lookup values match independent standard sRGB formula");
        check(expected<=value && (!value || expected>=reference_pwm(value-1)),"output table is monotonic and never increases channel duty");
        lighting_output(0,value,0,0);
        check(rendered[0].r==expected && !rendered[0].g && !rendered[0].b,"red-only output never introduces green or blue");
        lighting_output(0,0,value,0);
        check(!rendered[0].r && rendered[0].g==expected && !rendered[0].b,"green-only output never introduces red or blue");
        lighting_output(0,0,0,value);
        check(!rendered[0].r && !rendered[0].g && rendered[0].b==expected,"blue-only output never introduces red or green");
        lighting_output(0,value,value,value);
        check(rendered[0].r==expected && rendered[0].g==expected && rendered[0].b==expected,"neutral channels retain equal duty without invented white-point gains");
    }
    lighting_output(0,255,212,71);
    check(rendered[0].r==255 && rendered[0].g==168 && rendered[0].b==16,"FFD447 final output decodes exactly once to255168016");
    lighting_output(0,240,199,66);
    check(rendered[0].r==222 && rendered[0].g==146 && rendered[0].b==14,"FFD447 after240brightness decodes once to222146014");

    uint8_t config[IG_MAX_SIZE], saved[sizeof(mock_backing)];
    reset(); make_v4(config); config[26]=0;
    for(unsigned at=IG_RGB_OFFSET;at<IG_MACRO_OFFSET;at+=3) {
        config[at]=255; config[at+1]=212; config[at+2]=71;
    }
    config[IG_SIZE+2]=240;
    install_size(config,IG_MAX_SIZE);
    unsigned before=writes, reload_before=reloads;
    uint32_t generation=active_generation, crc=active_crc;
    memcpy(saved,mock_backing,sizeof(saved));
    for(unsigned scale=0;scale<256;scale++) {
        uint8_t desired=scale;
        check(command(10,&desired,1)==0,"runtime dimmer remains available across all256 levels");
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        ig_rgb_t expected={reference_pwm((uint16_t)240*scale/255),reference_pwm((uint16_t)199*scale/255),reference_pwm((uint16_t)66*scale/255)};
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++)
            check(!memcmp(&rendered[led],&expected,sizeof(expected)),"every physical key and side applies sRGB transfer after both brightness scales, exactly once");
    }
    check(writes==before && reloads==reload_before && active_generation==generation && active_crc==crc && !memcmp(saved,mock_backing,sizeof(saved)),"rendering and runtime dimming preserve original profile bytes, generation and CRC");
    check_readback(config,IG_MAX_SIZE);

    // All modes still generate encoded colors. Only the final driver boundary changes.
    for(uint8_t mode=0;mode<=20;mode++) {
        config[IG_SIZE]=mode; install_size(config,IG_MAX_SIZE); brightness=128; mock_now=1973;
        effect_hit_count=1; effect_hits[0]=(ig_effect_hit_t){1900,28,32,0};
        rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
            ig_rgb_t encoded=ig_effect_pixel(config+IG_SIZE,(ig_rgb_t){255,212,71},led,g_led_config.point[led].x,g_led_config.point[led].y,(g_led_config.flags[led]&LED_FLAG_UNDERGLOW)!=0,mock_now,effect_hits,effect_hit_count);
            ig_rgb_t expected={reference_pwm((uint16_t)encoded.r*128/255),reference_pwm((uint16_t)encoded.g*128/255),reference_pwm((uint16_t)encoded.b*128/255)};
            check(!memcmp(&rendered[led],&expected,sizeof(expected)),"every effect shares the same single final output transform after dimming");
        }
    }
    make_v3(config); memset(config+IG_SEQUENCE_OFFSET,0,IG_SEQUENCE_BYTES);
    config[IG_SEQUENCE_OFFSET]=1; config[IG_SEQUENCE_OFFSET+1]=255; config[IG_SEQUENCE_OFFSET+2]=212; config[IG_SEQUENCE_OFFSET+3]=71;
    config[IG_SIZE+2]=240; install_size(config,IG_MAX_SIZE); mock_now=1729;
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++)
        check(rendered[led].r==222 && rendered[led].g==146 && rendered[led].b==14,"custom color sequences use the same corrected mixed color as static output");
    check_readback(config,IG_MAX_SIZE);

    // Manual and automatic patterns go through the same boundary, without changing
    // their encoded pixels, frame timing, timeout or persistent configuration.
    make_v4(config); config[29]=2; install_size(config,IG_MAX_SIZE);
    before=writes; reload_before=reloads; generation=active_generation; crc=active_crc;
    memcpy(saved,mock_backing,sizeof(saved));
    uint8_t action=1; check(command(12,&action,1)==0,"explicit test starts for output boundary verification");
    for(uint16_t elapsed=0;elapsed<21000;elapsed+=137) {
        mock_now=light_test_started+elapsed; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
            ig_rgb_t expected=reference_output(light_test_pixel(elapsed,led));
            check(!memcmp(&rendered[led],&expected,sizeof(expected)),"manual test pixels are transformed once in every phase");
        }
    }
    action=2; check(command(12,&action,1)==0,"explicit test stop remains available after output change");
    mock_now=lighting_last_activity+10000+13573; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
        ig_rgb_t expected=reference_output(light_test_pixel(13573,led));
        check(!memcmp(&rendered[led],&expected,sizeof(expected)),"automatic screensaver uses the same final conversion");
    }
    suspend_power_down_user(); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) check(!rendered[led].r && !rendered[led].g && !rendered[led].b,"suspend remains exact zero after conversion");
    check(writes==before && reloads==reload_before && active_generation==generation && active_crc==crc && !memcmp(saved,mock_backing,sizeof(saved)),"patterns and output conversion never modify the saved configuration");
    check_readback(config,IG_MAX_SIZE);
}
