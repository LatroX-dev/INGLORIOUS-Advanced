// Executes the production integer effect renderer. No USB/flash or QMK dependencies.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "../inglorious/ig_effects.h"
static unsigned checks;
static void check(bool valid, const char *name) { checks++; if (!valid) { fprintf(stderr,"FAIL %u: %s\n",checks,name); exit(1); } }
static bool equal(ig_rgb_t a, ig_rgb_t b) { return a.r==b.r && a.g==b.g && a.b==b.b; }
static uint8_t peak(ig_rgb_t color) { uint8_t max=color.r>color.g?color.r:color.g; return max>color.b?max:color.b; }
int main(void) {
    uint8_t config[8]={0,128,255,3,0,0,0,0};
    ig_rgb_t base={203,117,49}, black={0,0,0};
    ig_effect_hit_t hits[IG_EFFECT_HITS];
    for(unsigned i=0;i<IG_EFFECT_HITS;i++) hits[i]=(ig_effect_hit_t){1000+i*7,28+i*23,8+i*7,i};
    check(sizeof(ig_effect_hit_t)==8,"bounded hit state size");
    for(unsigned value=0;value<256;value++) {
        check(ig_effect_scale(value,255)==value && ig_effect_scale(value,0)==0,"scale endpoints preserve/blackout every channel");
        check(equal(ig_effect_pixel(config,(ig_rgb_t){value,value,255-value},0,28,32,false,UINT32_MAX,NULL,0),(ig_rgb_t){value,value,255-value}),"static legacy channels exact");
    }
    for(unsigned mode=0;mode<=20;mode++) {
        config[0]=mode;
        for(unsigned speed=0;speed<256;speed+=17) {
            config[1]=speed;
            for(unsigned t=0;t<64;t++) {
                uint32_t now=1000+t*157;
                for(unsigned x=0;x<=224;x+=28) {
                    config[2]=255; config[3]=1; config[4]=0;
                    check(equal(ig_effect_pixel(config,base,0,x,32,true,now,hits,8),base),"keys target leaves side base unchanged for every mode");
                    config[3]=2;
                    check(equal(ig_effect_pixel(config,base,0,x,32,false,now,hits,8),base),"sides target leaves key base unchanged for every mode");
                    config[3]=3; config[2]=0;
                    check(equal(ig_effect_pixel(config,base,0,x,32,false,now,hits,8),black),"brightness zero blackouts all effect families");
                    config[2]=255; config[4]=2;
                    ig_rgb_t left=ig_effect_pixel(config,base,29,0,32,true,now,hits,8), right=ig_effect_pixel(config,base,30,224,32,true,now,hits,8);
                    check(equal(left,right),"side sync maps equal ordinal geometry for all effects");
                    check(equal(left,ig_effect_pixel(config,base,29,0,32,true,now,hits,255)),"untrusted hit count clamped to fixed capacity");
                }
            }
        }
    }
    const uint8_t directional[]={3,4,5,6,7,12,13,16,17};
    for(unsigned i=0;i<sizeof(directional);i++) {
        config[0]=directional[i]; config[1]=128; config[2]=255; config[3]=3;
        bool differs=false;
        for(unsigned t=0;t<12;t++) {
            config[4]=0; ig_rgb_t a=ig_effect_pixel(config,base,0,56,16,false,t*137,NULL,0);
            config[4]=1; ig_rgb_t b=ig_effect_pixel(config,base,0,56,16,false,t*137,NULL,0);
            if(!equal(a,b)) differs=true;
        }
        check(differs,"each advertised directional effect changes when reversed");
    }
    const uint8_t reactive[]={9,10,11,15,20};
    for(unsigned i=0;i<sizeof(reactive);i++) {
        config[0]=reactive[i]; config[1]=0; config[4]=0;
        check(equal(ig_effect_pixel(config,base,0,28,8,false,1000,hits,0),black),"reactive effects idle black without key events");
        check(!equal(ig_effect_pixel(config,base,0,28,8,false,1000,hits,1),black),"reactive event starts at pressed coordinate");
        check(equal(ig_effect_pixel(config,base,0,28,8,false,5000,hits,1),black),"reactive effect expires at bounded lifetime");
        ig_effect_hit_t wrap={UINT32_MAX-99,28,8,0};
        ig_rgb_t wrapped=ig_effect_pixel(config,base,0,28,8,false,50,&wrap,1), ordinary=ig_effect_pixel(config,base,0,28,8,false,1150,hits,1);
        check(config[0]==11 ? peak(wrapped)==peak(ordinary) : equal(wrapped,ordinary),"reactive age handles timer wrap; rainbow hue has independent clock");
    }
    printf("PASS: %u production effect renderer checks, all21 modes; no hardware access.\n",checks);
    return 0;
}
