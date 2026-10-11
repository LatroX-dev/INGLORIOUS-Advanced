// Offline fault-injection harness for the actual firmware module, not a reimplementation.
// No Windows/USB/flash APIs. The memory model deliberately has separate cache and backing.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#define QMK_KEYBOARD_H "qmk_mock.h"
#include "../inglorious/inglorious.c"

static uint8_t mock_cache[16384], mock_backing[16384], response[32];
static matrix_row_t pressed_rows[MATRIX_ROWS];
static uint8_t registered[256];
static uint32_t mock_now;
static unsigned checks, replies, writes, fail_write, reloads;
static bool fail_after_persist, fail_reload, erase_on_failure;
static bool mock_borrowed;
static uint32_t mock_stage_address;
static size_t mock_stage_length;
static uint8_t mock_stage_phase;
static unsigned scans, release_at_scan, boot_jumps, waited_ms, volume_taps, midi_messages;
static bool external_cs_high, external_cs_output;
static int16_t analog_sample;
static ig_rgb_t rendered[RGB_MATRIX_LED_COUNT];
static uint8_t mock_active_interval=1, mock_saved_interval=1, mock_debounce=5;
static bool mock_reconnect_pending;
static uint32_t mock_reconnect_at;
static unsigned mock_reconnects;
led_config_t g_led_config;
int midi_device;
uint32_t layer_state, default_layer_state;
static void check(bool condition, const char *name) { checks++; if (!condition) { fprintf(stderr, "FAIL check %u: %s\n", checks, name); exit(1); } }
// Independent floating-point oracle; production is a constant integer table.
static uint8_t reference_pwm(uint8_t value) {
    double encoded=(double)value/255.0;
    double linear=encoded<=0.04045?encoded/12.92:pow((encoded+0.055)/1.055,2.4);
    return (uint8_t)floor(linear*255.0+0.5);
}
static ig_rgb_t reference_output(ig_rgb_t color) {
    return (ig_rgb_t){reference_pwm(color.r),reference_pwm(color.g),reference_pwm(color.b)};
}

