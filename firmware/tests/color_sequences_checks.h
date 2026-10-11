// Included by the actual-runtime harness; no device or native USB access.
static void make_v3(uint8_t *config) {
    make_v2(config); put16(config+4,3); put16(config+12,IG_SEQUENCE_EVENTS);
    config[IG_SEQUENCE_OFFSET]=2; config[IG_SEQUENCE_OFFSET+1]=255; config[IG_SEQUENCE_OFFSET+6]=255;
    config[IG_SIZE]=2; config[IG_SIZE+1]=255; config[IG_SIZE+5]=1;
}
static void color_sequence_checks(void) {
    uint8_t config[IG_MAX_SIZE], bad[IG_MAX_SIZE], legacy[IG_MAX_SIZE];
    reset(); make_v3(config); make_v2(legacy);
    check(IG_SEQUENCE_EVENTS==(IG_BOARD==1?668:702),"sequence storage trades macro capacity without increasing staging RAM");
    check(sizeof(staging)==sizeof(uint8_t *),"staging borrows inactive cache with no duplicate payload allocation");
    check(validate_config(config,IG_MAX_SIZE),"schema3 palette valid");
    install_size(config,IG_MAX_SIZE);
    check(command(1,NULL,0)==0 && (response[15]&32) && (response[15]&64) && u16(response+18)==IG_SEQUENCE_EVENTS && u16(response+30)==3,"INFO reports sequence/runtime support and current macro capacity");
    check_readback(config,IG_MAX_SIZE);
    unsigned before=writes; keyboard_post_init_user(); check(writes==before && active_u16(4)==3,"schema3 reboot is byte-preserving and write-free");
    mock_now=0; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==255 && rendered[0].g==0 && rendered[0].b==0,"sequence starts at exact first color");
    mock_now=100; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==reference_pwm(127) && rendered[0].b==reference_pwm(127),"two-color midpoint uses deterministic integer interpolation");
    mock_now=200; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==0 && rendered[0].b==255,"half cycle is exact second color");
    mock_now=400; rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT); check(rendered[0].r==255 && rendered[0].b==0,"cycle closes back to first color");
    make_v3(config); config[IG_SIZE+3]=2; // Global sequence on sides only.
    config[IG_SEQUENCE_OFFSET+25]=1; config[IG_SEQUENCE_OFFSET+27]=255; // One-color green sequence.
    config[IG_OVERRIDE_OFFSET]=2; config[IG_OVERRIDE_OFFSET+1]=255; config[IG_OVERRIDE_OFFSET+2]=255; config[IG_SEQUENCE_OFFSET+200]=2;
    check(validate_config(config,IG_MAX_SIZE),"individual sequence canonical global-then-led order"); install_size(config,IG_MAX_SIZE); mock_now=0;
    rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].g==255 && !rendered[0].r && !rendered[0].b && rendered[1].r==reference_pwm(32) && rendered[RGB_MATRIX_LED_COUNT-1].r==255,"own sequence bypasses global target; inherited excluded key retains base");
    config[IG_OVERRIDE_OFFSET]=0; install_size(config,IG_MAX_SIZE); rgb_matrix_indicators_advanced_user(0,RGB_MATRIX_LED_COUNT);
    check(rendered[0].r==reference_pwm(0xC0) && active_byte(IG_SEQUENCE_OFFSET+200)==2,"other modes preserve but ignore sequences");
    make_v3(config);
    const uint16_t offsets[]={12,14,IG_SEQUENCE_OFFSET,IG_SEQUENCE_OFFSET+7,IG_SEQUENCE_OFFSET+25,IG_SIZE+5,IG_SEQUENCE_OFFSET+200,IG_SIZE-1};
    const uint16_t values[]={768,IG_SEQUENCE_EVENTS+1,9,1,1,9,1,1};
    for(unsigned i=0;i<sizeof(offsets)/sizeof(offsets[0]);i++) {
        memcpy(bad,config,sizeof(bad)); if(i<2) put16(bad+offsets[i],values[i]); else bad[offsets[i]]=values[i];
        check(!validate_config(bad,IG_MAX_SIZE),"invalid capacity, sequence count, tail, unused slot, ref or inheritance rejected");
    }
    memcpy(bad,config,sizeof(bad)); bad[IG_SIZE+5]=0; check(!validate_config(bad,IG_MAX_SIZE),"unreferenced palette rejected");
    memcpy(bad,config,sizeof(bad)); memcpy(bad+IG_SEQUENCE_OFFSET+25,bad+IG_SEQUENCE_OFFSET,25); bad[IG_SIZE+8+5]=2; check(!validate_config(bad,IG_MAX_SIZE),"duplicate palette rejected");
    memcpy(bad,config,sizeof(bad)); bad[IG_SEQUENCE_OFFSET+50]=1; bad[IG_SIZE+8+5]=3; check(!validate_config(bad,IG_MAX_SIZE),"pool gaps and noncanonical first use rejected");
    put16(config+14,IG_SEQUENCE_EVENTS);
    for(uint16_t i=0;i<IG_SEQUENCE_EVENTS;i++) { config[IG_EVENT_OFFSET+i*4]=(i&1)?2:1; put16(config+IG_EVENT_OFFSET+i*4+2,4); }
    for(uint16_t at=0,m=0;at<IG_SEQUENCE_EVENTS;m++) { uint16_t n=IG_SEQUENCE_EVENTS-at>128?128:IG_SEQUENCE_EVENTS-at; put16(config+IG_MACRO_OFFSET+m*4,at); put16(config+IG_MACRO_OFFSET+m*4+2,n); at+=n; }
    check(validate_config(config,IG_MAX_SIZE),"exact reduced macro capacity ends before palette; no events truncated");
    make_v3(config);
    for(unsigned failure=1;failure<=4;failure++) {
        reset(); install_size(legacy,IG_MAX_SIZE); begin_size(config,IG_MAX_SIZE,1); stage_size_all(config,IG_MAX_SIZE); fail_write=writes+failure;
        check(commit_size(config,IG_MAX_SIZE,1)==7 && active_u16(4)==2 && active_generation==1 && !storage_ok,"schema3 persistence failures retain old schema2 and latch fault");
    }
    reset(); install_size(config,IG_MAX_SIZE); install_size(legacy,IG_MAX_SIZE); check(active_u16(4)==2,"explicit schema2 restore removes palette without changing format");
}
static void runtime_brightness_checks(void) {
    reset(); uint8_t config[IG_MAX_SIZE]; make_v3(config); install_size(config,IG_MAX_SIZE);
    unsigned before=writes; uint32_t gen=active_generation, crc=active_crc;
    check(command(9,NULL,0)==0 && response[6]==4 && response[8]==255 && response[9]==0 && !response[10] && !response[11],"runtime read reports current dimmer and slider mode");
    for(unsigned v=0;v<=255;v++) { uint8_t value=v; check(command(10,&value,1)==0 && response[6]==1 && response[8]==v && brightness==v,"all volatile brightness byte values ACK exactly"); }
    uint8_t value[2]={42,99}; check(command(9,value,1)==1 && command(10,NULL,0)==1 && command(10,value,2)==1 && brightness==255,"bad runtime payload lengths cannot change brightness");
    begin_size(config,IG_MAX_SIZE,gen); check(command(10,value,1)==3 && brightness==255 && command(9,NULL,0)==0,"read-only runtime access allowed during staging; setter blocked"); command(6,NULL,0);
    mock_reconnect_pending=true; check(command(10,value,1)==3 && brightness==255,"setter blocked while USB reconnect queued"); mock_reconnect_pending=false;
    pressed_rows[0]=1; check(command(10,value,1)==0 && brightness==42,"held key does not block safe volatile dimming"); pressed_rows[0]=0;
    check(writes==before && active_generation==gen && active_crc==crc,"runtime commands never write persistence or alter generation/CRC"); check_readback(config,IG_MAX_SIZE);
#ifdef IG_HAS_SLIDER
    config[16]=3; install_size(config,IG_MAX_SIZE); prime_slider(1023);
    check(command(9,NULL,0)==0 && response[8]==255 && response[9]==3,"autonomous Numpad slider brightness is observable");
    before=writes; sample_slider(0,3); check(command(9,NULL,0)==0 && response[8]==0 && writes==before,"filtered slider dimming stays volatile");
#endif
}
