"""Normalize six pinned manufacturer board definitions for the current QMK tree.

No hardware I/O or network access. Original files are copied byte-for-byte from
the separate local manufacturer clone, and are never replaced if they differ.
"""
from pathlib import Path
import ast
import hashlib
import json
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
REPO = ROOT / 'firmware/reference-forks/gmmk3'
PORTS = ROOT / 'firmware/ports/gmmk3'
ORIGINAL = ROOT / 'reference/glorious-gmmk3'
CHECK_ONLY = '--check' in sys.argv[1:]
if any(arg != '--check' for arg in sys.argv[1:]):
    raise ValueError('Only --check is supported')
ORIGINAL_MANIFEST_SHA256 = '50BB0ABF4FF6B627318F8DF0496C232DAFB29C1A1A5E01D92B6DA4F49CC1AD88'
QMK_COMMIT = '7a1bbf37c5c07da4ea0a162bb139083a46ef40a1'
PINS = {'65':'6ce76ee92f7d83b798147baaed2d3d8ec89564b2',
        '75':'121e1a6341c5c045b396dab8c5e755a4b9a4aefd',
        '100':'f53deb1cc507412fe4cbb849c89080a1f580886c'}
source_records, output_records, normalizations = [], [], []
original_manifest = ORIGINAL/'sources.json'
if original_manifest.exists():
    raw_manifest = original_manifest.read_bytes()
    if hashlib.sha256(raw_manifest).hexdigest().upper() != ORIGINAL_MANIFEST_SHA256:
        raise ValueError('The pinned original-source catalog changed')
    original_catalog = {r['sourcePath']:r for r in json.loads(raw_manifest)['files']}
elif CHECK_ONLY:
    raise ValueError('Pinned original sources are missing')
else:
    original_catalog = {}

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def primary(size, file):
    path = f'keyboards/gmmk/gmmk3/p{size}/{file}'
    target = ORIGINAL / f'p{size}' / file
    if original_catalog:
        expected=original_catalog[path]
        data=target.read_bytes()
        if expected['commit']!=PINS[size] or expected['sha256']!=sha(data) or expected['bytes']!=len(data):
            raise ValueError(f'Original source integrity mismatch: {path}')
    else:
        data = subprocess.check_output(['git','-C',str(REPO),'show',f'{PINS[size]}:{path}'])
    if not CHECK_ONLY:
        target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        if target.read_bytes() != data:
            raise ValueError(f'Existing original changed: {target}')
    elif not CHECK_ONLY:
        target.write_bytes(data)
    else:
        raise ValueError(f'Original source missing: {target}')
    source_records.append(dict(path=target.relative_to(ROOT).as_posix(), sourcePath=path,
        commit=PINS[size], bytes=len(data), sha256=sha(data),
        url=f'https://gitlab.com/glorious-public/qmk_firmware/-/blob/{PINS[size]}/{path}'))
    return data.decode('utf-8')

def write(path, data):
    if isinstance(data, str):
        data = data.encode('utf-8')
    if CHECK_ONLY:
        if not path.exists() or path.read_bytes()!=data:
            raise ValueError(f'Normalized output differs from its pinned source derivation: {path}')
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
        if not path.exists() or path.read_bytes() != data:
            path.write_bytes(data)
    output_records.append(dict(path=path.relative_to(ROOT).as_posix(), bytes=len(data), sha256=sha(data)))

def json_text(value):
    return json.dumps(value, ensure_ascii=False, indent=2)+'\n'

def parse_json(text):
    return json.loads(re.sub(r'(?m)(?<=\s)//[^\n]*','',text))

def clean(text):
    return re.sub(r'/\*.*?\*/|//[^\n]*','',text,flags=re.S)

def bounds(text, token):
    start = text.index('{',text.index(token)); end, depth = start+1, 1
    while depth:
        depth += (text[end]=='{')-(text[end]=='}'); end += 1
    return start,end