uint16_t keycode_at_keymap_location_raw(uint8_t layer, uint8_t row, uint8_t col) { (void)layer; (void)row; (void)col; return 4; }
uint8_t eeprom_read_byte(const uint8_t *address) { uintptr_t at = (uintptr_t)address; if (at >= sizeof(mock_cache)) abort(); return mock_cache[at]; }
void eeprom_read_block(void *out, const void *address, size_t length) { uintptr_t at = (uintptr_t)address; if (at + length > sizeof(mock_cache)) abort(); memcpy(out, mock_cache + at, length); }
static wear_leveling_status_t mock_write(uint32_t address, const void *data, size_t length) {
    if (address + length > sizeof(mock_cache)) abort();
    writes++; if(data != mock_cache + address) memcpy(mock_cache + address, data, length); // QMK does this BEFORE physical I/O.
    if (writes == fail_write) {
        if (erase_on_failure) memset(mock_backing, 0, sizeof(mock_backing));
        else if (fail_after_persist) memcpy(mock_backing + address, data, length);
        return WEAR_LEVELING_FAILED;
    }
    memcpy(mock_backing + address, data, length); return WEAR_LEVELING_SUCCESS;
}
wear_leveling_status_t wear_leveling_write(uint32_t address, const void *data, size_t length) { return mock_borrowed ? WEAR_LEVELING_FAILED : mock_write(address,data,length); }
wear_leveling_status_t wear_leveling_init(void) { if(mock_borrowed) return WEAR_LEVELING_FAILED; reloads++; if (fail_reload) { memset(mock_cache, 0, sizeof(mock_cache)); return WEAR_LEVELING_FAILED; } memcpy(mock_cache, mock_backing, sizeof(mock_cache)); return WEAR_LEVELING_SUCCESS; }
const uint8_t *ig_storage_view(uint32_t address,size_t length) { return address<=sizeof(mock_cache) && length<=sizeof(mock_cache)-address ? mock_cache+address : NULL; }
uint8_t *ig_storage_stage_begin(uint32_t address,size_t length) {
    if(mock_borrowed || (address!=1056 && address!=8224) || !length || length>7136) return NULL;
    mock_borrowed=true; mock_stage_address=address; mock_stage_length=length; mock_stage_phase=0;
    memset(mock_cache+address,0,length); return mock_cache+address;
}
wear_leveling_status_t ig_storage_stage_write(uint32_t address,const void *data,size_t length) {
    if(!mock_borrowed || mock_stage_phase>3) return WEAR_LEVELING_FAILED;
    bool match=mock_stage_phase==0?address==mock_stage_address-4&&length==4:
        mock_stage_phase==1?address==mock_stage_address&&length==mock_stage_length&&data==mock_cache+address:
        mock_stage_phase==2?address==mock_stage_address-32&&length==28:address==mock_stage_address-4&&length==4;
    if(!match) return WEAR_LEVELING_FAILED;
    wear_leveling_status_t status=mock_write(address,data,length);
    mock_stage_phase=status==WEAR_LEVELING_FAILED?255:mock_stage_phase+1;
    return status;
}
wear_leveling_status_t ig_storage_stage_finish(bool commit_attempted) {
    if(!mock_borrowed) return WEAR_LEVELING_FAILED;
    bool failed=mock_stage_phase==255 || (!commit_attempted && mock_stage_phase!=0);
    mock_borrowed=false;
    wear_leveling_status_t status=wear_leveling_init();
    return failed?WEAR_LEVELING_FAILED:status;
}
matrix_row_t matrix_get_row(uint8_t row) { return pressed_rows[row]; }
void raw_hid_send(uint8_t *data, uint8_t length) { if (length != 32) abort(); memcpy(response, data, 32); replies++; }
void register_code(uint8_t code) { registered[code] = 1; }
void unregister_code(uint8_t code) { registered[code] = 0; }
uint32_t timer_read32(void) { return mock_now; }
void layer_clear(void) { layer_state = 0; }
uint8_t get_highest_layer(uint32_t state) { uint8_t i = 0; while (state >>= 1) i++; return i; }
void rgb_matrix_set_color(uint8_t led, uint8_t r, uint8_t g, uint8_t b) { if (led >= RGB_MATRIX_LED_COUNT) abort(); rendered[led] = (ig_rgb_t){r,g,b}; }
void rgb_matrix_enable_noeeprom(void) {}
void rgb_matrix_mode_noeeprom(uint8_t mode) { (void)mode; }
uint32_t timer_elapsed32(uint32_t since) { return mock_now - since; }
int16_t analogReadPin(int pin) { (void)pin; return analog_sample; }
void midi_send_cc(int *device, uint8_t channel, uint8_t controller, uint8_t value) { (void)device; (void)channel; (void)controller; (void)value; midi_messages++; }
void tap_code(uint8_t code) { (void)code; volume_taps++; }
void gpio_write_pin_high(int pin) { if (pin == B6) external_cs_high = true; }
void gpio_set_pin_output(int pin) { if (pin == B6) { if (!external_cs_high) abort(); external_cs_output = true; } }
void matrix_scan(void) { scans++; if (scans == release_at_scan) memset(pressed_rows, 0, sizeof(pressed_rows)); }
void wait_ms(uint16_t milliseconds) { waited_ms += milliseconds; mock_now += milliseconds; }
void bootloader_jump(void) { boot_jumps++; }
bool ig_timing_valid(uint8_t interval,uint8_t delay) { return (interval==1 || interval==2 || interval==4 || interval==8) && delay<=40; }
void ig_timing_init(uint8_t interval,uint8_t delay) { if(!ig_timing_valid(interval,delay)) abort(); mock_active_interval=mock_saved_interval=interval; mock_debounce=delay; mock_reconnect_pending=false; }
bool ig_timing_update(uint8_t interval,uint8_t delay) { if(!ig_timing_valid(interval,delay)) return false; mock_saved_interval=interval; mock_debounce=delay; mock_reconnect_pending=false; return true; }
uint8_t ig_timing_active_interval(void) { return mock_active_interval; }
uint8_t ig_timing_saved_interval(void) { return mock_saved_interval; }
uint8_t ig_timing_debounce_ms(void) { return mock_debounce; }
bool ig_timing_reconnect_pending(void) { return mock_reconnect_pending; }
bool ig_timing_schedule_reconnect(void) {
    check(response[3]==8 && response[5]==0 && response[6]==4,"RAW8 ACK emitted before schedule hook");
    if(mock_active_interval==mock_saved_interval) return false;
    if(!mock_reconnect_pending) { mock_reconnect_pending=true; mock_reconnect_at=mock_now; }
    return true;
}
void ig_timing_task(bool safe) { if(mock_reconnect_pending && safe && mock_now-mock_reconnect_at>=100) { mock_active_interval=mock_saved_interval; mock_reconnect_pending=false; mock_reconnects++; } }

