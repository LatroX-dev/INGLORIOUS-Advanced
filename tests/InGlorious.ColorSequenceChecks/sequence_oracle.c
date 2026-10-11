// SPDX-License-Identifier: GPL-2.0-or-later
#include <stdio.h>
#include "../../firmware/inglorious/ig_effects.h"
int main(int argc,char **argv) {
    if(argc!=2)return 1;
    FILE *f=fopen(argv[1],"wb");
    if(!f)return 2;
    // Exercise original renderer too, so the complete real header stays warning-clean.
    uint8_t cfg[8]={0,128,255,3,0};ig_rgb_t color=ig_effect_pixel(cfg,(ig_rgb_t){0,0,0},0,0,0,0,0,NULL,0);if(color.r)return 3;
    for(unsigned seed=0;seed<64;seed++)for(unsigned count=1;count<=8;count++)for(unsigned phase=0;phase<256;phase++) {
        uint8_t colors[24];for(unsigned i=0;i<24;i++)colors[i]=(seed*73+i*47)^(seed+i*19);
        color=ig_effect_sequence(phase,colors,count);fputc(count,f);fputc(phase,f);fwrite(colors,1,24,f);fputc(color.r,f);fputc(color.g,f);fputc(color.b,f);
    }
    return fclose(f)?4:0;
}
