// SPDX-License-Identifier: GPL-2.0-or-later
// Actual common runtime, board-specific generated headers, independently derived
// QMK LED geometry, and bytes emitted by the real desktop serializer. No USB/OS API.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define QMK_KEYBOARD_H "port_mock.h"
#include "../../inglorious/inglorious.c"
#include "port_reference.h"

static uint8_t cache[16384], backing[16384], response[32], defaults[IG_MAX_SIZE];
static matrix_row_t pressed[MATRIX_ROWS];
static ig_rgb_t pixels[RGB_MATRIX_LED_COUNT];
static uint8_t registered[256];
static uint32_t now;
static unsigned checks, replies, writes, jumps, scans, release_at, fail_write;
static int raised_cs = -1, output_cs = -1;
static bool borrowed;
static uint8_t active_interval = 1, mock_saved_interval = 1, debounce_ms = 5;
static bool reconnect;
uint32_t layer_state, default_layer_state;
led_config_t g_led_config;

static void check(bool value, const char *message) {
    checks++;
    if (!value) { fprintf(stderr, "FAIL board%d check%u: %s\n", IG_BOARD, checks, message); exit(1); }
}
static void bounds(uint32_t offset, size_t length) { check(offset <= sizeof(cache) && length <= sizeof(cache) - offset, "storage range"); }
uint8_t eeprom_read_byte(const uint8_t *address) { uint32_t at = (uintptr_t)address; bounds(at, 1); return cache[at]; }
void eeprom_read_block(void *out, const void *address, size_t length) { uint32_t at = (uintptr_t)address; bounds(at, length); memcpy(out, cache + at, length); }
wear_leveling_status_t wear_leveling_write(uint32_t address, const void *data, size_t length) {
    bounds(address, length); if (borrowed) return WEAR_LEVELING_FAILED;
    memcpy(cache + address, data, length); writes++;
    if (writes == fail_write) return WEAR_LEVELING_FAILED;
    memcpy(backing + address, data, length); return WEAR_LEVELING_SUCCESS;
}
wear_leveling_status_t wear_leveling_init(void) { if (borrowed) return WEAR_LEVELING_FAILED; memcpy(cache, backing, sizeof(cache)); return WEAR_LEVELING_SUCCESS; }
const uint8_t *ig_storage_view(uint32_t address, size_t length) { bounds(address, length); return cache + address; }
uint8_t *ig_storage_stage_begin(uint32_t address, size_t length) {
    bounds(address, length); if (borrowed) return NULL;
    borrowed = true; memset(cache + address, 0, length); return cache + address;
}
wear_leveling_status_t ig_storage_stage_write(uint32_t address, const void *data, size_t length) {
    bounds(address, length); check(borrowed, "commit owns borrowed cache");
    memmove(cache + address, data, length); writes++;
    if (writes == fail_write) return WEAR_LEVELING_FAILED;
    memcpy(backing + address, cache + address, length); return WEAR_LEVELING_SUCCESS;
}
wear_leveling_status_t ig_storage_stage_finish(bool commit_attempted) {
    (void)commit_attempted; borrowed = false; return wear_leveling_init();
}
uint16_t keycode_at_keymap_location_raw(uint8_t layer, uint8_t row, uint8_t col) {
    return u16(defaults + IG_KEYS_OFFSET + ((layer * MATRIX_ROWS + row) * MATRIX_COLS + col) * 2);
}
matrix_row_t matrix_get_row(uint8_t row) { check(row < MATRIX_ROWS, "matrix row"); return pressed[row]; }
void raw_hid_send(uint8_t *packet, uint8_t length) { check(length == 32, "RAW frame length"); memcpy(response, packet, length); replies++; }
void register_code(uint8_t code) { registered[code] = 1; }
void unregister_code(uint8_t code) { registered[code] = 0; }
uint32_t timer_read32(void) { return now; }
uint32_t timer_elapsed32(uint32_t start) { return now - start; }
void layer_clear(void) { layer_state = 0; }
uint8_t get_highest_layer(uint32_t state) { uint8_t i = 0; while (state >>= 1) i++; return i; }
void rgb_matrix_set_color(uint8_t led, uint8_t r, uint8_t g, uint8_t b) { check(led < RGB_MATRIX_LED_COUNT, "LED index"); pixels[led] = (ig_rgb_t){r,g,b}; }
void rgb_matrix_enable_noeeprom(void) {}
void rgb_matrix_mode_noeeprom(uint8_t mode) { (void)mode; }
void gpio_write_pin_high(int pin) { raised_cs = pin; }
void gpio_set_pin_output(int pin) { check(raised_cs == pin, "inactive SPI chip select before output enable"); output_cs = pin; }
void matrix_scan(void) { scans++; if (release_at && scans == release_at) memset(pressed, 0, sizeof(pressed)); }
void wait_ms(uint16_t ms) { now += ms; }
void bootloader_jump(void) { jumps++; }
bool ig_timing_valid(uint8_t interval, uint8_t delay) { return (interval == 1 || interval == 2 || interval == 4 || interval == 8) && delay <= 40; }
void ig_timing_init(uint8_t interval, uint8_t delay) { active_interval = mock_saved_interval = interval; debounce_ms = delay; reconnect = false; }
bool ig_timing_update(uint8_t interval, uint8_t delay) { if (!ig_timing_valid(interval, delay)) return false; mock_saved_interval = interval; debounce_ms = delay; return true; }
uint8_t ig_timing_active_interval(void) { return active_interval; }
uint8_t ig_timing_saved_interval(void) { return mock_saved_interval; }
uint8_t ig_timing_debounce_ms(void) { return debounce_ms; }
bool ig_timing_reconnect_pending(void) { return reconnect; }
bool ig_timing_schedule_reconnect(void) { reconnect = active_interval != mock_saved_interval; return reconnect; }
void ig_timing_task(bool safe) { if (safe && reconnect) { active_interval = mock_saved_interval; reconnect = false; } }

