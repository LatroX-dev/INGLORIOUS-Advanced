// SPDX-License-Identifier: GPL-2.0-or-later
// Shared actual-runtime checks; harness adapters supply clocks, matrix and uploads.
static void knob_status_checks(const uint8_t *base);
static void knob_press_checks(const uint8_t *base);
static void knob_press_fixture_checks(const char *directory);
#if IG_HAS_ENCODER
static bool knob_test_event(bool down, uint16_t code) {
    keyrecord_t event = {.event = {.pressed = down, .type = 1, .key = {IG_ENCODER_KEY_ROW, IG_ENCODER_KEY_COL}}};
    if (down) KNOB_TEST_ROWS[IG_ENCODER_KEY_ROW] |= (matrix_row_t)1U << IG_ENCODER_KEY_COL;
    else KNOB_TEST_ROWS[IG_ENCODER_KEY_ROW] &= ~((matrix_row_t)1U << IG_ENCODER_KEY_COL);
    return process_record_user(code, &event);
}
static uint16_t knob_test_offset(uint8_t layer) {
    return IG_KEYS_OFFSET + 2 * (layer * MATRIX_ROWS * MATRIX_COLS + IG_ENCODER_KEY_ROW * MATRIX_COLS + IG_ENCODER_KEY_COL);
}
static void knob_test_hold(uint16_t code) {
    check(!knob_test_event(true, code), "cycling press is deferred");
    KNOB_TEST_NOW += 700; knob_task();
    check(!knob_test_event(false, code), "cycling release is consumed");
}
#endif
static void knob_cycle_checks(const uint8_t *base) {
    uint8_t config[IG_MAX_SIZE];
    memcpy(config, base, sizeof(config)); put16(config + 4, 5); config[17] = 1;
    for (uint8_t layer = 0; layer < 4; ++layer) { config[IG_SIZE + layer * 8 + 6] = 31; config[IG_SIZE + layer * 8 + 7] = 1; }
#if !IG_HAS_ENCODER
    check(!validate_config(config, IG_MAX_SIZE), "encoderless board rejects cycling schema");
    return;
#else
    uint8_t bad[IG_MAX_SIZE];
    for (uint8_t layer = 0; layer < IG_LAYERS; ++layer) put16(config + knob_test_offset(layer), 0xA8);
    for (uint8_t layer = 0; layer < IG_LAYERS; ++layer) {
        put16(config + IG_ENCODER_OFFSET + layer * 4, 0xAA);
        put16(config + IG_ENCODER_OFFSET + layer * 4 + 2, 0xA9);
    }
    check(validate_config(config, IG_MAX_SIZE), "schema5 allows explicit safe knob cycle");
    for (unsigned mask = 0; mask < 256; ++mask) {
        config[17] = mask;
        check(validate_config(config, IG_MAX_SIZE) == (mask > 0 && mask < 16), "only nonempty four-layer cycle mask is accepted");
    }
    config[17] = 1;
    for (unsigned mask = 0; mask < 256; ++mask) { config[IG_SIZE + 6] = mask; check(validate_config(config,IG_MAX_SIZE) == (mask != 0), "cycle selection must contain at least one of eight modes"); }
    config[IG_SIZE + 6] = 31;
    for (unsigned packed = 0; packed < 256; ++packed) {
        config[IG_SIZE + 7] = packed;
        bool valid = (packed & 7) >= 1 && (packed & 7) <= 5 && (packed & 0xE0) == 0 && ((packed >> 3) & 3) <= 2;
        check(validate_config(config,IG_MAX_SIZE) == valid, "step and host key layout have strict bounded encoding");
    }
    config[IG_SIZE + 7] = 1;
    for (unsigned mask = 0; mask < 256; ++mask) { config[31] = mask; check(validate_config(config, IG_MAX_SIZE) == (mask <= 1), "zoom platform bits must be a subset of enabled layers"); }
    config[31] = 0;
    memcpy(bad, config, sizeof(bad)); put16(bad + 4, 4);
    check(!validate_config(bad, IG_MAX_SIZE), "old schema keeps cycle byte reserved");
    check(!validate_config(config, IG_SIZE), "cycling requires exact extended config length");
    static const uint16_t unsafe[] = {0xE0,0xE7,0x0104,0x1F04,0x5221,0x5201,0x5261};
    for (unsigned i = 0; i < sizeof(unsafe)/sizeof(unsafe[0]); ++i) {
        memcpy(bad, config, sizeof(bad)); put16(bad + knob_test_offset(0), unsafe[i]);
        check(!validate_config(bad, IG_MAX_SIZE), "held modifier chord or layer cannot be delayed");
        bad[17] = 8; put16(bad + knob_test_offset(3), 1); put16(bad + knob_test_offset(2), 0xA8);
        check(!validate_config(bad, IG_MAX_SIZE), "transparent press checks even lower layers skipped by active stack");
    }
    memcpy(bad, config, sizeof(bad)); bad[17] = 8;
    put16(bad + knob_test_offset(3), 1); put16(bad + knob_test_offset(2), 1);
    check(validate_config(bad, IG_MAX_SIZE), "safe inherited knob press remains supported");
    put16(bad + knob_test_offset(0), 1);
    check(!validate_config(bad, IG_MAX_SIZE), "transparent cycle press needs a defined base fallback");

    knob_test_install(config); unsigned initial_writes = writes;
    uint32_t generation = active_generation, crc = active_crc;
    check(!knob_test_event(true,0xA8) && !knob_taps, "ordinary press action waits for release");
    KNOB_TEST_NOW += 699; knob_task();
    check(!knob_test_event(false,0xA8) && knob_taps == 1 && knob_tapped_code == 0xA8 && !knob_modes[0], "short press retains exact tap action");
    knob_test_hold(0xA8);
    check(knob_taps == 1 && keycode_at_encodermap_location(0,0,false) == 0x784A && keycode_at_encodermap_location(0,0,true) == 0x7849,
          "700ms hold advances volume to brightness without mute tap");
    check(!knob_test_event(true,0xA8), "long hold starts");
    KNOB_TEST_NOW += 700; knob_task(); KNOB_TEST_NOW += 60000; knob_task();
    check(keycode_at_encodermap_location(0,0,false) == 0xAC && keycode_at_encodermap_location(0,0,true) == 0xAB,
          "held knob changes once to previous and next track");
    check(!knob_test_event(false,0xA8) && knob_taps == 1, "long release never sends short action");
    knob_test_hold(0xA8);
    check(keycode_at_encodermap_location(0,0,false) == 0xD9 && keycode_at_encodermap_location(0,0,true) == 0xDA, "third hold selects CCW up and CW down mouse wheel");
    knob_test_hold(0xA8);
    check(keycode_at_encodermap_location(0,0,false) == 0x7E43 && keycode_at_encodermap_location(0,0,true) == 0x7E42, "fourth hold selects Ctrl zoom");
    knob_test_hold(0xA8);
    check(keycode_at_encodermap_location(0,0,false) == 0xAA && !knob_modes[0], "fifth hold returns to saved volume pair");
    KNOB_TEST_NOW = 0xFFFFFF00UL; knob_test_hold(0xA8);
    check(knob_modes[0] == 2, "hold threshold remains correct across timer wrap");
    check(writes == initial_writes && active_generation == generation && active_crc == crc,
          "mode changes never write flash or alter saved profile identity");

    knob_test_reboot();
    check(!knob_modes[0] && !knob_pending && keycode_at_encodermap_location(0,0,false) == 0xAA,
          "reboot restores saved initial preset while retaining cycle setting");
    knob_test_hold(0xA8); knob_test_install(config);
    check(!knob_modes[0] && !knob_pending, "profile commit restores saved initial preset");
    config[IG_SIZE + 6] = 255; knob_test_install(config);
    const uint16_t all_modes[] = {0x784A,0xAC,0xD9,0x7E43,0xDB,0x7E46,0x7E48,0xAA};
    for (unsigned i = 0; i < 8; ++i) { knob_test_hold(0xA8); check(keycode_at_encodermap_location(0,0,false) == all_modes[i], "full cycle includes horizontal scroll tabs and undo in declared order"); }
    config[IG_SIZE + 6] = 18; knob_test_install(config);
    const uint16_t subset[] = {0x784A,0x7E43,0x784A};
    for (unsigned i = 0; i < 3; ++i) { knob_test_hold(0xA8); check(keycode_at_encodermap_location(0,0,false) == subset[i], "unchecked presets including initial volume are skipped"); }
    config[IG_SIZE + 6] = 31;
    put16(config + IG_ENCODER_OFFSET, 0x50); put16(config + IG_ENCODER_OFFSET + 2, 0x4F);
    knob_test_install(config);
    const uint16_t custom_cycle[] = {0xAA,0x784A,0xAC,0xD9,0x7E43,0x50};
    for (unsigned i = 0; i < 6; ++i) { knob_test_hold(0xA8); check(keycode_at_encodermap_location(0,0,false) == custom_cycle[i], "custom saved pair participates once in cycle"); }
    unsigned prior_taps = knob_taps;
    check(!knob_test_event(true,0xA8), "cancel test begins"); layer_state = 2;
    KNOB_TEST_NOW += 700; knob_task(); check(!knob_test_event(false,0xA8) && !knob_modes[0] && knob_taps == prior_taps,
          "layer change cancels pending gesture without stray tap"); layer_state = 0;
    check(!knob_test_event(true,0xA8), "other key test begins");
    keyrecord_t other = {.event = {.pressed = true, .type = 1, .key = {0,0}}};
    KNOB_TEST_ROWS[0] |= 1; check(process_record_user(4,&other), "other key retains its usual processing");
    KNOB_TEST_NOW += 700; knob_task(); KNOB_TEST_ROWS[0] &= ~1U;
    check(!knob_test_event(false,0xA8) && !knob_modes[0] && knob_taps == prior_taps, "another key cancels cycle and deferred short tap");
    other.event.pressed = true; KNOB_TEST_ROWS[0] |= 1;
    check(process_record_user(0xE0,&other), "already held Control retains its ordinary key event");
    check(knob_test_event(true,0xA8) && !knob_pending, "already held modifier leaves knob press with original QMK processing");
    KNOB_TEST_NOW += 1000; knob_task();
    check(knob_test_event(false,0xA8) && !knob_pending && !knob_modes[0] && knob_taps == prior_taps,
          "knob release after pre-held modifier passes through without cycling or duplicate tap");
    other.event.pressed = false; KNOB_TEST_ROWS[0] &= ~1U;
    check(process_record_user(0xE0,&other), "pre-held modifier releases normally after knob gesture");
    check(!knob_test_event(true,0xA8), "suspend test begins"); suspend_power_down_user();
    KNOB_TEST_NOW += 700; suspend_wakeup_init_user();
    check(!knob_test_event(false,0xA8) && !knob_modes[0] && knob_taps == prior_taps && !knob_pending, "suspend and resume cannot complete stale gesture");
    stage_open = true;
    check(knob_test_event(true,0xA8) && knob_test_event(false,0xA8) && !knob_pending, "staging keeps ordinary knob events and never starts cycling");
    stage_open = false;
    config[17] = 2; put16(config + knob_test_offset(1), 0xA8); knob_test_install(config); layer_state = 2;
    held_layer_keys[0] = KNOB_TEST_ROWS[0] = 1;
    knob_test_hold(0xA8);
    check(!knob_modes[0] && knob_modes[1] == 2 && layer_state == 2 && held_layer_keys[0] == 1,
          "held external Fn permits cycling only the active enabled layer");
    held_layer_keys[0] = KNOB_TEST_ROWS[0] = 0; layer_state = 0;
    check(knob_test_event(true,0xA8) && knob_test_event(false,0xA8), "disabled current layer keeps direct original press events");
    config[17] = 3; config[31] = 2; knob_test_install(config);
    knob_modes[0] = knob_modes[1] = 5;
    check(keycode_at_encodermap_location(0,0,true) == 0x7E42 && keycode_at_encodermap_location(1,0,true) == 0x7E44,
          "each enabled layer selects its own Ctrl or Command zoom");
    keyrecord_t turn = {.event = {.pressed = true, .type = 2}};
    for (uint16_t code = 0x7E42; code <= 0x7E4B; ++code) {
        for (unsigned mods = 0; mods < 256; ++mods) {
            weak_mods = mods; turn.event.pressed = true;
            uint8_t expected_key = code <= 0x7E45 ? ((code & 1) ? 0x56 : 0x57) : code <= 0x7E47 ? 0x2B : 0x1D;
            uint8_t expected_mods = code <= 0x7E45 ? (code >= 0x7E44 ? 8 : 1) : code <= 0x7E47 ? (code == 0x7E46 ? 3 : 1) : (code >= 0x7E4A ? 8 : 1) | ((code & 1) ? 2 : 0);
            check(!process_record_user(code,&turn) && weak_mods == mods && last_registered_mods == (mods | expected_mods) &&
                  last_registered_code == expected_key && !registered[expected_key],
                  "shortcut sends bounded key and exactly restores every existing weak modifier set");
            unsigned reports = keyboard_reports; turn.event.pressed = false;
            check(!process_record_user(code,&turn) && keyboard_reports == reports, "zoom release is consumed without repeated shortcut");
        }
    }
    registered[0x57] = 1; weak_mods = 0x42; unsigned reports = keyboard_reports; turn.event.pressed = true;
    check(!process_record_user(0x7E42,&turn) && registered[0x57] && weak_mods == 0x42 && keyboard_reports == reports,
          "zoom never releases an already held keypad key or its modifiers");
    registered[0x57] = 0; weak_mods = 0;
    config[17] = 0; config[31] = 0; config[IG_SIZE + 7] = 5; knob_test_install(config);
    for (uint8_t step = 1; step <= 5; ++step) {
        config[17] = 1; config[IG_SIZE + 7] = step; knob_test_install(config);
        knob_modes[0] = 2; brightness = 0; turn.event.pressed = true;
        check(!process_record_user(0x7849,&turn) && brightness == 17 * step, "brightness step applies exactly one selected encoder multiplier");
        keyrecord_t key = {.event = {.pressed = true, .type = 1, .key = {0,0}}};
        check(!process_record_user(0x7849,&key) && brightness == 17 * (step + 1), "same brightness on ordinary key is never multiplied");
        knob_modes[0] = 1; unsigned taps = knob_taps;
        check(process_record_user(0xA9,&turn) && knob_taps == taps + step - 1, "volume multiplier adds bounded taps before original QMK event");
        knob_modes[0] = 4; taps = knob_taps;
        check(process_record_user(0xDA,&turn) && knob_taps == taps + step - 1, "vertical wheel clockwise goes down with bounded native mouse taps");
        knob_modes[0] = 6; taps = knob_taps;
        check(process_record_user(0xDC,&turn) && knob_taps == taps + step - 1, "horizontal wheel clockwise sends right with bounded multiplier");
        knob_modes[0] = 5; reports = keyboard_reports;
        check(!process_record_user(0x7E42,&turn) && keyboard_reports == reports + step, "zoom shortcut uses selected multiplier");
        knob_modes[0] = 7; reports = keyboard_reports;
        check(!process_record_user(0x7E47,&turn) && keyboard_reports == reports + 1, "tabs always move once regardless of multiplier");
        knob_modes[0] = 8; reports = keyboard_reports;
        check(!process_record_user(0x7E48,&turn) && keyboard_reports == reports + 1, "undo always runs once regardless of multiplier");
        knob_modes[0] = 0; taps = knob_taps;
        check(process_record_user(0xA9,&turn) && knob_taps == taps, "unrecognized custom pair never receives extra volume taps");
    }
    for (uint8_t layout = 0; layout < 3; ++layout) {
        config[IG_SIZE + 7] = 1 | (layout << 3); knob_test_install(config);
        check(!process_record_user(0x7E48,&turn) && last_registered_code == (layout == 1 ? 0x1C : layout == 2 ? 0x1A : 0x1D),
              "undo host layout maps Z for QWERTY QWERTZ and AZERTY");
    }
    config[IG_SIZE + 7] = 1;
    memcpy(bad,config,sizeof(bad));
    config[17] = config[31] = 0;
    for (uint8_t layer = 1; layer < 4; ++layer) {
        put16(config + IG_ENCODER_OFFSET + layer * 4, 1); put16(config + IG_ENCODER_OFFSET + layer * 4 + 2, 1);
    }
    config[IG_SIZE + 3 * 8 + 7] = 5;
    put16(config + IG_ENCODER_OFFSET + 4, 4); put16(config + IG_ENCODER_OFFSET + 6, 5);
    const uint16_t inherited[][2] = {{0xAA,0xA9},{0x784A,0x7849},{0xD9,0xDA},{0xDB,0xDC},{0x7E43,0x7E42},{0x7E45,0x7E44}};
    for (unsigned i = 0; i < sizeof(inherited)/sizeof(inherited[0]); ++i) {
        put16(config + IG_ENCODER_OFFSET,inherited[i][0]); put16(config + IG_ENCODER_OFFSET + 2,inherited[i][1]);
        knob_test_install(config); layer_state = 8;
        check(knob_encoder_steps(&turn) == 5 && knob_effective_direction(false) == inherited[i][0] && knob_effective_direction(true) == inherited[i][1],
              "inherited preset gets active top layer step and ignores inactive intermediate bindings");
        layer_state = 10;
        check(knob_encoder_steps(&turn) == 1, "active custom intermediate pair blocks inherited preset scaling");
        layer_state = 8; default_layer_state = 2;
        check(knob_encoder_steps(&turn) == 1, "default layer stack participates in inherited pair resolution");
        default_layer_state = 0; layer_state = 0;
    }
    put16(config + IG_ENCODER_OFFSET,0x7E48); put16(config + IG_ENCODER_OFFSET + 2,0x7E49);
    config[IG_SIZE + 7] = 9; config[IG_SIZE + 8 + 7] = 1;
    put16(config + IG_ENCODER_OFFSET + 4,1); put16(config + IG_ENCODER_OFFSET + 6,1);
    put16(config + IG_KEYS_OFFSET,0x7E48); put16(config + IG_KEYS_OFFSET + MATRIX_ROWS * MATRIX_COLS * 2,1);
    put16(config + knob_test_offset(0),0x7E48); put16(config + knob_test_offset(1),1);
    config[17] = 2; knob_test_install(config); layer_state = 2;
    check(!process_record_user(0x7E49,&turn) && last_registered_code == 0x1C, "inherited encoder undo uses source QWERTZ layout rather than active layer QWERTY");
    keyrecord_t inherited_key = {.event = {.pressed = true, .type = 1, .key = {0,0}}};
    check(!process_record_user(0x7E48,&inherited_key) && last_registered_code == 0x1C, "physical inherited undo also follows source layer layout");
    check(!knob_test_event(true,0x7E48), "inherited knob short action captures source layer"); KNOB_TEST_NOW += 100;
    check(!knob_test_event(false,0x7E48) && last_registered_code == 0x1C, "deferred inherited short action retains captured source layout");
    knob_modes[1] = 8;
    check(!process_record_user(0x7E49,&turn) && last_registered_code == 0x1D, "current layer cycling undo uses its own layout");
    layer_state = default_layer_state = 0; memcpy(config,bad,sizeof(config));
    config[17] = 1; config[31] = 0;
    put16(config + knob_test_offset(0), 0x784A); knob_test_install(config); brightness = 100;
    check(!knob_test_event(true,0x784A), "brightness short action defers"); KNOB_TEST_NOW += 100;
    check(!knob_test_event(false,0x784A) && brightness == 83, "short brightness press uses shared volatile dimmer once");
    knob_test_hold(0x784A); check(brightness == 83, "long brightness press changes mode without dimming");
    put16(config + knob_test_offset(0), 0x7E44); knob_test_install(config);
    check(!knob_test_event(true,0x7E44), "zoom short action defers"); KNOB_TEST_NOW += 100;
    check(!knob_test_event(false,0x7E44) && last_registered_code == 0x57 && last_registered_mods == 8 && !weak_mods,
          "short Mac zoom uses same balanced shortcut as encoder direction");
    put16(config + knob_test_offset(0), 0x7700); put16(config + 14, 2);
    memset(config + IG_MACRO_OFFSET, 0, IG_MACROS * 4); put16(config + IG_MACRO_OFFSET + 2, 2);
    memset(config + IG_EVENT_OFFSET, 0, IG_SEQUENCE_EVENTS * 4);
    config[IG_EVENT_OFFSET] = 1; put16(config + IG_EVENT_OFFSET + 2, 4);
    config[IG_EVENT_OFFSET + 4] = 2; put16(config + IG_EVENT_OFFSET + 6, 4);
    knob_test_install(config);
    check(!knob_test_event(true,0x7700), "macro short action defers"); KNOB_TEST_NOW += 100;
    check(!knob_test_event(false,0x7700) && macro_running, "macro starts once on short release");
    macro_task(); check(registered[4], "knob macro uses normal key-down runner"); macro_task(); macro_task();
    check(!registered[4] && !macro_running, "knob macro releases keys through existing runner");
    knob_test_hold(0x7700); check(!macro_running && !registered[4], "long macro press never runs short macro");
    put16(config + knob_test_offset(0), 0xA8);
#if IG_BOARD == 16
    config[17] = 1; config[31] = 0; knob_test_install(config); uint8_t action = 1;
    check(command(14,&action,1) == 0, "physical diagnostic session starts before knob check");
    check(knob_test_event(true,0xA8), "diagnostic knob press is not deferred");
    KNOB_TEST_NOW += 700; knob_task();
    check(knob_test_event(false,0xA8) && !knob_modes[0] && !knob_pending && diagnostic_active,
          "diagnostics preserve direct knob input without changing mode");
    action = 2; check(command(14,&action,1) == 0, "diagnostic session ends");
#endif
    // Turning the optional setting off keeps ordinary mappings and schema4.
    config[17] = config[31] = 0; put16(config + 4, 4);
    for (uint8_t layer = 0; layer < 4; ++layer) config[IG_SIZE + layer * 8 + 6] = config[IG_SIZE + layer * 8 + 7] = 0;
    knob_test_install(config);
    check(knob_test_event(true,0xA8) && knob_test_event(false,0xA8), "schema4 profiles retain immediate QMK press behavior");
#endif
}
static void knob_order_checks(const uint8_t *base) {
    uint8_t config[IG_ORDER_SIZE];
    memcpy(config,base,IG_MAX_SIZE); memset(config+IG_ORDER_OFFSET,0,8); put16(config+4,6);
    config[17]=1; config[31]=0; put16(config+IG_ORDER_OFFSET,40319);
    for(uint8_t layer=0;layer<4;++layer) {config[IG_SIZE+layer*8+6]=31;config[IG_SIZE+layer*8+7]=1;}
#if !IG_HAS_ENCODER
    check(!config_length_allowed(IG_ORDER_SIZE) && !validate_config(config,IG_ORDER_SIZE),"encoderless board refuses schema6 and its extra payload length");
#else
    for(uint8_t layer=0;layer<4;++layer) {
        put16(config+knob_test_offset(layer),0xA8);
        put16(config+IG_ENCODER_OFFSET+layer*4,0xAA); put16(config+IG_ENCODER_OFFSET+layer*4+2,0xA9);
    }
    check(validate_config(config,IG_ORDER_SIZE),"schema6 appends ordered knob settings without shifting old fields");
    check(!validate_config(config,IG_MAX_SIZE) && !validate_config(config,IG_ORDER_SIZE-1),"schema6 requires its exact appended length");
    put16(config+4,5);check(!validate_config(config,IG_ORDER_SIZE) && validate_config(config,IG_MAX_SIZE),"schema5 retains its exact former size");put16(config+4,6);
    put16(config+IG_ORDER_OFFSET,0);check(!validate_config(config,IG_ORDER_SIZE),"default order uses previous schemas instead of redundant schema6");
    for(uint8_t layer=0;layer<4;++layer) {
        put16(config+IG_ORDER_OFFSET+layer*2,1);check(validate_config(config,IG_ORDER_SIZE),"custom order on any layer is preserved even if cycling is disabled");
        put16(config+IG_ORDER_OFFSET+layer*2,40320);check(!validate_config(config,IG_ORDER_SIZE),"first rank beyond 8 factorial is rejected on every layer");
        put16(config+IG_ORDER_OFFSET+layer*2,65535);check(!validate_config(config,IG_ORDER_SIZE),"maximum unsigned rank is rejected on every layer");
        put16(config+IG_ORDER_OFFSET+layer*2,0);
    }
    uint8_t order[8];
    for(uint16_t rank=0;rank<40320;++rank) {
        check(knob_decode_order(rank,order),"all 40320 permutation ranks decode");
        unsigned reconstructed=0,seen=0;
        for(uint8_t at=0;at<8;++at) {
            check(order[at]>=1 && order[at]<=8 && !(seen & (1U<<(order[at]-1))),"every decoded order is a complete duplicate-free permutation");
            seen|=1U<<(order[at]-1);
            unsigned inversions=0;for(uint8_t later=at+1;later<8;++later)if(order[later]<order[at])++inversions;
            reconstructed=reconstructed*(8-at)+inversions;
        }
        check(reconstructed==rank && seen==255,"independent inversion ranking reconstructs each decoded permutation");
    }
    check(!knob_decode_order(40320,order) && !knob_decode_order(65535,order),"decoder rejects invalid ranks before indexing");
    const uint16_t representative[]={0,1,19873,40319};
    for(unsigned r=0;r<4;++r) {
        check(knob_decode_order(representative[r],order),"representative order decodes");
        for(unsigned mask=1;mask<=255;++mask) {
            uint8_t current=0;
            for(uint8_t at=0;at<8;++at)if(mask&(1U<<(order[at]-1))) {
                current=knob_next_mode(order,mask,0,current);check(current==order[at],"every selected-mask subset follows full order from custom slot");
            }
            check(knob_next_mode(order,mask,0,current)==0,"custom pair occurs exactly once after every selected ordered mode");
        }
    }
    put16(config+IG_ORDER_OFFSET,40319);config[IG_SIZE+6]=255;
    config[24]=4;config[25]=9;config[26]=1;put16(config+27,10);config[29]=1;config[30]=17;
    knob_test_install_length(config,IG_ORDER_SIZE);
    check(active_size==IG_ORDER_SIZE && saved_interval()==4 && saved_debounce()==9,"schema6 retains existing polling and debounce settings");
    brightness=200;check(lighting_scale(KNOB_TEST_NOW+10001)==34,"schema6 retains standby dimming");lighting_activity();
    unsigned prior_writes=writes;uint32_t prior_crc=active_crc,prior_generation=active_generation;
    const uint8_t reverse_modes[]={8,7,6,5,4,3,2,0};
    for(unsigned i=0;i<8;++i){knob_test_hold(0xA8);check(knob_modes[0]==reverse_modes[i],"actual long press follows stored reverse order and returns to saved volume");}
    check(writes==prior_writes && active_crc==prior_crc && active_generation==prior_generation,"ordered cycles never rewrite flash or saved profile");
    knob_test_hold(0xA8);knob_test_reboot();
    check(!knob_modes[0] && active_size==IG_ORDER_SIZE,"reboot resets volatile mode while retaining schema6");
    for(uint16_t at=0;at<IG_ORDER_SIZE;++at)check(active_byte(at)==config[at],"all existing settings and appended ranks survive reboot byte-exactly");
    prior_writes=writes;prior_crc=active_crc;prior_generation=active_generation;
    put16(config+IG_ORDER_OFFSET,1);knob_test_stage_length(config,IG_ORDER_SIZE);
    check(writes==prior_writes && active_crc==prior_crc && active_generation==prior_generation && active_u16(IG_ORDER_OFFSET)==40319,"schema6 staging leaves active order and flash unchanged");
    check(command(6,NULL,0)==0 && writes==prior_writes && active_u16(IG_ORDER_OFFSET)==40319,"aborting schema6 upload preserves saved ordered configuration");
    put16(config+IG_ORDER_OFFSET,40320);knob_test_stage_length(config,IG_ORDER_SIZE);
    check(knob_test_commit_length(config,IG_ORDER_SIZE)==6 && writes==prior_writes && active_crc==prior_crc,"invalid appended order is refused before any persistent write");
    check(command(6,NULL,0)==0 && active_u16(IG_ORDER_OFFSET)==40319,"invalid order abort keeps known good order");
    put16(config+IG_ORDER_OFFSET,40319);
    config[IG_SIZE+6]=1|16|128;knob_test_install_length(config,IG_ORDER_SIZE);
    const uint8_t sparse_modes[]={8,5,0};
    for(unsigned i=0;i<3;++i){knob_test_hold(0xA8);check(knob_modes[0]==sparse_modes[i],"actual sparse selected subset preserves sorted order");}
    put16(config+IG_ENCODER_OFFSET,0x50);put16(config+IG_ENCODER_OFFSET+2,0x4F);knob_test_install_length(config,IG_ORDER_SIZE);
    const uint8_t custom_modes[]={8,5,1,0};
    for(unsigned i=0;i<4;++i){knob_test_hold(0xA8);check(knob_modes[0]==custom_modes[i],"custom pair is retained once around sorted cycle");}
    config[17]=3;put16(config+IG_ORDER_OFFSET+2,1);config[IG_SIZE+8+6]=255;knob_test_install_length(config,IG_ORDER_SIZE);
    layer_state=2;knob_test_hold(0xA8);check(knob_modes[1]==2 && !knob_modes[0],"active layer uses its own independent permutation");layer_state=0;
    config[17]=0;knob_test_install_length(config,IG_ORDER_SIZE);
    check(knob_test_event(true,0xA8) && knob_test_event(false,0xA8) && !knob_pending,"disabled cycling keeps direct press while custom order stays saved");
    put16(config+4,4);for(uint8_t layer=0;layer<4;++layer)config[IG_SIZE+layer*8+6]=config[IG_SIZE+layer*8+7]=0;
    knob_test_install(config);knob_test_reboot();check(active_size==IG_MAX_SIZE && active_u16(4)==4,"older schema4 profile still commits and boots after schema6");
#endif
}
static void knob_fixture_checks(const char *directory, bool ordered) {
#if IG_HAS_ENCODER
    static const char *device_ids[] = {"", "pro-iso", "numpad", "pro-ansi", "pro-rev2-ansi", "pro-rev2-iso",
        "gmmk2-65-ansi", "gmmk2-65-iso", "gmmk2-96-ansi", "gmmk2-96-iso", "gmmk3-65-ansi", "gmmk3-65-iso",
        "gmmk3-75-ansi", "gmmk3-75-iso", "gmmk3-100-ansi", "gmmk3-100-iso", "gmmk3-pro-100-iso"};
    static const char *preset_ids[] = {"volume","brightness","tracks","scroll","zoom","zoom-mac","hscroll","tabs","undo","undo-mac"};
    static const uint16_t pairs[][2] = {{0xAA,0xA9},{0x784A,0x7849},{0xAC,0xAB},{0xD9,0xDA},{0x7E43,0x7E42},
        {0x7E45,0x7E44},{0xDB,0xDC},{0x7E46,0x7E47},{0x7E48,0x7E49},{0x7E4A,0x7E4B}};
    static const uint8_t bits[] = {1,2,4,8,16,16,32,64,128,128};
    for (unsigned preset = 0; preset < 10; ++preset) {
        char path[1024]; uint8_t config[IG_ORDER_SIZE + 1];
        uint16_t length=ordered?IG_ORDER_SIZE:IG_MAX_SIZE;
        snprintf(path,sizeof(path),"%s/%s-%s.bin",directory,device_ids[IG_BOARD],preset_ids[preset]);
        FILE *file = fopen(path,"rb"); check(file != NULL,"C# knob fixture exists");
        size_t bytes = fread(config,1,sizeof(config),file); fclose(file);
        check(bytes == length && u16(config + 4) == (ordered?6:5) && validate_config(config,length), "actual C# knob payload passes firmware semantic checks");
        check(config[17] == 7 && config[31] == ((preset == 5 || preset == 9) ? 7 : 0), "C# fixture cycle and Mac masks agree with independent contract");
        const uint8_t masks[] = {31,255,bits[preset],224}, packed[] = {1,10,19,5};
        for (uint8_t layer = 0; layer < 4; ++layer) {
            check(config[IG_SIZE + layer * 8 + 6] == masks[layer] && config[IG_SIZE + layer * 8 + 7] == packed[layer], "C# per-layer mask step and host layout pack exactly");
            check(u16(config + IG_ENCODER_OFFSET + layer * 4) == pairs[preset][0] && u16(config + IG_ENCODER_OFFSET + layer * 4 + 2) == pairs[preset][1], "C# preset directions agree with independent firmware wire expectations");
            if(ordered){const uint16_t ranks[]={40319,1,19873,0};check(u16(config+IG_ORDER_OFFSET+layer*2)==ranks[layer],"C# appended permutation ranks match independent wire expectations");}
        }
        knob_test_install_length(config,length);
        for (unsigned pass = 0; pass < 2; ++pass) {
            if (pass) knob_test_reboot();
            for (uint16_t offset = 0; offset < length; ++offset) check(active_byte(offset) == config[offset], "C# knob fixture commits and survives restart byte-exactly");
            for (uint8_t layer = 0; layer < 4; ++layer) check(keycode_at_encodermap_location(layer,0,false) == pairs[preset][0] && keycode_at_encodermap_location(layer,0,true) == pairs[preset][1], "C# preset produces intended left and right functions after upload and restart");
        }
    }
#else
    (void)directory; (void)ordered;
#endif
}
static void knob_status_checks(const uint8_t *base) {
#if !IG_HAS_ENCODER
    (void)base;
    check(command(16,NULL,0)==9,"encoderless board does not advertise or invent knob state");
#else
    uint8_t config[IG_MAX_SIZE]; memcpy(config,base,sizeof(config)); put16(config+4,5);
    config[17]=1;config[31]=0;
    for(uint8_t layer=0;layer<4;++layer) {
        config[IG_SIZE+layer*8+6]=255;config[IG_SIZE+layer*8+7]=1;
        put16(config+knob_test_offset(layer),0xA8);
        put16(config+IG_ENCODER_OFFSET+layer*4,KC_VOLD);put16(config+IG_ENCODER_OFFSET+layer*4+2,KC_VOLU);
    }
    knob_test_install(config);knob_test_reboot();
    check(command(16,NULL,0)==0 && response[6]==24 && response[8]==1 && response[9]==0 && response[10]==1 && response[11]==1,
          "initial status reports actual volume and enabled cycling without a change signal");
    check(u16(response+12)==KC_VOLD && u16(response+14)==KC_VOLU && u32(response+16)==0 && u32(response+20)==active_generation && u32(response+24)==active_crc && !response[28] && !response[29] && response[30]==1 && !response[31],
          "status binds resolved codes and source layers to saved generation and CRC");
    unsigned initial_writes=writes;uint32_t crc=active_crc,generation=active_generation,activity=lighting_last_activity;
    for(uint8_t i=0;i<10;++i) check(command(16,NULL,0)==0 && !u32(response+16),"polling does not create changes");
    check(writes==initial_writes && active_crc==crc && active_generation==generation && lighting_last_activity==activity,"status does not write or wake the keyboard");
    uint8_t extra[24]={0};for(uint8_t count=1;count<=24;++count) check(command(16,extra,count)==1,"status rejects nonempty payloads");
    knob_test_event(true,0xA8);KNOB_TEST_NOW+=699;knob_test_event(false,0xA8);
    check(command(16,NULL,0)==0 && !u32(response+16) && !knob_signal_active,"short press does not report a mode switch");
    knob_test_hold(0xA8);
    check(command(16,NULL,0)==0 && response[10]==2 && response[11]==3 && u32(response+16)==1,"actual long-press switch has one counter increment and signal");
    check(knob_signal_remaining(knob_signal_started-1)==0 && knob_signal_active,"older partial RGB frame waits without cancelling a new signal");
    KNOB_TEST_NOW+=100;rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;++led) check(KNOB_TEST_PIXELS[led].r==255 && KNOB_TEST_PIXELS[led].g==ig_srgb_to_pwm(208) && KNOB_TEST_PIXELS[led].b==0,"brightness preset produces exact bounded yellow feedback on all LEDs");
    brightness=0;rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;++led) check(!KNOB_TEST_PIXELS[led].r && !KNOB_TEST_PIXELS[led].g && !KNOB_TEST_PIXELS[led].b,"signal respects a dark runtime dimmer");
    brightness=255;KNOB_TEST_NOW+=500;rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    ig_rgb_t restored[RGB_MATRIX_LED_COUNT];memcpy(restored,KNOB_TEST_PIXELS,sizeof(restored));
    check(!knob_signal_active && command(16,NULL,0)==0 && response[11]==1 && u32(response+16)==1,"signal expires at600ms without changing active function");
    knob_reset();rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!memcmp(restored,KNOB_TEST_PIXELS,sizeof(restored)) && writes==initial_writes && active_crc==crc && active_generation==generation,"feedback restores normal frame without changing stored colors");
    knob_test_hold(0xA8);stage_open=true;check(!knob_signal_remaining(KNOB_TEST_NOW),"upload cancels signal");stage_open=false;
    knob_test_hold(0xA8);light_test_active=true;check(!knob_signal_remaining(KNOB_TEST_NOW) && light_test_active,"manual lighting test has priority without being stopped by status");light_test_active=false;
    knob_test_hold(0xA8);suspend_power_down_user();suspend_wakeup_init_user();check(!knob_signal_active,"suspend discards signal permanently");
    knob_test_hold(0xA8);layer_state=2;check(!knob_signal_remaining(KNOB_TEST_NOW),"layer change discards obsolete signal");layer_state=0;