static void reset(void) {
    memset(mock_backing, 0, sizeof(mock_backing)); memset(mock_cache, 0, sizeof(mock_cache));
    memset(pressed_rows, 0, sizeof(pressed_rows)); memset(registered, 0, sizeof(registered));
    writes = fail_write = replies = reloads = 0; fail_reload = fail_after_persist = erase_on_failure = false;
    mock_borrowed=false; mock_stage_phase=0; mock_stage_address=0; mock_stage_length=0;
    stage_open = macro_running = runtime_ready = false; stage_received = 0; active_slot = -1; active_generation = 0; mock_now = 0;
    memset(held_layer_keys, 0, sizeof(held_layer_keys));
    scans = release_at_scan = boot_jumps = waited_ms = volume_taps = midi_messages = 0;
    external_cs_high = external_cs_output = false; analog_sample = 512;
    memset(&g_led_config, 0, sizeof(g_led_config)); memset(g_led_config.matrix_co, 255, sizeof(g_led_config.matrix_co));
    g_led_config.matrix_co[0][0] = 0;
    g_led_config.point[0] = (point_t){28,32};
    g_led_config.point[RGB_MATRIX_LED_COUNT - 2] = (point_t){0,32};
    g_led_config.point[RGB_MATRIX_LED_COUNT - 1] = (point_t){224,32};
    g_led_config.flags[RGB_MATRIX_LED_COUNT - 2] = g_led_config.flags[RGB_MATRIX_LED_COUNT - 1] = LED_FLAG_UNDERGLOW;
    layer_state = default_layer_state = 0; brightness = 255;
    mock_reconnects=0;
    keyboard_pre_init_user();
    keyboard_post_init_user();
}
static uint8_t command(uint8_t cmd, const uint8_t *payload, uint8_t count) {
    uint8_t packet[32] = {'I','G',1,cmd,71,0,count,0}; if (count) memcpy(packet + 8, payload, count);
    unsigned before = replies; raw_hid_receive(packet, 32);
    check(replies == before + 1 && response[3] == cmd && response[4] == 71, "response command/sequence correlation"); return response[5];
}
static void make_config(uint8_t *config, uint8_t color) { for (uint16_t i = 0; i < IG_SIZE; i++) config[i] = default_byte(i); config[IG_RGB_OFFSET] = color; }
static uint8_t begin(const uint8_t *config, uint32_t gen) { uint8_t p[10]; put16(p, IG_SIZE); put32(p + 2, gen); put32(p + 6, crc_buffer(config, IG_SIZE)); return command(4, p, 10); }
static void stage_all(const uint8_t *config) {
    for (uint16_t at = 0; at < IG_SIZE;) { uint8_t p[24] = {0}; uint8_t n = IG_SIZE - at > 20 ? 20 : IG_SIZE - at; put16(p, at); p[2] = n; memcpy(p + 4, config + at, n); check(command(3, p, n + 4) == 0 && u16(response + 8) == at + n, "sequential staging acknowledgement"); at += n; }
}
static uint8_t commit(const uint8_t *config, uint32_t gen) { uint8_t p[8]; put32(p, crc_buffer(config, IG_SIZE)); put32(p + 4, gen); return command(5, p, 8); }
static void install(const uint8_t *config) { uint32_t gen = active_generation; check(begin(config, gen) == 0, "BEGIN valid"); stage_all(config); check(commit(config, gen) == 0, "COMMIT valid"); }
static uint8_t begin_size(const uint8_t *config, uint16_t length, uint32_t gen) { uint8_t p[10]; put16(p,length); put32(p+2,gen); put32(p+6,crc_buffer(config,length)); return command(4,p,10); }
static void stage_size_all(const uint8_t *config, uint16_t length) {
    for (uint16_t at=0; at<length;) { uint8_t p[24]={0}; uint8_t n=length-at>20?20:length-at; put16(p,at); p[2]=n; memcpy(p+4,config+at,n); check(command(3,p,n+4)==0 && u16(response+8)==at+n,"variable length stage acknowledgement"); at+=n; }
}
static uint8_t commit_size(const uint8_t *config, uint16_t length, uint32_t gen) { uint8_t p[8]; put32(p,crc_buffer(config,length)); put32(p+4,gen); return command(5,p,8); }
static void install_size(const uint8_t *config, uint16_t length) { uint32_t gen=active_generation; check(begin_size(config,length,gen)==0,"variable length BEGIN"); stage_size_all(config,length); check(commit_size(config,length,gen)==0,"variable length COMMIT"); }
static void make_v2(uint8_t *config) {
    memset(config,0,IG_MAX_SIZE); make_config(config,0xC0); put16(config+4,2); config[24]=1; config[25]=5;
    for(uint8_t layer=0;layer<4;layer++) { config[IG_SIZE+layer*8+1]=128; config[IG_SIZE+layer*8+2]=255; config[IG_SIZE+layer*8+3]=3; }
    for(uint16_t at=IG_OVERRIDE_OFFSET;at<IG_MAX_SIZE;at+=3) config[at]=255;
}
static void check_readback(const uint8_t *config, uint16_t length) {
    for(uint16_t at=0;at<length;) { uint8_t p[4]={0}; uint8_t n=length-at>20?20:length-at; put16(p,at); p[2]=n; check(command(2,p,4)==0 && !memcmp(response+12,config+at,n),"full active variable blob readback exact"); at+=n; }
}
static void effect_runtime_checks(void) {
    uint8_t legacy[IG_SIZE], config[IG_MAX_SIZE], broken[IG_MAX_SIZE];
    reset(); make_config(legacy,0xC0); install(legacy); unsigned initial_writes=writes; uint32_t initial_crc=active_crc;
    keyboard_post_init_user();
    check(writes==initial_writes && active_crc==initial_crc && active_generation==1 && active_size==IG_SIZE,"V1 startup does not rewrite or migrate persisted data");
    check_readback(legacy,IG_SIZE);
    check(command(1,NULL,0)==0 && (response[15]&8) && u16(response+30)==1,"effects capability on unchanged active V1");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(legacy[IG_RGB_OFFSET]) && rendered[0].g==reference_pwm(legacy[IG_RGB_OFFSET+1]) && rendered[0].b==reference_pwm(legacy[IG_RGB_OFFSET+2]),"legacy stored sRGB decoded only at output");
    make_v2(config); check(IG_MAX_SIZE==(IG_BOARD==1?6360:4184),"final schema2 fixed size"); check(validate_config(config,IG_MAX_SIZE),"V2 defaults valid");
    check(!validate_config(config,IG_SIZE) && !validate_config(legacy,IG_MAX_SIZE-1),"schema and length mismatch rejected before access");
    for(uint8_t mode=0;mode<=20;mode++) { config[IG_SIZE]=mode; config[IG_OVERRIDE_OFFSET]=mode|128; config[IG_OVERRIDE_OFFSET+1]=255; config[IG_OVERRIDE_OFFSET+2]=0; check(validate_config(config,IG_MAX_SIZE),"every global and individual effect ID accepted"); }
    make_v2(config);
    const uint16_t invalid_offsets[]={IG_SIZE,IG_SIZE+3,IG_SIZE+4,IG_SIZE+5,IG_OVERRIDE_OFFSET,IG_OVERRIDE_OFFSET+1,24,25,26};
    const uint8_t invalid_values[]={21,0,4,1,0x20,1,3,41,1};
    for(unsigned i=0;i<sizeof(invalid_values);i++) { memcpy(broken,config,IG_MAX_SIZE); broken[invalid_offsets[i]]=invalid_values[i]; check(!validate_config(broken,IG_MAX_SIZE),"V2 semantic out of range/reserved/canonical inherit rejected"); }
    check(begin_size(config,IG_MAX_SIZE-1,1)==2 && writes==initial_writes,"unsupported BEGIN length does not write");
    install_size(config,IG_MAX_SIZE); check(active_size==IG_MAX_SIZE && active_generation==2,"explicit V2 commit selects larger slot");
    check(command(1,NULL,0)==0 && u16(response+16)==IG_MAX_SIZE && u16(response+30)==2,"INFO advertises actual schema2 size");
    check_readback(config,IG_MAX_SIZE);
    initial_writes=writes; keyboard_post_init_user(); check(writes==initial_writes && active_size==IG_MAX_SIZE && active_generation==2,"V2 reboot retained without migration");
    install(legacy); check(active_size==IG_SIZE && active_generation==3,"explicit V1 downgrade allowed, earlier V2 slot retained"); check_readback(legacy,IG_SIZE);
    // Global target excludes keys, yet a key's individual override still applies.
    make_v2(config); config[IG_SIZE+3]=2; config[IG_SIZE+2]=0;
    config[IG_OVERRIDE_OFFSET]=0; config[IG_OVERRIDE_OFFSET+1]=128; config[IG_OVERRIDE_OFFSET+2]=128;
    install_size(config,IG_MAX_SIZE); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(ig_effect_scale(0xC0,128)) && rendered[1].r==reference_pwm(32) && rendered[RGB_MATRIX_LED_COUNT-1].r==0,"per key override bypasses target, inherited key retains base, targeted side dark");
    // Mirrored side phase preserves independent per-LED base colors, and applies to overrides.
    make_v2(config); config[IG_SIZE]=3; config[IG_SIZE+4]=2; install_size(config,IG_MAX_SIZE); mock_now=1731; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!memcmp(&rendered[RGB_MATRIX_LED_COUNT-2],&rendered[RGB_MATRIX_LED_COUNT-1],sizeof(ig_rgb_t)),"side sync equal geometry gives equal global rainbow phase");
    make_v2(config); config[IG_SIZE]=9; install_size(config,IG_MAX_SIZE);
    keyrecord_t press={.event={.pressed=true,.type=1,.key={0,0}}};
    for(unsigned i=0;i<IG_EFFECT_HITS+4;i++) { mock_now++; process_record_user(4,&press); }
    check(effect_hit_count==IG_EFFECT_HITS && effect_hits[0].time==mock_now,"hit state bounded to newest eight physical presses");
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==reference_pwm(0xC0) && rendered[1].r==0,"reactive highlights only pressed key among keys");
    press.event.pressed=false; process_record_user(4,&press); check(effect_hit_count==IG_EFFECT_HITS,"release does not add hit");
    suspend_power_down_user(); check(effect_hit_count==0,"suspend clears effects hits");
    for(unsigned failure=1;failure<=4;failure++) {
        reset(); install(legacy); begin_size(config,IG_MAX_SIZE,1); stage_size_all(config,IG_MAX_SIZE); fail_write=writes+failure;
        check(commit_size(config,IG_MAX_SIZE,1)==7 && active_size==IG_SIZE && active_generation==1 && !storage_ok,"V2 migration write failure retains valid V1, fault latched");
    }
    reset(); install(legacy); install_size(config,IG_MAX_SIZE);
    // A CRC-valid but semantically invalid newer slot must not hide the earlier V1.
    uint16_t bad_base=slot_base(active_slot); uint8_t *bad_blob=mock_backing+bad_base+IG_SLOT_HEADER;
    bad_blob[IG_OVERRIDE_OFFSET]=0x40; put32(mock_backing+bad_base+12,crc_buffer(bad_blob,IG_MAX_SIZE));
    keyboard_post_init_user(); check(active_size==IG_SIZE && active_generation==1,"invalid newer V2 semantics falls back to unchanged older V1 slot");
    reset(); install(legacy); begin_size(config,IG_MAX_SIZE,1); stage_size_all(config,IG_MAX_SIZE); fail_write=writes+4; fail_after_persist=true;
    check(commit_size(config,IG_MAX_SIZE,1)==7 && active_size==IG_MAX_SIZE && active_generation==2 && !storage_ok,"uncertain V2 final marker failure reports newly persisted V2 honestly");
    check_readback(config,IG_MAX_SIZE);
}
static void timing_runtime_checks(void) {
    uint8_t config[IG_MAX_SIZE]; reset(); make_v2(config); config[24]=8; config[25]=40;
    install_size(config,IG_MAX_SIZE);
    check(mock_active_interval==1 && mock_saved_interval==8 && mock_debounce==40 && !mock_reconnect_pending && !mock_reconnects,"timing COMMIT applies debounce but never reconnects or changes active descriptor");
    check_readback(config,IG_MAX_SIZE);
    check(command(1,NULL,0)==0 && (response[15]&16),"timing support capability present");
    check(begin_size(config,IG_MAX_SIZE,active_generation)==0 && command(8,NULL,0)==3 && !mock_reconnect_pending,"RAW8 blocked during staging"); command(6,NULL,0);
    pressed_rows[0]=1; check(command(8,NULL,0)==3 && !mock_reconnect_pending,"RAW8 blocked by physical key"); pressed_rows[0]=0;
    macro_running=true; check(command(8,NULL,0)==3 && !mock_reconnect_pending,"RAW8 blocked by active macro"); macro_running=false;
    uint8_t unexpected=1; check(command(8,&unexpected,1)==1 && !mock_reconnect_pending,"RAW8 rejects payload");
    effect_hit_count=3;
    for (uint8_t i=0;i<effect_hit_count;i++) effect_hits[i].time=mock_now;
    check(command(8,NULL,0)==0 && response[8]==1 && response[9]==8 && response[10]==1 && !response[11] && mock_reconnect_pending,"RAW8 ACK reports old/new interval and queued action");
    mock_now+=99; housekeeping_task_user(); check(mock_reconnects==0 && effect_hit_count==3,"no reconnect before ACK delivery window");
    mock_now++; pressed_rows[0]=1; housekeeping_task_user(); check(mock_reconnects==0,"key activity defers scheduled reconnect");
    pressed_rows[0]=0; housekeeping_task_user(); check(mock_reconnects==1 && !effect_hit_count && mock_active_interval==8,"idle reconnect completes and clears reactive history");
    check(command(8,NULL,0)==0 && !response[10] && !mock_reconnect_pending,"RAW8 matching interval is no-op");
    mock_now+=1000; housekeeping_task_user(); check(mock_reconnects==1,"no repeated reconnect after completed action");
    ig_timing_init(1,5); keyboard_pre_init_user();
    check(mock_active_interval==8 && mock_debounce==40,"early pre-init loads persisted timing before descriptors");
    keyboard_post_init_user(); check(mock_active_interval==8 && mock_debounce==40,"post-init does not undo early timing selection");
    storage_ok=false; check(command(8,NULL,0)==7,"storage fault blocks remote reconnect");
}

