// Exercises the actual common runtime's borrowed-cache lifecycle and fail gates.
static void cache_staging_checks(void) {
    uint8_t first[IG_SIZE], next[IG_MAX_SIZE];
    uint8_t before_cache[sizeof(mock_cache)], before_backing[sizeof(mock_backing)];
    reset(); make_config(first,0x29); install(first); make_v2(next);
    memcpy(before_cache,mock_cache,sizeof(mock_cache)); memcpy(before_backing,mock_backing,sizeof(mock_backing));
    unsigned before_writes=writes, before_reloads=reloads;
    check(begin_size(next,IG_MAX_SIZE,active_generation)==0,"borrowed-cache BEGIN");
    check(staging==mock_cache+slot_base(active_slot==0?1:0)+IG_SLOT_HEADER,"runtime borrows only the inactive payload");
    stage_size_all(next,IG_MAX_SIZE);
    uint8_t byte=7;
    check(wear_leveling_write(0,&byte,1)==WEAR_LEVELING_FAILED,"other EEPROM writes blocked during upload");
    check(wear_leveling_init()==WEAR_LEVELING_FAILED,"unrelated cache initialization blocked during upload");
    check(writes==before_writes && reloads==before_reloads && !memcmp(mock_backing,before_backing,sizeof(mock_backing)),"full upload has zero physical writes");
    check_readback(first,IG_SIZE);
    uint8_t bad_crc[8]; put32(bad_crc,stage_crc^1); put32(bad_crc+4,active_generation);
    check(command(5,bad_crc,8)==5 && writes==before_writes && mock_borrowed,"bad CRC cannot write or release staged data");
    check(command(6,NULL,0)==0 && !stage_open && !staging && !mock_borrowed,"ABORT releases cache ownership");
    check(writes==before_writes && reloads==before_reloads+1 && !memcmp(mock_cache,before_cache,sizeof(mock_cache)) && !memcmp(mock_backing,before_backing,sizeof(mock_backing)),"ABORT restores cache and leaves physical backing unchanged");
    check(begin_size(next,IG_MAX_SIZE,active_generation)==0,"new upload after abort"); stage_size_all(next,IG_MAX_SIZE);
    check(begin(first,active_generation)==0 && stage_received==0 && stage_size==IG_SIZE,"new BEGIN discards earlier upload through read-only reload");
    stage_all(first); check(command(6,NULL,0)==0 && !memcmp(mock_backing,before_backing,sizeof(mock_backing)),"replacement BEGIN and abort never persist either payload");
    check(begin_size(next,IG_MAX_SIZE,active_generation)==0,"borrow before simulated abort failure"); stage_size_all(next,IG_MAX_SIZE);
    fail_reload=true;
    check(command(6,NULL,0)==7 && !storage_ok && !stage_open && !mock_borrowed,"abort read failure is reported and latches storage fault");
    check(writes==before_writes && !memcmp(mock_backing,before_backing,sizeof(mock_backing)),"failed abort has no physical write");
}