def children(text):
    result,depth,start=[],0,0
    for i,c in enumerate(text[1:-1]):
        if c=='{':
            if depth==0:start=i+1
            depth+=1
        elif c=='}':
            depth-=1
            if depth==0:result.append(text[start:i+2])
    return result

def base_codes(text):
    text=clean(text); m=re.search(r'\bLAYOUT\s*\(',text)
    start=m.end(); end,depth=start,1
    while depth:
        depth+=(text[end]=='(')-(text[end]==')');end+=1
    return [x.strip() for x in text[start:end-1].split(',')]

references = json.loads((ROOT/'reference/qmk/sources.json').read_text(encoding='utf-8-sig'))
references = [r for r in references if not r['file'].startswith('gmmk3-')]
for size,commit in PINS.items():
    parent_info=parse_json(primary(size,'info.json'))
    config=primary(size,'config.h')
    # Counts in the parent were copied from p100 ISO into all six variants.
    config=re.sub(r'(?m)^#define (?:DRIVER_[12]_LED_TOTAL|RGB_MATRIX_LED_COUNT).*$\n?', '', config)
    write(PORTS/f'p{size}/config.h', config)
    for file in ('halconf.h','mcuconf.h'):
        write(PORTS/f'p{size}'/file,primary(size,file))
    for layout in ('ansi','iso'):
        device=f'gmmk3-{size}-{layout}'
        prefix=f'{layout}/'
        info=parse_json(primary(size,prefix+'info.json'))
        source=primary(size,prefix+layout+'.c')
        primary(size,prefix+'readme.md')
        rules=primary(size,prefix+'rules.mk')
        default=primary(size,prefix+'keymaps/default/keymap.c')
        text=clean(source); start,end=bounds(text,'led_config_t g_led_config')
        matrix,points,flags=[ast.literal_eval(x.replace('__','255').replace('{','[').replace('}',']')) for x in children(text[start:end])]
        a,b=bounds(text,'g_aw20216s_leds');drivers=children(text[a:b])
        assert len(drivers)==len(points)==len(flags)
        assert len(set(re.sub(r'\s','',x) for x in drivers))==len(drivers), 'Duplicate electrical LED channels'
        keys=info['layouts']['LAYOUT']['layout'];codes=base_codes(default)
        assert len(codes)==len(keys)
        mapped={tuple(k['matrix']):(k,c) for k,c in zip(keys,codes)}
        fixes=[]
        if size=='75' and layout=='iso':
            # The manufacturer's p75 ISO JSON misplaced two key rectangles.
            # Its keymap orders KC_NUHS between KC_QUOT and KC_ENT; the p100
            # ISO source uses the same matrix coordinates and row offsets.
            related_path='keyboards/gmmk/gmmk3/p100/iso/info.json'
            if original_catalog:
                related_record=original_catalog[related_path]
                related_data=(ROOT/related_record['path']).read_bytes()
                assert related_record['commit']==PINS['100'] and sha(related_data)==related_record['sha256'] and len(related_data)==related_record['bytes']
            else:
                related_data=subprocess.check_output(['git','-C',str(REPO),'show',f"{PINS['100']}:{related_path}"])
            related={tuple(k['matrix']):k for k in parse_json(related_data.decode('utf-8'))['layouts']['LAYOUT']['layout']}
            enter,enter_code=mapped[(10,4)];hash_key,hash_code=mapped[(10,2)]
            assert enter_code=='KC_ENT' and enter==dict(label='Enter',matrix=[10,4],x=13.75,y=1.25,w=1.25,h=2)
            assert hash_code=='KC_NUHS' and hash_key==dict(label='#',matrix=[10,2],x=11.25,y=4.25)
            assert mapped[(8,3)][0]['y']==3.25 and mapped[(6,1)][0]['y']==2.25
            assert mapped[(8,5)][0]['x']==11.25 and mapped[(8,5)][0]['y']==4.25
            index=codes.index('KC_NUHS');assert codes[index-1:index+2]==['KC_QUOT','KC_NUHS','KC_ENT']
            assert related[(10,4)]==dict(label='Enter',matrix=[10,4],x=13.75,y=2.25,w=1.25,h=2)
            assert related[(10,2)]==dict(label='#',matrix=[10,2],x=12.75,y=3.25)
            enter['y']=2.25
            hash_key['x']=12.75;hash_key['y']=3.25
            fixes.append('Layout Enter matrix[10][4]: y 1.25 -> 2.25; p75 Tab/bracket row y2.25 and home row y3.25, matching pinned p100 ISO Enter geometry at the same matrix position.')
            fixes.append('Layout # matrix[10][2]: XY(11.25,4.25) -> (12.75,3.25); own default KC_QUOT,KC_NUHS,KC_ENT order and pinned p100 ISO geometry at the same matrix position; removes duplicate Slash rectangle.')
        if size=='65':
            key,code=mapped[(8,7)]
            assert code=='KC_0' and key['label']=='0' and matrix[8][7]==0
            assert points[10]==[150,0] and flags[10]==4
            assert re.search(r'//\s*10\s+0\s*$',source,re.M)
            matrix[8][7]=10
            fixes.append('Matrix[8][7]: LED 0 -> 10; physical/default key KC_0, driver comment 10=0, XY(150,0), flags4.')
            if layout=='iso':
                key,code=mapped[(3,4)]
                assert code=='KC_C' and key['label']=='C' and matrix[3][4]==56
                assert points[46]==[63,48] and flags[46]==4
                assert re.search(r'//\s*46\s+C\s*$',source,re.M)
                matrix[3][4]=46
                fixes.append('Matrix[3][4]: LED 56 -> 46; physical/default key KC_C, driver comment 46=C, XY(63,48), flags4.')
        used={}
        for row,cells in enumerate(matrix):
            for col,led in enumerate(cells):
                if led!=255:
                    assert (row,col) in mapped and led not in used and 0<=led<len(points)
                    used[led]=[row,col]
        assert set(used)=={i for i,f in enumerate(flags) if f==4}, 'Every key LED must map exactly once'
        assert [k['matrix'] for k in keys if matrix[k['matrix'][0]][k['matrix'][1]]==255]==[[11,6]]
        leds=[]
        for i,(point,flag) in enumerate(zip(points,flags)):
            led=dict(x=point[0],y=point[1],flags=flag)
            if i in used:led=dict(matrix=used[i],**led)
            leds.append(led)
        info['keyboard_name']=f'GMMK3 {size}% {layout.upper()}'
        info['features']={}
        feature_names={'BOOTMAGIC':'bootmagic','ENCODER':'encoder','MOUSEKEY':'mousekey','EXTRAKEY':'extrakey','AUDIO':'audio','CONSOLE':'console','COMMAND':'command','NKRO':'nkro','BACKLIGHT':'backlight','RGBLIGHT':'rgblight','RGB_MATRIX':'rgb_matrix','DIP_SWITCH':'dip_switch'}
        for name,value in re.findall(r'(?m)^([A-Z_]+)_ENABLE\s*=\s*(yes|no)',rules):
            info['features'][feature_names[name]]=value=='yes'
        # INGLORIOUS owns four user layers; vendor OS-switch defaults must not silently select layer2.
        info['features']['dip_switch']=False
        info['rgb_matrix']=dict(parent_info['rgb_matrix'],layout=leds)
        json_bytes=json_text(info)
        write(PORTS/f'p{size}/{layout}/keyboard.json',json_bytes)
        write(ROOT/f'reference/qmk/{device}.json',json_bytes)
        canonical={'QK_GESC':'KC_ESC','MO(WIN_FL)':'MO(1)','KC_LWIN':'KC_LGUI'}
        codes=[canonical.get(code,code) for code in codes]
        default_normalized='// SPDX-License-Identifier: GPL-2.0-or-later\n// Windows base from pinned Glorious manufacturer source; original preserved separately.\n#include QMK_KEYBOARD_H\nconst uint16_t PROGMEM keymaps[4][MATRIX_ROWS][MATRIX_COLS] = {\n [0] = LAYOUT(\n  '+', '.join(codes)+')\n};\n'
        write(PORTS/f'p{size}/{layout}/keymaps/default/keymap.c',default_normalized)
        write(ROOT/f'reference/qmk/{device}-default.c',default_normalized)
        # Modern QMK generates g_led_config from the normalized JSON. Preserve the electrical driver table and SPI override.
        start,end=bounds(source,'led_config_t g_led_config')
        declaration=source.index('led_config_t g_led_config')
        assert source[end:].lstrip().startswith(';')
        semi=source.index(';',end)
        source=source[:declaration]+'// LED matrix, XY and flags generated from keyboard.json.\n'+source[semi+1:]
        # Current pinned QMK's WB32 compatibility path already applies the exact
        # SCK CURRENT_LEVEL3 flags. Its SPI init also owns the modern private state.
        assert source.count('#ifdef EEPROM_ENABLE')==1
        spi_tail=source.split('#ifdef EEPROM_ENABLE',1)[1]
        assert re.findall(r'(?m)^void ([a-z_]+)\(',spi_tail)==['spi_init']
        source=source.split('#ifdef EEPROM_ENABLE',1)[0]+'// Modern QMK supplies WB32 SCK current-level 3 in chibios_config.h.\n'
        write(PORTS/f'p{size}/{layout}/{layout}.c',source)
        record=dict(deviceId=device,manufacturerCommit=commit,declaredLeds=126,activeLeds=len(leds),
            corrections=fixes,notes=['LED count is the common length of initialized driver, XY and flag arrays.',
                'Knob press at matrix[11][6] has no LED.', 'No stock USB identity or PRO/wireless/HE compatibility is asserted.',
                'Vendor OS DIP layer switching is disabled; INGLORIOUS user layers remain authoritative.',
                'Obsolete spi_init override omitted: pinned modern QMK already applies identical WB32 SPI_SCK_FLAGS with PAL_WB32_CURRENT_LEVEL3 and owns its private SPI state.'])
        normalizations.append(record)
        for suffix in ('.json','-default.c'):
            file=device+suffix; data=(ROOT/'reference/qmk'/file).read_bytes()
            references.append(dict(file=file,url=f'https://gitlab.com/glorious-public/qmk_firmware/-/tree/{commit}/keyboards/gmmk/gmmk3/p{size}/{layout}',qmkCommit=QMK_COMMIT,
                manufacturerCommit=commit,normalization='firmware/ports/gmmk3/provenance.json',retrievedOn='2026-10-11',
                bytes=len(data),sha256=sha(data),purpose='Normalized manufacturer geometry/default keys; not a hardware compatibility approval'))

catalog=json_text(dict(schema=1,repository='https://gitlab.com/glorious-public/qmk_firmware',files=source_records))
assert sha(catalog.replace('\n','\r\n').encode())==ORIGINAL_MANIFEST_SHA256
for path,content in ((ORIGINAL/'sources.json',catalog),(ROOT/'reference/qmk/sources.json',json_text(references)),
        (PORTS/'provenance.json',json_text(dict(schema=1,qmkCommit=QMK_COMMIT,originalSources=source_records,normalizations=normalizations,outputs=output_records)))):
    if CHECK_ONLY:
        if path.read_text(encoding='utf-8')!=content:raise ValueError(f'Catalog derivation mismatch: {path}')
    elif path==ORIGINAL/'sources.json':path.write_bytes(content.replace('\n','\r\n').encode())
    else:path.write_text(content,encoding='utf-8')
print(json.dumps(dict(targets=len(normalizations),originalFiles=len(source_records),normalizedFiles=len(output_records),checkOnly=CHECK_ONLY,hardwareAccess=False)))