static void reactive_history_expiry_checks(void) {
    uint8_t config[IG_MAX_SIZE]; reset(); make_v2(config);
    config[IG_SIZE]=9; config[IG_SIZE+1]=0; install_size(config,IG_MAX_SIZE);
    unsigned before_writes=writes; uint32_t before_generation=active_generation, before_crc=active_crc;
    keyrecord_t press={.event={.pressed=true,.type=1,.key={0,0}}};
    uint32_t first=UINT32_MAX-1999U;
    mock_now=first; process_record_user(4,&press);
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r>0 && effect_hit_count==1,"new reactive key press lights its key");
    mock_now=first+1000U; process_record_user(4,&press);
    mock_now=first+3999U; housekeeping_task_user();
    check(effect_hit_count==2,"oldest hit survives until maximum lifetime across timer wrap");
    mock_now++; housekeeping_task_user();
    check(effect_hit_count==1 && effect_hits[0].time==first+1000U,"only expired oldest hit is discarded at its deadline");
    mock_now=first+5000U; housekeeping_task_user();
    check(effect_hit_count==0,"latest hit is also permanently removed at its deadline");
    // The same timer value recurs after a complete 32-bit timer cycle, without input.
    mock_now=first+1000U; housekeeping_task_user();
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(!rendered[0].r && !rendered[0].g && !rendered[0].b && !effect_hit_count,"expired hits cannot light up again after a complete timer cycle");
    check(writes==before_writes && active_generation==before_generation && active_crc==before_crc,"reactive expiry does not change stored settings");
}