static void boot(bool erase) {
    borrowed = false; if (erase) memset(backing, 0, sizeof(backing));
    memset(pressed, 0, sizeof(pressed)); memset(registered, 0, sizeof(registered));
    stage_open = macro_running = runtime_ready = false; stage_received = 0; active_slot = -1;
    lighting_suspended = light_test_active = false; layer_state = default_layer_state = 0;
    writes = fail_write = jumps = scans = release_at = 0; now = 0; raised_cs = output_cs = -1;
    g_led_config = reference_leds;
    keyboard_pre_init_user(); keyboard_post_init_user();
    check(storage_ok && runtime_ready && !borrowed && writes == 0, "boot loads without storage writes");
#ifdef IG_EXTERNAL_SPI_CS
    check(output_cs == IG_EXTERNAL_SPI_CS, "model-specific external storage remains deselected");
#else
    check(output_cs == -1, "no invented SPI chip select");
#endif
}
static uint8_t command(uint8_t command, const uint8_t *payload, uint8_t count) {
    uint8_t frame[32] = {'I','G',1,command,17,0,count,0}; if (count) memcpy(frame+8,payload,count);
    unsigned before = replies; raw_hid_receive(frame,32);
    check(replies == before+1 && response[3] == command && response[4] == 17, "correlated RAW response"); return response[5];
}
static uint8_t begin(const uint8_t *config, uint16_t length) {
    uint8_t p[10]; put16(p,length); put32(p+2,active_generation); put32(p+6,crc_buffer(config,length)); return command(4,p,10);
}
static void upload(const uint8_t *config, uint16_t length) {
    check(begin(config,length) == 0,"begin upload");
    for (uint16_t offset=0;offset<length;) {
        uint8_t n=length-offset>20?20:length-offset; uint8_t p[24]={0}; put16(p,offset); p[2]=n; memcpy(p+4,config+offset,n);
        check(command(3,p,n+4)==0 && u16(response+8)==offset+n,"sequential upload");
        check(command(3,p,n+4)==0,"identical duplicate accepted");
        p[4]^=1; check(command(3,p,n+4)==2,"conflicting duplicate rejected"); offset+=n;
    }
}
static uint8_t commit(const uint8_t *config, uint16_t length) { uint8_t p[8]; put32(p,crc_buffer(config,length)); put32(p+4,active_generation); return command(5,p,8); }
static void readback(const uint8_t *config, uint16_t length) {
    check(active_size==length,"active configuration length");
    for(uint16_t offset=0;offset<length;offset++) check(active_byte(offset)==config[offset],"complete configuration readback");
}
static uint16_t load(const char *path, uint8_t *bytes) {
    FILE *file=fopen(path,"rb"); check(file!=NULL,"fixture exists"); size_t length=fread(bytes,1,IG_MAX_SIZE,file);
    check(feof(file) || fgetc(file)==EOF,"fixture capacity"); fclose(file);
    check(length==IG_SIZE || length==IG_MAX_SIZE,"fixture length"); return length;
}
static void check_recovery(void) {
    memset(pressed,0,sizeof(pressed)); scans=jumps=0; release_at=0;
    pressed[reference_recovery[0][0]]|=(matrix_row_t)1<<reference_recovery[0][1];
    pressed[reference_recovery[1][0]]|=(matrix_row_t)1<<reference_recovery[1][1];
    check(recovery_chord_held(),"Space+B coordinates from independent base layout");
    recovery_startup(); check(jumps==1,"fixed startup chord enters bootloader");
    for(uint8_t row=0;row<MATRIX_ROWS;row++) for(uint8_t col=0;col<MATRIX_COLS;col++) {
        if(pressed[row]&((matrix_row_t)1<<col)) continue;
        pressed[row]|=(matrix_row_t)1<<col; check(!recovery_chord_held(),"third key blocks recovery"); pressed[row]&=~((matrix_row_t)1<<col);
    }
    jumps=scans=0; release_at=4; recovery_startup(); check(jumps==0,"early chord release prevents bootloader entry");
    memset(pressed,0,sizeof(pressed)); release_at=0;
}
static void check_lighting(void) {
    light_test_active=false; stage_open=false; macro_running=false; lighting_suspended=false; reconnect=false;
    uint8_t action=1; check(command(12,&action,1)==0,"lighting test starts");
    check(response[8]==1 && response[9]==1 && u16(response+10)==21000,"keyboard lighting pattern and 21-second duration");
    for(uint16_t t=0;t<21000;t+=100) {
        for(uint8_t led=0;led<RGB_MATRIX_LED_COUNT;led++) {
            ig_rgb_t color=light_test_pixel(t,led);
            if(t<2000) check(color.r==160 && color.g==160 && color.b==160,"all LEDs illuminate for two seconds");
            else if(t<2500) check(!color.r && !color.g && !color.b,"dark gap");
            else if(t<12500) {
                uint8_t step=(t-2500)/1000; bool lit=(t-2500)%1000<700 && led==reference_leds.matrix_co[reference_letters[step][0]][reference_letters[step][1]];
                check(color.r==(lit?160:0) && !color.g && !color.b,"INGLORIOUS follows physical letter keys on each layout");
            } else if(t>=19000) check(color.r==160 && !color.g && !color.b,"all LEDs red at the end");
        }
    }
    now=light_test_started+21000; check(light_test_remaining(now)==0 && !light_test_active,"lighting overlay expires");
}
int main(int argc,char **argv) {
    check(argc==4,"three serializer fixtures supplied"); uint16_t default_length=load(argv[1],defaults); check(default_length==IG_SIZE,"schema1 default");
    check(sizeof(staging)==sizeof(uint8_t *),"no full duplicate staging allocation");
    check(sizeof(ig_physical)==sizeof(reference_physical) && !memcmp(ig_physical,reference_physical,sizeof(reference_physical)),"physical matrix agrees with QMK reference");
    boot(true);
    for(uint16_t i=IG_KEYS_OFFSET;i<IG_ENCODER_OFFSET;i++) check(default_byte(i)==defaults[i],"all four default keymap layers");
    check(command(1,NULL,0)==0 && response[8]==IG_BOARD && response[9]==MATRIX_ROWS && response[10]==MATRIX_COLS && response[12]==RGB_MATRIX_LED_COUNT,"board identity and dimensions");
    check(command(13,NULL,0)==0 && response[10]==IG_BOARD && u16(response+14)==11 && u32(response+20)==0x000B0001,"candidate version identity");
    uint8_t config[IG_MAX_SIZE], before[sizeof(backing)];
    for(int fixture=1;fixture<argc;fixture++) {
        uint16_t length=load(argv[fixture],config); check(validate_config(config,length),"desktop payload semantically accepted");
        memcpy(before,backing,sizeof(backing)); unsigned initial_writes=writes;
        upload(config,length); check(writes==initial_writes && !memcmp(before,backing,sizeof(backing)),"upload is entirely volatile");
        check(command(6,NULL,0)==0 && !borrowed && writes==initial_writes && !memcmp(before,backing,sizeof(backing)),"abort preserves backing");
        upload(config,length); check(commit(config,length)==0 && !borrowed,"commit validated payload"); readback(config,length);
        boot(false); readback(config,length);
        memcpy(before,backing,sizeof(backing)); initial_writes=writes;
        config[6]^=0x40; upload(config,length); check(commit(config,length)==6 && writes==initial_writes && !memcmp(before,backing,sizeof(backing)),"other board payload rejected before writes");
        check(command(6,NULL,0)==0,"abort invalid board payload"); config[6]^=0x40;
        upload(config,length); uint8_t bad[8]; put32(bad,crc_buffer(config,length)^1); put32(bad+4,active_generation);
        check(command(5,bad,8)==5 && writes==initial_writes,"wrong CRC rejected before writes"); check(command(6,NULL,0)==0,"abort bad CRC");
        for(unsigned phase=1;phase<=4;phase++) {
            boot(false); upload(config,length); fail_write=writes+phase;
            check(commit(config,length)==7 && !storage_ok && !borrowed,"each persistence phase failure latches a fault and releases staging");
        }
        boot(false);
    }
    check_recovery(); check_lighting();
    printf("PASS board%d: %u native port checks; no hardware access.\n",IG_BOARD,checks); return 0;
}
