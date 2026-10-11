// Exercises the production bulk override with mocked EFL; no USB or real flash.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <limits.h>
#include "bulk_mocks/hal.h"
#include "bulk_mocks/wear_leveling_internal.h"
#include "../inglorious/wb32_bulk.c"

static unsigned checks,program_calls,scalar_calls,descriptor_calls,fail_call;
static bool null_descriptor;
static uint8_t storage[131072],expected[131072];
static uint64_t values[4096],original_values[4096];
BaseFlash EFLD1;
static flash_descriptor_t descriptor;
#define CHECK(x) do{++checks;if(!(x)){fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x);exit(1);}}while(0)

const flash_descriptor_t *flashGetDescriptor(BaseFlash *flash){
    CHECK(flash==&EFLD1);++descriptor_calls;return null_descriptor?NULL:&descriptor;
}
int flashProgram(BaseFlash *flash,uint32_t offset,size_t count,const uint8_t *bytes){
    CHECK(flash==&EFLD1);CHECK(offset>=98304);CHECK(offset+count<=sizeof(storage));
    CHECK(count==8||count==256);CHECK((offset%256)+count<=256);CHECK(bytes!=NULL);
    ++program_calls;if(program_calls==fail_call)return 99;
    // The pinned EFL implementation copies the current page, erases, then programs
    // the whole page. RMW preserves all bytes outside this requested region.
    uint8_t page[256];uint32_t start=offset&~255U;
    memcpy(page,storage+start,sizeof(page));memcpy(page+(offset%256),bytes,count);
    memset(storage+start,0,sizeof(page));memcpy(storage+start,page,sizeof(page));
    return FLASH_NO_ERROR;
}
bool backing_store_write(uint32_t address,backing_store_int_t value){
    ++scalar_calls;if(descriptor.attributes&FLASH_ATTR_ERASED_IS_ONE)value=~value;
    return flashProgram(&EFLD1,98304+address,sizeof(value),(const uint8_t*)&value)==FLASH_NO_ERROR;
}
static void reset(bool invert){
    program_calls=scalar_calls=descriptor_calls=fail_call=0;null_descriptor=false;
    descriptor=(flash_descriptor_t){.attributes=invert?FLASH_ATTR_ERASED_IS_ONE:0,.size=131072,.sectors_size=256,.sectors_count=512,.page_size=8,.sectors=NULL};
    for(size_t i=0;i<sizeof(storage);++i)storage[i]=(uint8_t)(i*29U+17U);
    memcpy(expected,storage,sizeof(storage));
    for(size_t i=0;i<4096;++i)values[i]=UINT64_C(0x93819A764BEF0119)^(UINT64_C(0x0936718273640921)*i);
    memcpy(original_values,values,sizeof(values));
}
static void expect_data(uint32_t address,size_t count,bool invert){
    for(size_t i=0;i<count*8;++i){uint8_t value=((uint8_t*)values)[i];expected[98304+address+i]=invert?(uint8_t)~value:value;}
}
static void valid_ranges(void){
    const size_t counts[]={0,1,2,31,32,33,63,64,65,127,511,2048,4096};
    for(unsigned invert=0;invert<2;++invert){
        for(uint32_t address=0;address<512;address+=8){
            for(size_t c=0;c<sizeof(counts)/sizeof(counts[0]);++c){
                size_t count=counts[c];if(count>(32768-address)/8)continue;
                reset(invert!=0);expect_data(address,count,invert!=0);
                CHECK(backing_store_write_bulk(address,values,count));
                CHECK(memcmp(storage,expected,sizeof(storage))==0);
                CHECK(memcmp(values,original_values,sizeof(values))==0);
                if(count==0)CHECK(descriptor_calls==0&&program_calls==0);
            }
        }
        reset(invert!=0);expect_data(32760,1,invert!=0);CHECK(backing_store_write_bulk(32760,values,1));CHECK(memcmp(storage,expected,sizeof(storage))==0);
    }
    reset(false);expect_data(0,2048,false);CHECK(backing_store_write_bulk(0,values,2048));CHECK(program_calls==64);CHECK(scalar_calls==0);CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(false);expect_data(0,4096,false);CHECK(backing_store_write_bulk(0,values,4096));CHECK(program_calls==128);CHECK(scalar_calls==0);CHECK(memcmp(storage,expected,sizeof(storage))==0);
}
static void rejected_inputs(void){
    reset(false);
    CHECK(backing_store_write_bulk(UINT32_MAX,NULL,0));CHECK(program_calls==0&&descriptor_calls==0);
    CHECK(!backing_store_write_bulk(0,NULL,1));CHECK(!backing_store_write_bulk(1,values,1));
    CHECK(!backing_store_write_bulk(0,(backing_store_int_t*)((uint8_t*)values+1),1));
    CHECK(!backing_store_write_bulk(32768,values,1));CHECK(!backing_store_write_bulk(UINT32_MAX-7,values,1));
    CHECK(!backing_store_write_bulk(32760,values,2));CHECK(!backing_store_write_bulk(0,values,SIZE_MAX));
    CHECK(!backing_store_write_bulk(8,values,4096));CHECK(program_calls==0&&descriptor_calls==0);
    CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(false);null_descriptor=true;CHECK(!backing_store_write_bulk(0,values,32));CHECK(program_calls==0);
    reset(false);descriptor.size=131071;CHECK(!backing_store_write_bulk(0,values,32));CHECK(program_calls==0);
    reset(false);descriptor.sectors_size=128;CHECK(!backing_store_write_bulk(0,values,32));CHECK(program_calls==0);
    reset(false);descriptor.sectors_count=511;CHECK(!backing_store_write_bulk(0,values,32));CHECK(program_calls==0);
    reset(false);descriptor.page_size=4;CHECK(!backing_store_write_bulk(0,values,32));CHECK(program_calls==0);
    reset(false);descriptor.sectors=&descriptor;CHECK(!backing_store_write_bulk(0,values,32));CHECK(program_calls==0);
}
static void failures_abort(void){
    reset(false);fail_call=1;CHECK(!backing_store_write_bulk(0,values,64));CHECK(program_calls==1);CHECK(scalar_calls==0);CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(false);fail_call=2;expect_data(0,32,false);CHECK(!backing_store_write_bulk(0,values,96));CHECK(program_calls==2);CHECK(scalar_calls==0);CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(true);fail_call=2;expect_data(0,32,true);CHECK(!backing_store_write_bulk(0,values,96));CHECK(program_calls==2);CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(false);fail_call=1;CHECK(!backing_store_write_bulk(8,values,64));CHECK(program_calls==1);CHECK(scalar_calls==1);CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(false);fail_call=2;expect_data(248,1,false);CHECK(!backing_store_write_bulk(248,values,65));CHECK(program_calls==2);CHECK(scalar_calls==1);CHECK(memcmp(storage,expected,sizeof(storage))==0);
    reset(false);fail_call=2;expect_data(0,32,false);CHECK(!backing_store_write_bulk(0,values,33));CHECK(program_calls==2);CHECK(scalar_calls==1);CHECK(memcmp(storage,expected,sizeof(storage))==0);
}
int main(void){valid_ranges();rejected_inputs();failures_abort();printf("WB32 actual bulk source: %u offline assertions PASS; 16KiB consolidation =64 page calls vs2048 scalar calls. No hardware.\n",checks);return 0;}