#if IG_BOARD==16
    knob_test_hold(0xA8);diagnostic_active=true;diagnostic_refreshed=KNOB_TEST_NOW;check(!knob_signal_remaining(KNOB_TEST_NOW) && diagnostic_active,"device diagnostics have priority");diagnostic_stop();
#endif
    knob_test_install(config);knob_test_reboot();config[IG_SIZE+6]=1;knob_test_install(config);knob_test_hold(0xA8);
    check(command(16,NULL,0)==0 && !u32(response+16) && !knob_signal_active,"single unchanged preset creates no misleading switch event");
    config[17]=0;config[IG_SIZE+6]=255;config[IG_SIZE+3*8+7]=4;
    put16(config+IG_ENCODER_OFFSET,IG_ZOOM_OUT_MAC);put16(config+IG_ENCODER_OFFSET+2,IG_ZOOM_IN_MAC);
    for(uint8_t layer=1;layer<4;++layer){put16(config+IG_ENCODER_OFFSET+layer*4,KC_TRNS);put16(config+IG_ENCODER_OFFSET+layer*4+2,KC_TRNS);}
    knob_test_install(config);layer_state=8;
    check(command(16,NULL,0)==0 && response[9]==3 && response[10]==16 && !response[11] && u16(response+12)==IG_ZOOM_OUT_MAC && u16(response+14)==IG_ZOOM_IN_MAC && !response[28] && !response[29] && response[30]==4,
          "transparent active layer reports inherited actual macOS pair and active layer step");
    layer_state=0;put16(config+IG_ENCODER_OFFSET+2,KC_VOLU);knob_test_install(config);
    check(command(16,NULL,0)==0 && !response[10] && response[30]==1,"mixed manual directions are not falsely recognized as preset");
    knob_test_reboot();check(command(16,NULL,0)==0 && !u32(response+16),"boot begins a new switch counter baseline");
