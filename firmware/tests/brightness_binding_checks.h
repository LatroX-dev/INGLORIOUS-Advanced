// Actual runtime coverage for physical brightness bindings. No hardware access.
static void brightness_binding_checks(void) {
    uint8_t config[IG_MAX_SIZE], bad[IG_MAX_SIZE];
    reset(); make_v4(config);
    // Test fixed wire codes independently of the production aliases.
    put16(config + IG_KEYS_OFFSET, 0x7849);
    put16(config + IG_ENCODER_OFFSET, 0x784A);
    put16(config + IG_ENCODER_OFFSET + 2, 0x7849);
    check(validate_config(config, IG_MAX_SIZE), "brightness keys and both encoder directions validate");
    const uint16_t unsupported[] = {0x7848, 0x784B, 0x7820};
    for (unsigned i = 0; i < sizeof(unsupported)/sizeof(unsupported[0]); ++i) {
        memcpy(bad, config, sizeof(bad)); put16(bad + IG_KEYS_OFFSET, unsupported[i]);
        check(!validate_config(bad, IG_MAX_SIZE), "neighboring QMK RGB operations remain unsupported");
        memcpy(bad, config, sizeof(bad)); put16(bad + IG_ENCODER_OFFSET, unsupported[i]);
        check(!validate_config(bad, IG_MAX_SIZE), "neighboring encoder RGB operations remain unsupported");
    }
    install_size(config, IG_MAX_SIZE);
    check(keycode_at_encodermap_location(0, 0, false) == 0x784A && keycode_at_encodermap_location(0, 0, true) == 0x7849,
          "stored encoder direction resolves the expected brightness action");
    check(keycode_at_keymap_location(0, 0, 0) == 0x7849, "stored physical key resolves brightness up");
    unsigned initial_writes = writes;
    uint32_t generation = active_generation, crc = active_crc;
    keyrecord_t turn = {.event = {.pressed = true, .type = 2}};
    for (unsigned start = 0; start <= 255; ++start) {
        brightness = start;
        turn.event.type = 2; turn.event.pressed = true;
        check(!process_record_user(0x7849, &turn) && brightness == (start > 238 ? 255 : start + 17),
              "clockwise brightness step saturates for every starting value and is consumed");
        uint8_t up = brightness; turn.event.pressed = false;
        check(!process_record_user(0x7849, &turn) && brightness == up, "up release is consumed without a second step");
        brightness = start;
        turn.event.type = 3; turn.event.pressed = true;
        check(!process_record_user(0x784A, &turn) && brightness == (start < 17 ? 0 : start - 17),
              "counterclockwise brightness step saturates for every starting value and is consumed");
        uint8_t down = brightness; turn.event.pressed = false;
        check(!process_record_user(0x784A, &turn) && brightness == down, "down release is consumed without a second step");
    }
    keyrecord_t key = {.event = {.pressed = true, .type = 1, .key = {0, 0}}};
    brightness = 100;
    check(!process_record_user(0x7849, &key) && brightness == 117, "ordinary key can raise keyboard brightness");
    key.event.pressed = false;
    check(!process_record_user(0x7849, &key) && brightness == 117, "ordinary key release does not repeat brightness action");
    key.event.pressed = true;
    check(!process_record_user(0x784A, &key) && brightness == 100, "ordinary key can lower keyboard brightness");
    turn.event.pressed = true; layer_state = 2;
    held_layer_keys[0] = pressed_rows[0] = 1;
    check(!process_record_user(0x784A, &turn) && brightness == 83 && layer_state == 2 && held_layer_keys[0] == 1,
          "held Fn and its selected layer do not prevent encoder brightness or alter Fn state");
    held_layer_keys[0] = pressed_rows[0] = 0; layer_state = 0;
    mock_now += 10000;
    check(lighting_scale(mock_now) == 0 && lighting_idle, "brightness binding starts from standby");
    check(!process_record_user(0x7849, &turn) && !lighting_idle && brightness == 100,
          "turning brightness wakes standby and performs its assigned step");
    light_test_active = true; light_test_started = mock_now;
    check(!process_record_user(0x784A, &turn) && !light_test_active && brightness == 83,
          "brightness turn cancels manual lighting overlay like other physical input");
    macro_running = true; macro_held[4 >> 3] |= 1U << (4 & 7); registered[4] = 1;
    check(!process_record_user(0x7849, &turn) && !macro_running && !registered[4] && brightness == 100,
          "brightness press preserves macro cancellation and held-key cleanup");
    check(command(9, NULL, 0) == 0 && response[8] == 100, "brightness bindings share observable runtime dimmer");
    keyrecord_t synthetic = {.event = {.pressed = true, .type = 4}};
    check(!process_record_user(0x7849, &synthetic) && brightness == 100, "nonphysical event cannot synthesize a brightness step");
    rgb_matrix_indicators_advanced_user(0, RGB_MATRIX_LED_COUNT);
    for (uint8_t led = 0; led < RGB_MATRIX_LED_COUNT; ++led) {
        const uint16_t offset = IG_RGB_OFFSET + 3 * led;
        check(rendered[led].r == reference_pwm(ig_effect_scale(config[offset], 100)) &&
              rendered[led].g == reference_pwm(ig_effect_scale(config[offset + 1], 100)) &&
              rendered[led].b == reference_pwm(ig_effect_scale(config[offset + 2], 100)),
              "brightness binding dims every key and side through the existing color output transfer");
    }
    check(writes == initial_writes && active_generation == generation && active_crc == crc,
          "detents, releases, activity and macro cleanup never write flash or mutate the profile");
    check_readback(config, IG_MAX_SIZE);

    // The new input must leave a borrowed upload intact. It only changes runtime state.
    begin_size(config, IG_MAX_SIZE, generation);
    uint8_t staged_before[IG_MAX_SIZE]; memcpy(staged_before, staging, IG_MAX_SIZE);
    uint16_t received = stage_received;
    check(!process_record_user(0x7849, &turn) && brightness == 117 && stage_open && stage_received == received &&
          !memcmp(staged_before, staging, IG_MAX_SIZE) && writes == initial_writes,
          "local brightness input does not touch staged configuration or persistence");
    command(6, NULL, 0);
    check_readback(config, IG_MAX_SIZE);

    // An individual effect and a global effect still use this final dimmer.
    config[IG_SIZE] = 3; config[IG_SIZE + 2] = 190;
    config[IG_OVERRIDE_OFFSET] = 2; config[IG_OVERRIDE_OFFSET + 1] = 80; config[IG_OVERRIDE_OFFSET + 2] = 123;
    install_size(config, IG_MAX_SIZE); mock_now += 413; brightness = 255;
    ig_rgb_t original[RGB_MATRIX_LED_COUNT];
    // Capture the effect in sRGB before the existing output transfer to avoid double decoding.
    uint8_t global[8]; memcpy(global, config + IG_SIZE, sizeof(global));
    for (uint8_t led = 0; led < RGB_MATRIX_LED_COUNT; ++led) {
        uint8_t selected[8]; memcpy(selected, global, sizeof(selected));
        if (!led) { selected[0] = 2; selected[1] = 80; selected[2] = 123; selected[3] = 3; }
        uint16_t offset = IG_RGB_OFFSET + 3 * led;
        ig_rgb_t base = {config[offset], config[offset + 1], config[offset + 2]};
        original[led] = ig_effect_pixel(selected, base, led, g_led_config.point[led].x, g_led_config.point[led].y,
                                       (g_led_config.flags[led] & LED_FLAG_UNDERGLOW) != 0, mock_now, effect_hits, effect_hit_count);
    }
    turn.event.type = 3;
    check(!process_record_user(0x784A, &turn) && brightness == 238, "effect dimmer starts with one counterclockwise detent");
    rgb_matrix_indicators_advanced_user(0, RGB_MATRIX_LED_COUNT);
    for (uint8_t led = 0; led < RGB_MATRIX_LED_COUNT; ++led)
        check(rendered[led].r == reference_pwm(ig_effect_scale(original[led].r, 238)) &&
              rendered[led].g == reference_pwm(ig_effect_scale(original[led].g, 238)) &&
              rendered[led].b == reference_pwm(ig_effect_scale(original[led].b, 238)),
              "knob brightness applies after individual and global effects on all LEDs");
}