static void set_recovery_chord(void) {
    memset(pressed_rows, 0, sizeof(pressed_rows));
#if IG_BOARD == 1
    pressed_rows[9] = 1U << 4; pressed_rows[4] = 1U << 5;
#else
    pressed_rows[1] = 1U << 3; pressed_rows[3] = 1U << 3;
#endif
}
static void fixture(const char *directory, const char *kind) {
    char name[1024]; uint8_t config[IG_MAX_SIZE + 1];
    snprintf(name, sizeof(name), "%s/%s-%s.bin", directory, IG_BOARD == 1 ? "pro-iso" : "numpad", kind);
    FILE *file = fopen(name, "rb"); check(file != NULL, "C# fixture exists");
    size_t count = fread(config, 1, sizeof(config), file); fclose(file);
    check((count == IG_SIZE || count == IG_MAX_SIZE) && validate_config(config, count), "C# serialized fixture passes actual firmware semantic validation");
    reset(); install_size(config,count);
    for (uint16_t offset = 0; offset < count;) {
        uint8_t request[4] = {0}; uint8_t n = count - offset > 20 ? 20 : count - offset;
        put16(request, offset); request[2] = n;
        check(command(2, request, 4) == 0 && response[6] == n + 4 && u16(response + 8) == offset && response[10] == n && !response[11] && memcmp(response + 12, config + offset, n) == 0, "C# -> firmware COMMIT -> READ_ACTIVE exact bytes");
        offset += n;
    }
}
#ifdef IG_HAS_SLIDER
static void sample_slider(int16_t value, unsigned count) {
    analog_sample=value;
    for(unsigned i=0;i<count;i++) { mock_now+=20; slider_task(); }
}
static void prime_slider(int16_t value) { slider_reset(); sample_slider(value,3); }
#endif
#include "color_sequences_checks.h"
#include "standby_checks.h"
#include "lighting_test_checks.h"
#include "version_reporting_checks.h"
#include "color_output_checks.h"
#include "cache_staging_checks.h"
int main(int argc, char **argv) {
    uint8_t first[IG_SIZE], second[IG_SIZE], bad[IG_SIZE];
    reset(); make_config(first, 0x15); make_config(second, 0x67);
    check(IG_SIZE == (IG_BOARD == 1 ? 5140 : 3780), "board schema exact blob size");
    check(command(1, NULL, 0) == 0 && response[8] == IG_BOARD && u16(response + 16) == IG_SIZE && (response[15] & 1), "INFO schema and storage flag");
    check(active_generation == 0 && active_slot == -1, "empty storage chooses generation0 defaults");
    uint8_t bounds[4] = {255,255,20,0}; check(command(2, bounds, 4) == 2, "READ rejects overflowing address");
    uint8_t packet[32] = {'I','G',1,1,9,0,25,0}; raw_hid_receive(packet, 32); check(response[5] == 1, "payload count >24 rejected");
    packet[6] = 0; packet[31] = 1; raw_hid_receive(packet, 32); check(response[5] == 1, "nonzero padding rejected");
    packet[31] = 0; unsigned before = replies; raw_hid_receive(packet, 31); check(replies == before, "short packet ignored without buffer access");
    uint8_t write[24] = {0,0,20,0}; memcpy(write + 4, first, 20); check(command(3, write, 24) == 4, "no upload rejects stage write");
    check(begin(first, 1) == 8, "stale generation rejects BEGIN");
    check(begin(first, 0) == 0, "initial BEGIN"); write[0] = 1; check(command(3, write, 24) == 2, "staging gap rejected"); write[0] = 0;
    check(command(3, write, 24) == 0 && stage_received == 20, "first staging chunk");
    check(command(3, write, 24) == 0 && stage_received == 20, "duplicate is idempotent");
    write[4] ^= 1; check(command(3, write, 24) == 2 && staging[0] == first[0], "conflicting duplicate leaves existing bytes intact");
    check(commit(first, 0) == 2 && writes == 0, "incomplete COMMIT does not write");
    check(command(6, NULL, 0) == 0 && !stage_open, "ABORT closes staging");
    begin(first, 0); stage_all(first); check(commit(second, 0) == 5 && writes == 0, "wrong CRC does not write");
    pressed_rows[0] = 1; check(commit(first, 0) == 3 && writes == 0, "held physical key blocks COMMIT"); pressed_rows[0] = 0;
    macro_running = true; check(commit(first, 0) == 3, "running macro blocks COMMIT"); macro_running = false;
    check(commit(first, 0) == 0 && active_generation == 1 && active_slot == 0 && reloads >= 2, "commit reloads actual backing before success");
    check(memcmp(mock_backing + slot_base(0) + IG_SLOT_HEADER, first, IG_SIZE) == 0, "persisted data exact");
    memset(mock_cache, 0xEC, sizeof(mock_cache)); keyboard_post_init_user(); check(active_generation == 1 && active_byte(IG_RGB_OFFSET) == 0x15, "power-cycle simulation loads backing not stale cache");
    uint16_t hole = 0; while (hole < MATRIX_ROWS * MATRIX_COLS && ig_physical[hole]) hole++;
    check(hole < MATRIX_ROWS * MATRIX_COLS, "board fixture includes a matrix hole");
    memcpy(bad, second, IG_SIZE); put16(bad + IG_KEYS_OFFSET + hole * 2, 4); begin(bad, 1); stage_all(bad); unsigned old_writes = writes;
    check(commit(bad, 1) == 6 && writes == old_writes && active_generation == 1, "missing physical key assignment rejected before write");
    memcpy(bad, second, IG_SIZE); put16(bad + IG_ENCODER_OFFSET, 0x5221); check(!validate_config(bad, IG_SIZE), "momentary encoder layer rejected");
    memcpy(bad, second, IG_SIZE); put16(bad + IG_KEYS_OFFSET, 0x7C00); check(!validate_config(bad, IG_SIZE), "boot/reset keycode rejected");
    memcpy(bad, second, IG_SIZE); put16(bad + IG_MACRO_OFFSET, 767); put16(bad + IG_MACRO_OFFSET + 2, 128); check(!validate_config(bad, IG_SIZE), "macro range overflow rejected");

    for (unsigned failure = 1; failure <= 4; failure++) {
        reset(); install(first); begin(second, 1); stage_all(second); fail_write = writes + failure;
        check(commit(second, 1) == 7, "each physical write failure returns storage status");
        check(!storage_ok && !stage_open && active_generation == 1 && active_byte(IG_RGB_OFFSET) == 0x15, "failed inactive slot does not replace valid active backing");
        check(begin(second, 1) == 7, "fault latch prevents dirty-cache retry");
    }
    reset(); install(first); begin(second, 1); stage_all(second); fail_write = writes + 4; fail_after_persist = true;
    check(commit(second, 1) == 7 && active_generation == 2 && !storage_ok, "uncertain final-marker failure reports actual persisted new generation");
    check(active_byte(IG_RGB_OFFSET) == 0x67, "uncertain commit readback exposes persisted bytes");
    reset(); install(first); begin(second, 1); stage_all(second); fail_write = writes + 2; erase_on_failure = true;
    check(commit(second, 1) == 7 && active_generation == 0 && active_slot == -1 && !storage_ok, "simulated consolidation loss falls back honestly; A/B not power-loss proof");
    reset(); fail_reload = true; keyboard_post_init_user(); check(!storage_ok && command(1, NULL, 0) == 0 && !(response[15] & 1), "backing reload failure clears persistent-available flag");

    reset(); make_config(bad, 0x22); put16(bad + 14, 3); put16(bad + IG_MACRO_OFFSET, 0); put16(bad + IG_MACRO_OFFSET + 2, 3);
    bad[IG_EVENT_OFFSET] = 1; put16(bad + IG_EVENT_OFFSET + 2, 4);
    bad[IG_EVENT_OFFSET + 4] = 3; put16(bad + IG_EVENT_OFFSET + 6, 100);
    bad[IG_EVENT_OFFSET + 8] = 2; put16(bad + IG_EVENT_OFFSET + 10, 4);
    check(validate_config(bad, IG_SIZE), "balanced macro fixture valid"); install(bad);
    pressed_rows[0] = 1; keyrecord_t encoder = {.event = {.pressed = true, .type = 2}};
    process_record_user(0x7700, &encoder); check(!macro_running, "encoder macro rejects one held physical key");
    keyrecord_t physical = {.event = {.pressed = true, .type = 1}};
    process_record_user(0x7700, &physical); check(macro_running, "physical macro allows its own trigger");
    macro_task(); check(registered[4], "macro key down emitted");
    process_record_user(5, &physical); check(!macro_running && !registered[4], "new normal key cancels and releases macro before physical processing");
    pressed_rows[0] = 0; process_record_user(0x7700, &encoder); check(macro_running, "encoder macro works without held keys");
    uint32_t started = mock_now;
    macro_task(); check(registered[4], "encoder macro first event"); macro_task(); mock_now = started + 99; macro_task(); check(registered[4], "macro delay nonblocking and not early"); mock_now = started + 100; macro_task(); check(!registered[4], "macro key up on deadline");
    suspend_power_down_user(); check(!macro_running, "suspend releases macro state");
    // Actual matrix coordinates: the held Fn switch is exempt, an ordinary modifier is not.
    const uint8_t fn_row = IG_BOARD == 1 ? 9 : 0, fn_col = IG_BOARD == 1 ? 2 : 0;
    const uint8_t trigger_row = 1, trigger_col = IG_BOARD == 1 ? 3 : 0;
    const uint8_t ctrl_row = 0, ctrl_col = IG_BOARD == 1 ? 6 : 1;
    keyrecord_t fn_event = {.event = {.pressed = true, .type = 1, .key = {fn_row, fn_col}}};
    keyrecord_t macro_event = {.event = {.pressed = true, .type = 1, .key = {trigger_row, trigger_col}}};
    keyrecord_t ctrl_event = {.event = {.pressed = true, .type = 1, .key = {ctrl_row, ctrl_col}}};
    memset(pressed_rows, 0, sizeof(pressed_rows)); pressed_rows[fn_row] |= 1U << fn_col;
    check(process_record_user(0x5221, &fn_event) && (held_layer_keys[fn_row] & (1U << fn_col)), "MO press records physical Fn position");
    pressed_rows[trigger_row] |= 1U << trigger_col;
    process_record_user(0x7700, &macro_event); check(macro_running, "held Fn plus physical macro trigger starts macro"); cancel_macro();
    pressed_rows[trigger_row] &= ~(1U << trigger_col);
    process_record_user(0x7700, &encoder); check(macro_running, "held Fn plus encoder macro starts macro"); cancel_macro();
    pressed_rows[ctrl_row] |= 1U << ctrl_col; process_record_user(0xE0, &ctrl_event);
    process_record_user(0x7700, &encoder); check(!macro_running, "Fn plus held Ctrl still blocks encoder macro");
    pressed_rows[trigger_row] |= 1U << trigger_col; process_record_user(0x7700, &macro_event);
    check(!macro_running, "Fn plus Ctrl plus physical macro trigger remains blocked");
    pressed_rows[trigger_row] &= ~(1U << trigger_col); pressed_rows[ctrl_row] &= ~(1U << ctrl_col);
    fn_event.event.pressed = false; pressed_rows[fn_row] &= ~(1U << fn_col); process_record_user(4, &fn_event);
    check(!(held_layer_keys[fn_row] & (1U << fn_col)), "Fn release clears mask even if resolved keycode changed");
    fn_event.event.pressed = true; pressed_rows[fn_row] |= 1U << fn_col; process_record_user(0x5221, &fn_event);
    process_record_user(0xE0, &fn_event); check(!(held_layer_keys[fn_row] & (1U << fn_col)), "non-MO press clears old position exemption");
    process_record_user(0x7700, &encoder); check(!macro_running, "remapped Fn position held as Ctrl blocks encoder macro");
    process_record_user(0x5221, &fn_event); suspend_power_down_user();
    bool no_layer_keys = true; for (uint8_t row = 0; row < MATRIX_ROWS; row++) if (held_layer_keys[row]) no_layer_keys = false;
    check(no_layer_keys && !macro_running, "suspend clears all Fn exemptions and macro state");
    reset(); check(boot_jumps == 0, "normal startup does not enter bootloader");
    check(command(7, NULL, 0) == 9 && boot_jumps == 0, "unknown remote command cannot enter bootloader");
    set_recovery_chord(); scans = waited_ms = 0; recovery_startup();
    check(boot_jumps == 1 && waited_ms == 550 && scans == 52, "fixed physical recovery chord requires debounce plus full500ms hold");
    set_recovery_chord(); pressed_rows[0] = 1; scans = waited_ms = 0; boot_jumps = 0; recovery_startup();
    check(boot_jumps == 0 && waited_ms == 50, "extra held key prevents recovery entry");
    set_recovery_chord(); scans = waited_ms = 0; release_at_scan = 10; recovery_startup();
    check(boot_jumps == 0 && waited_ms < 550, "release during recovery hold prevents boot entry");
    release_at_scan = 0;
#ifdef IG_HAS_SLIDER
    keyboard_pre_init_user(); check(external_cs_high && external_cs_output, "external flash CS latch high before output mode");
    reset(); make_config(bad, 0x20); bad[16] = 2; install(bad); prime_slider(512);
    check(volume_taps == 0, "relative volume slider startup position sends nothing");
    sample_slider(1000,3); check(volume_taps == 1, "confirmed large slider movement sends one bounded volume step");
#endif
    check(argc == 3, "C# legacy and standby fixture directories explicitly supplied"); fixture(argv[1], "default"); fixture(argv[1], "configured");
    fixture(argv[1],"effects"); fixture(argv[1],"timing"); fixture(argv[1],"v2-default"); fixture(argv[1],"sequences");
    effect_runtime_checks();
    reactive_history_expiry_checks();
    timing_runtime_checks();
    color_sequence_checks(); runtime_brightness_checks();
    standby_checks(); fixture(argv[2],"v4-standby"); fixture(argv[2],"v4-sequences");
    slider_filter_checks();
    lighting_test_checks();
    lighting_slider_checks();
    screensaver_checks();
    version_reporting_checks();
    color_output_checks();
    cache_staging_checks();
    printf("PASS: board%u, %u firmware runtime checks; separate mock cache/backing; zero hardware access.\n", IG_BOARD, checks);
    return 0;
}