#endif
}
static void knob_press_checks(const uint8_t *base) {
    uint8_t config[IG_PRESS_SIZE];memset(config,0,sizeof(config));memcpy(config,base,IG_MAX_SIZE);put16(config+4,7);
    config[17]=0x11;config[31]=0;
    for(uint16_t at=IG_PRESS_OFFSET;at<IG_PRESS_SIZE;at+=2) put16(config+at,KC_TRNS);
    for(uint8_t layer=0;layer<4;++layer){config[IG_SIZE+layer*8+6]=255;config[IG_SIZE+layer*8+7]=1;}
#if !IG_HAS_ENCODER
    check(!validate_config(config,IG_PRESS_SIZE),"encoderless board rejects function-specific press configuration");
#else
    for(uint8_t layer=0;layer<4;++layer){put16(config+knob_test_offset(layer),0xA8);put16(config+IG_ENCODER_OFFSET+layer*4,KC_VOLD);put16(config+IG_ENCODER_OFFSET+layer*4+2,KC_VOLU);}
    check(validate_config(config,IG_PRESS_SIZE),"per-function mode may initially inherit every normal press");
    config[17]=1;check(!validate_config(config,IG_PRESS_SIZE),"redundant schema7 with no map or enabled per-function layer is rejected");
    config[17]=0x11;put16(config+IG_PRESS_OFFSET,5);put16(config+IG_PRESS_OFFSET+2,6);
    check(!u16(config+IG_MACRO_OFFSET+31*4+2),"negative press macro fixture uses an empty slot");
    static const uint16_t unsafe[]={0xE0,0xE7,0x0104,0x5221,0x5261,0x5201,0xFFFF,0x771F};
    for(unsigned i=0;i<sizeof(unsafe)/sizeof(unsafe[0]);++i){put16(config+IG_PRESS_OFFSET,unsafe[i]);check(!validate_config(config,IG_PRESS_SIZE),"press override rejects holds chords invalid codes and absent macros");}
    put16(config+IG_PRESS_OFFSET,5);config[31]=0x10;check(!validate_config(config,IG_PRESS_SIZE),"per-function flag does not authorize a Mac cycle bit");config[31]=0;
    check(!validate_config(config,IG_ORDER_SIZE),"schema7 requires the complete press table");
    knob_test_install_length(config,IG_PRESS_SIZE);knob_test_reboot();
    unsigned prior_writes=writes;uint32_t prior_crc=active_crc,prior_generation=active_generation;
    unsigned taps_before=knob_taps;
    knob_test_event(true,0xA8);KNOB_TEST_NOW+=60;knob_test_event(false,0xA8);
    check(knob_taps==taps_before+1 && knob_tapped_code==5,"volume uses its own short press");
    taps_before=knob_taps;knob_test_hold(0xA8);check(knob_taps==taps_before && knob_modes[0]==2,"long hold switches without triggering either short action");
    knob_test_event(true,0xA8);KNOB_TEST_NOW+=60;knob_test_event(false,0xA8);
    check(knob_taps==taps_before+1 && knob_tapped_code==6,"new active brightness uses its own short press");
    config[17]=0x10;knob_test_install_length(config,IG_PRESS_SIZE);taps_before=knob_taps;
    check(!knob_test_event(true,0xA8),"explicit per-function press works when long-press cycling is disabled");
    knob_modes[0]=2;layer_state=2;KNOB_TEST_NOW+=900;knob_task();knob_test_event(false,0xA8);layer_state=0;
    check(knob_taps==taps_before+1 && knob_tapped_code==5,"release uses the function and layer captured at physical key-down");
    config[17]=0;knob_test_install_length(config,IG_PRESS_SIZE);taps_before=knob_taps;
    check(knob_test_event(true,0xA8) && knob_test_event(false,0xA8) && knob_taps==taps_before,"disabled per-function map preserves ordinary QMK action while keeping stored map");
    config[17]=0x10;put16(config+IG_PRESS_OFFSET,KC_TRNS);put16(config+knob_test_offset(0),0x5221);knob_test_install_length(config,IG_PRESS_SIZE);
    check(knob_test_event(true,0x5221) && knob_test_event(false,0x5221),"inherited general Fn remains a real held action when cycling is disabled");
    put16(config+knob_test_offset(0),0xE0);knob_test_install_length(config,IG_PRESS_SIZE);
    check(knob_test_event(true,0xE0) && knob_test_event(false,0xE0),"inherited general modifier keeps native press and release");
    put16(config+IG_PRESS_OFFSET,5);knob_test_install_length(config,IG_PRESS_SIZE);taps_before=knob_taps;
    check(!knob_test_event(true,0xE0) && !knob_test_event(false,0xE0) && knob_tapped_code==5 && knob_taps==taps_before+1,"explicit safe override replaces the general hold only for this function");
    config[17]|=1;check(!validate_config(config,IG_PRESS_SIZE),"cycling still rejects unsafe general fallback even with a safe override");config[17]=0x10;
    put16(config+knob_test_offset(0),0xA8);put16(config+IG_PRESS_OFFSET,0);knob_test_install_length(config,IG_PRESS_SIZE);taps_before=knob_taps;
    knob_test_event(true,0xA8);knob_test_event(false,0xA8);check(knob_taps==taps_before,"explicit disabled short action produces no key");
    config[17]=0x80;put16(config+IG_PRESS_OFFSET+3*16+4*2,5);
    put16(config+IG_ENCODER_OFFSET,IG_ZOOM_OUT_MAC);put16(config+IG_ENCODER_OFFSET+2,IG_ZOOM_IN_MAC);
    for(uint8_t layer=1;layer<4;++layer){put16(config+IG_ENCODER_OFFSET+layer*4,KC_TRNS);put16(config+IG_ENCODER_OFFSET+layer*4+2,KC_TRNS);}
    knob_test_install_length(config,IG_PRESS_SIZE);layer_state=8;taps_before=knob_taps;
    knob_test_event(true,0xA8);knob_test_event(false,0xA8);
    check(knob_taps==taps_before+1 && knob_tapped_code==5,"transparent inherited Mac zoom uses active layer's common Zoom press slot");
    knob_test_event(true,0xA8);suspend_power_down_user();suspend_wakeup_init_user();taps_before=knob_taps;knob_test_event(false,0xA8);
    check(knob_taps==taps_before,"suspend discards an unfinished short tap");layer_state=0;
    prior_writes=writes;prior_crc=active_crc;prior_generation=active_generation;knob_test_reboot();
    for(uint16_t at=0;at<IG_PRESS_SIZE;++at)check(active_byte(at)==config[at],"per-function map and flags persist byte-exact across reboot");
    check(writes==prior_writes && active_crc==prior_crc && active_generation==prior_generation,"reboot and press state do not write the saved map");
#endif
}
static void knob_press_fixture_checks(const char *directory) {
#if IG_HAS_ENCODER
    static const char *ids[]={"","pro-iso","numpad","pro-ansi","pro-rev2-ansi","pro-rev2-iso","gmmk2-65-ansi","gmmk2-65-iso","gmmk2-96-ansi","gmmk2-96-iso","gmmk3-65-ansi","gmmk3-65-iso","gmmk3-75-ansi","gmmk3-75-iso","gmmk3-100-ansi","gmmk3-100-iso","gmmk3-pro-100-iso"};
    char path[1024];uint8_t config[IG_PRESS_SIZE+1];snprintf(path,sizeof(path),"%s/%s-knob-press.bin",directory,ids[IG_BOARD]);
    FILE *file=fopen(path,"rb");check(file!=NULL,"C# schema7 fixture exists");size_t bytes=fread(config,1,sizeof(config),file);fclose(file);
    check(bytes==IG_PRESS_SIZE && u16(config+4)==7 && config[17]==0x53 && !config[31] && validate_config(config,IG_PRESS_SIZE),"actual C# schema7 passes firmware validation with exact flags and length");
    const uint16_t ranks[]={40319,1,19873,0},codes[]={0xA8,0,0xAE,1,0x7E44,0xDB,0x7E47,0x7E48};
    for(uint8_t layer=0;layer<4;++layer){
        check(u16(config+IG_ORDER_OFFSET+layer*2)==ranks[layer],"schema7 preserves separate order trailer");
        for(uint8_t mode=0;mode<8;++mode)check(u16(config+IG_PRESS_OFFSET+layer*16+mode*2)==codes[mode],"C# function press codes match independent wire contract");
    }
    knob_test_install_length(config,IG_PRESS_SIZE);knob_test_reboot();
    for(uint16_t at=0;at<IG_PRESS_SIZE;++at)check(active_byte(at)==config[at],"schema7 real C# profile persists byte-exact on every encoder board");
    for(uint8_t mode=1;mode<=8;++mode){
        knob_modes[0]=mode;unsigned before=knob_taps;uint16_t expected=codes[mode-1];
        bool down=knob_test_event(true,0xA8);KNOB_TEST_NOW+=30;bool up=knob_test_event(false,0xA8);
        check(!down && !up,"schema7 cycling short press remains deferred");
        if(mode==1||mode==3||mode==4||mode==6)check(knob_taps==before+1 && knob_tapped_code==(expected==1?0xA8:expected),"C# press slot taps configured action or inherited global action");
        if(mode==2)check(knob_taps==before,"C# disabled brightness press emits no key");
    }
#else
    (void)directory;
#endif
}
