"""Independent, read-only audit of pinned Glorious originals and normalized references.

All input roots are explicit. Does not import or execute the normalizer, use a
compiler/network/device, or write any file.
"""
import argparse
import copy
import hashlib
import itertools
import json
import math
from pathlib import Path
import re

PINS = {'65': '6ce76ee92f7d83b798147baaed2d3d8ec89564b2',
        '75': '121e1a6341c5c045b396dab8c5e755a4b9a4aefd',
        '100': 'f53deb1cc507412fe4cbb849c89080a1f580886c'}
QMK = '7a1bbf37c5c07da4ea0a162bb139083a46ef40a1'
CATALOG_SHA = '50BB0ABF4FF6B627318F8DF0496C232DAFB29C1A1A5E01D92B6DA4F49CC1AD88'
FACTS = {'65': (66, 85, 20), '75': (81, 101, 20), '100': (105, 125, 20)}
checks = 0


def verify(condition, message):
    global checks
    checks += 1
    if not condition:
        raise ValueError(message)


def sha(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def inside(root, relative):
    verify(isinstance(relative, str) and '\\' not in relative and
           not relative.startswith('/') and '..' not in relative.split('/'),
           'Unsafe evidence path')
    path = (root / relative).resolve()
    verify(path.is_relative_to(root.resolve()), 'Evidence path escaped supplied root')
    return path


def clean(source):
    return re.sub(r'/\*.*?\*/|//[^\r\n]*', '', source, flags=re.S)


def json_source(path):
    # These vendor JSON files contain line comments, but URL strings are data.
    return json.loads(re.sub(r'(?m)^\s*//[^\r\n]*|(?<=\s)//[^\r\n]*', '', path.read_text(encoding='utf-8-sig')))


def balanced(source, marker, opening='{', closing='}'):
    found = source.index(marker)
    start = source.index(opening, found)
    depth = 0
    for end in range(start, len(source)):
        depth += (source[end] == opening) - (source[end] == closing)
        if depth == 0:
            return start, end + 1, source[start:end + 1]
    raise ValueError('Unclosed C initializer')


def c_array(text):
    tokens = re.findall(r'[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[{},]', text)
    verify(re.sub(r'\s+', '', ''.join(tokens)) == re.sub(r'\s+', '', text), 'Unsupported C initializer syntax')
    index = 0

    def read():
        nonlocal index
        verify(tokens[index] == '{', 'Expected C initializer')
        index += 1
        result = []
        while tokens[index] != '}':
            if tokens[index] == '{':
                result.append(read())
            else:
                token = tokens[index]
                result.append(255 if token in ('__', 'NO_LED') else int(token) if token.isdigit() else token)
                index += 1
            if tokens[index] == ',':
                index += 1
            else:
                verify(tokens[index] == '}', 'Missing C array separator')
        index += 1
        return result

    value = read()
    verify(index == len(tokens), 'Trailing initializer data')
    return value


def codes(source):
    source = clean(source)
    _, _, call = balanced(source, 'LAYOUT', '(', ')')
    result, start, depth = [], 1, 0
    for i in range(1, len(call) - 1):
        depth += (call[i] == '(') - (call[i] == ')')
        if call[i] == ',' and depth == 0:
            result.append(re.sub(r'\s+', '', call[start:i])); start = i + 1
    result.append(re.sub(r'\s+', '', call[start:-1]))
    return result


def physical_geometry(keys, base, device):
    """Check occupied key areas, including the actual ISO Enter upper extension.

    QMK's 1.25x2 ISO definition starts at the lower stem; its upper section
    extends 0.25u to the left. Touching boundaries are not intersections.
    """
    shapes = []
    for key, code in zip(keys, base):
        values = [key['x'], key['y'], key.get('w', 1), key.get('h', 1)]
        verify(all(type(v) in (int, float) and math.isfinite(v) for v in values), f'{device}: invalid physical geometry')
        x, y, w, h = values
        verify(0 <= x < 32 and 0 <= y < 16 and 0 < w <= 8 and 0 < h <= 2, f'{device}: physical geometry bounds')
        iso_enter = code == 'KC_ENT' and w == 1.25 and h == 2
        rectangles = [(x - .25, y, x + w, y + 1), (x, y + 1, x + w, y + 2)] if iso_enter else [(x, y, x + w, y + h)]
        verify(not iso_enter or x >= .25, f'{device}: ISO Enter upper extension bounds')
        shapes.append((key, rectangles))
    for (a, ar), (b, br) in itertools.combinations(shapes, 2):
        overlap = any(min(ra[2], rb[2]) > max(ra[0], rb[0]) and
                      min(ra[3], rb[3]) > max(ra[1], rb[1]) for ra in ar for rb in br)
        verify(not overlap, f"{device}: physical key overlap: {a['matrix']} {a['label']} / {b['matrix']} {b['label']}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--reference-root', type=Path, required=True)
    parser.add_argument('--manufacturer-root', type=Path, required=True)
    parser.add_argument('--port-root', type=Path, required=True)
    args = parser.parse_args()
    refs, originals, ports = args.reference_root, args.manufacturer_root, args.port_root
    raw_catalog = (originals / 'sources.json').read_bytes()
    verify(sha(raw_catalog) == CATALOG_SHA, 'Pinned manufacturer catalog SHA256 mismatch')
    catalog = json.loads(raw_catalog)
    verify(catalog['schema'] == 1 and catalog['repository'] == 'https://gitlab.com/glorious-public/qmk_firmware', 'Manufacturer catalog identity')
    expected = {f'p{size}/{suffix}' for size in PINS for suffix in
                (['info.json', 'config.h', 'halconf.h', 'mcuconf.h'] +
                 [f'{layout}/{file}' for layout in ('ansi', 'iso') for file in
                  ('info.json', f'{layout}.c', 'readme.md', 'rules.mk', 'keymaps/default/keymap.c')])}
    verify(len(catalog['files']) == len(expected) == 42, 'Exactly 42 manufacturer inputs required')
    seen = set()
    for entry in catalog['files']:
        prefix = 'reference/glorious-gmmk3/'
        verify(entry['path'].startswith(prefix), 'Wrong manufacturer path prefix')
        rel = entry['path'][len(prefix):]
        verify(rel in expected and rel not in seen, 'Unknown/duplicate manufacturer source'); seen.add(rel)
        pin = PINS[rel.split('/')[0][1:]]
        source_path = 'keyboards/gmmk/gmmk3/' + rel
        verify(entry['commit'] == pin and entry['sourcePath'] == source_path and
               entry['url'] == f'https://gitlab.com/glorious-public/qmk_firmware/-/blob/{pin}/{source_path}', 'Manufacturer commit/URL mismatch')
        raw = inside(originals, rel).read_bytes()
        verify(len(raw) == entry['bytes'] and sha(raw) == entry['sha256'], f'Original source hash mismatch: {rel}')
    provenance = json.loads((ports / 'provenance.json').read_text(encoding='utf-8-sig'))
    verify(provenance['schema'] == 1 and provenance['qmkCommit'] == QMK and
           provenance['originalSources'] == catalog['files'], 'Provenance original source records changed')
    verify(len(provenance['normalizations']) == 6, 'Expected six normalization records')
    manifest = json.loads((refs / 'sources.json').read_text(encoding='utf-8-sig'))
    entries = [e for e in manifest if e['file'].startswith('gmmk3-')]
    records = {e['file']: e for e in entries}
    verify(len(entries) == len(records) == 12, 'Expected twelve unique normalized GMMK3 references')
    results = []
    output_paths = set()
    features_map = {'BOOTMAGIC': 'bootmagic', 'ENCODER': 'encoder', 'MOUSEKEY': 'mousekey',
                    'EXTRAKEY': 'extrakey', 'AUDIO': 'audio', 'CONSOLE': 'console', 'COMMAND': 'command',
                    'NKRO': 'nkro', 'BACKLIGHT': 'backlight', 'RGBLIGHT': 'rgblight',
                    'RGB_MATRIX': 'rgb_matrix', 'DIP_SWITCH': 'dip_switch'}
    for size, pin in PINS.items():
        original_parent = originals / f'p{size}'
        parent_info = json_source(original_parent / 'info.json')
        config = (original_parent / 'config.h').read_text(encoding='utf-8-sig')
        declared = [int(x) for x in re.findall(r'#define\s+DRIVER_[12]_LED_TOTAL\s+\(([0-9]+)\)', config)]
        verify(len(declared) == 2 and sum(declared) == 126 and
               re.search(r'#define\s+RGB_MATRIX_LED_COUNT\s+\(DRIVER_1_LED_TOTAL\s*\+\s*DRIVER_2_LED_TOTAL\)', config), 'Expected documented copied LED count')
        for file in ('config.h', 'halconf.h', 'mcuconf.h'):
            original = (original_parent / file).read_text(encoding='utf-8-sig')
            expected_text = re.sub(r'(?m)^#define (?:DRIVER_[12]_LED_TOTAL|RGB_MATRIX_LED_COUNT).*$\n?', '', original) if file == 'config.h' else original
            verify((ports / f'p{size}' / file).read_text(encoding='utf-8-sig') == expected_text, f'Unexpected board config normalization: p{size}/{file}')
            output_paths.add(f'firmware/ports/gmmk3/p{size}/{file}')
        for layout in ('ansi', 'iso'):
            device = f'gmmk3-{size}-{layout}'
            op = original_parent / layout
            info = json_source(op / 'info.json')
            data = json_source(refs / f'{device}.json')
            verify(json_source(ports / f'p{size}/{layout}/keyboard.json') == data, f'{device}: port/reference mismatch')
            source = (op / f'{layout}.c').read_text(encoding='utf-8-sig')
            source_clean = clean(source)
            _, _, led_text = balanced(source_clean, 'led_config_t g_led_config')
            matrix, xy, flags = c_array(led_text)
            _, _, driver_text = balanced(source_clean, 'g_aw20216s_leds')
            driver = c_array(driver_text)
            keys = info['layouts']['LAYOUT']['layout']
            base = codes((op / 'keymaps/default/keymap.c').read_text(encoding='utf-8-sig'))
            key_count, ansi_leds, side_count = FACTS[size]
            key_count += layout == 'iso'; led_count = ansi_leds + (layout == 'iso')
            verify(len(keys) == len(base) == key_count, f'{device}: physical/default key count')
            verify(len(matrix) == 14 and all(len(row) == 8 for row in matrix), f'{device}: physical matrix dimensions')
            verify(len(driver) == len(xy) == len(flags) == led_count, f'{device}: initialized driver/XY/flags count')
            key_lookup = {tuple(k['matrix']): (k, c) for k, c in zip(keys, base)}
            verify(len(key_lookup) == key_count, f'{device}: duplicate physical keys')
            fixes = []
            expected_info = copy.deepcopy(info)
            if size == '75' and layout == 'iso':
                # Prove both source defects against their original neighbor rows,
                # default key identity and the independently pinned 100% ISO file.
                related_keys = json_source(originals / 'p100/iso/info.json')['layouts']['LAYOUT']['layout']
                related = {tuple(k['matrix']): k for k in related_keys}
                enter, enter_code = key_lookup[(10, 4)]
                hash_key, hash_code = key_lookup[(10, 2)]
                verify(enter_code == 'KC_ENT' and enter == dict(label='Enter', matrix=[10, 4], x=13.75, y=1.25, w=1.25, h=2), f'{device}: original Enter defect evidence')
                verify(hash_code == 'KC_NUHS' and hash_key == dict(label='#', matrix=[10, 2], x=11.25, y=4.25), f'{device}: original hash-key defect evidence')
                verify(key_lookup[(8, 3)][0]['y'] == 3.25 and key_lookup[(6, 1)][0]['y'] == 2.25 and
                       key_lookup[(8, 5)][0]['x'] == 11.25 and key_lookup[(8, 5)][0]['y'] == 4.25, f'{device}: original neighbor-row evidence')
                index = base.index('KC_NUHS')
                verify(base[index - 1:index + 2] == ['KC_QUOT', 'KC_NUHS', 'KC_ENT'], f'{device}: original home-row default order')
                verify(related[(10, 4)] == dict(label='Enter', matrix=[10, 4], x=13.75, y=2.25, w=1.25, h=2) and
                       related[(10, 2)] == dict(label='#', matrix=[10, 2], x=12.75, y=3.25), f'{device}: pinned 100% ISO correction evidence')
                normalized_keys = {tuple(k['matrix']): k for k in expected_info['layouts']['LAYOUT']['layout']}
                normalized_keys[(10, 4)]['y'] = 2.25
                normalized_keys[(10, 2)]['x'], normalized_keys[(10, 2)]['y'] = 12.75, 3.25
                fixes.append('Layout Enter matrix[10][4]: y 1.25 -> 2.25; p75 Tab/bracket row y2.25 and home row y3.25, matching pinned p100 ISO Enter geometry at the same matrix position.')
                fixes.append('Layout # matrix[10][2]: XY(11.25,4.25) -> (12.75,3.25); own default KC_QUOT,KC_NUHS,KC_ENT order and pinned p100 ISO geometry at the same matrix position; removes duplicate Slash rectangle.')
            physical_geometry(data['layouts']['LAYOUT']['layout'], base, device)
            if size == '65':
                verify(matrix[8][7] == 0 and key_lookup[(8, 7)][0]['label'] == '0' and
                       key_lookup[(8, 7)][1] == 'KC_0' and xy[10] == [150, 0] and flags[10] == 4 and
                       re.search(r'//\s*10\s+0\s*$', source, re.M), f'{device}: zero-key correction evidence')
                matrix[8][7] = 10
                fixes.append('Matrix[8][7]: LED 0 -> 10; physical/default key KC_0, driver comment 10=0, XY(150,0), flags4.')
                if layout == 'iso':
                    verify(matrix[3][4] == 56 and key_lookup[(3, 4)][0]['label'] == 'C' and
                           key_lookup[(3, 4)][1] == 'KC_C' and xy[46] == [63, 48] and flags[46] == 4 and
                           re.search(r'//\s*46\s+C\s*$', source, re.M), f'{device}: C-key correction evidence')
                    matrix[3][4] = 46
                    fixes.append('Matrix[3][4]: LED 56 -> 46; physical/default key KC_C, driver comment 46=C, XY(63,48), flags4.')
            mapped = {}
            for row, cells in enumerate(matrix):
                for col, led in enumerate(cells):
                    if led == 255:
                        continue
                    verify((row, col) in key_lookup and 0 <= led < led_count and led not in mapped, f'{device}: matrix-to-LED assignment')
                    mapped[led] = [row, col]
            verify(set(mapped) == {i for i, flag in enumerate(flags) if flag == 4}, f'{device}: key LED coverage')
            verify([k['matrix'] for k in keys if matrix[k['matrix'][0]][k['matrix'][1]] == 255] == [[11, 6]], f'{device}: only knob is unlit')
            electrical = set()
            for led in driver:
                verify(len(led) == 4 and led[0] in (0, 1), f'{device}: AW20216S driver address')
                for channel in led[1:]:
                    verify(isinstance(channel, str) and re.fullmatch(r'SW(?:[1-9]|1[0-2])_CS(?:[1-9]|1[0-8])', channel) and
                           (led[0], channel) not in electrical, f'{device}: invalid/duplicate electrical channel')
                    electrical.add((led[0], channel))
            expected_leds = []
            for i, (point, flag) in enumerate(zip(xy, flags)):
                verify(len(point) == 2 and all(isinstance(x, int) for x in point) and 0 <= point[0] <= 224 and 0 <= point[1] <= 64 and flag in (2, 4, 8), f'{device}: LED position/flags')
                expected_leds.append(dict(x=point[0], y=point[1], flags=flag, **({'matrix': mapped[i]} if i in mapped else {})))
            verify(data['rgb_matrix']['layout'] == expected_leds, f'{device}: normalized LED map differs from corrected primary arrays')
            sides = [i for i, f in enumerate(flags) if f == 2]
            badge = [i for i, f in enumerate(flags) if f == 8]
            expected_badges = 0 if size == '65' else 1
            verify(len(sides) == side_count and len(badge) == expected_badges, f'{device}: side/badge counts')
            left = [i for i in sides if xy[i][0] == 0]; right = [i for i in sides if xy[i][0] == 224]
            verify(len(left) == len(right) == side_count // 2 and sorted(xy[i][1] for i in left) == sorted(xy[i][1] for i in right), f'{device}: paired side geometry')
            expected_info['keyboard_name'] = f'GMMK3 {size}% {layout.upper()}'
            rules = (op / 'rules.mk').read_text(encoding='utf-8-sig')
            expected_info['features'] = {features_map[name]: value == 'yes' for name, value in re.findall(r'(?m)^([A-Z_]+)_ENABLE\s*=\s*(yes|no)', rules)}
            expected_info['features']['dip_switch'] = False
            expected_info['rgb_matrix'] = dict(parent_info['rgb_matrix'], layout=expected_leds)
            verify(data == expected_info, f'{device}: unapproved source JSON normalization')
            verify(data['processor'] == 'WB32FQ95' and data['bootloader'] == 'wb32-dfu' and len(data['matrix_pins']['rows']) == 14 and len(data['matrix_pins']['cols']) == 8, f'{device}: MCU/boot/matrix definition')
            canonical = {'QK_GESC': 'KC_ESC', 'KC_LWIN': 'KC_LGUI', 'MO(WIN_FL)': 'MO(1)'}
            expected_codes = [canonical.get(c, c) for c in base]
            verify(codes((refs / f'{device}-default.c').read_text(encoding='utf-8-sig')) == expected_codes, f'{device}: normalized defaults differ from manufacturer Windows layer')
            expected_default = '#include QMK_KEYBOARD_H\nconst uint16_t PROGMEM keymaps[4][MATRIX_ROWS][MATRIX_COLS] = {[0] = LAYOUT(' + ','.join(expected_codes) + ')};'
            verify(re.sub(r'\s+', '', clean((refs / f'{device}-default.c').read_text(encoding='utf-8-sig'))) == re.sub(r'\s+', '', expected_default), f'{device}: unexpected additional default code')
            verify((refs / f'{device}-default.c').read_bytes() == (ports / f'p{size}/{layout}/keymaps/default/keymap.c').read_bytes(), f'{device}: port default byte mismatch')
            port_source = (ports / f'p{size}/{layout}/{layout}.c').read_text(encoding='utf-8-sig')
            _, _, port_driver = balanced(clean(port_source), 'g_aw20216s_leds')
            verify(c_array(port_driver) == driver, f'{device}: electrical RGB table changed')
            a, b, _ = balanced(source, 'led_config_t g_led_config')
            del a
            without_led = source[:source.index('led_config_t g_led_config')] + source[source.index(';', b) + 1:]
            verify(without_led.count('#ifdef EEPROM_ENABLE') == 1 and re.findall(r'(?m)^void ([a-z_]+)\(', without_led.split('#ifdef EEPROM_ENABLE')[1]) == ['spi_init'], f'{device}: unexpected discarded vendor code')
            expected_c = without_led.split('#ifdef EEPROM_ENABLE')[0]
            verify(re.sub(r'\s+', '', clean(port_source)) == re.sub(r'\s+', '', clean(expected_c)), f'{device}: unexpected C driver normalization')
            norm = [n for n in provenance['normalizations'] if n['deviceId'] == device]
            verify(len(norm) == 1 and norm[0]['manufacturerCommit'] == pin and norm[0]['declaredLeds'] == 126 and norm[0]['activeLeds'] == led_count and norm[0]['corrections'] == fixes, f'{device}: normalization provenance mismatch')
            for suffix in ('.json', '-default.c'):
                name = device + suffix; entry = records[name]; raw = (refs / name).read_bytes()
                verify(entry['qmkCommit'] == QMK and entry['manufacturerCommit'] == pin and entry['normalization'] == 'firmware/ports/gmmk3/provenance.json' and entry['url'] == f'https://gitlab.com/glorious-public/qmk_firmware/-/tree/{pin}/keyboards/gmmk/gmmk3/p{size}/{layout}', f'{device}: normalized source provenance')
                verify(entry['bytes'] == len(raw) and entry['sha256'] == sha(raw), f'{device}: normalized manifest hash')
                output_paths.add('reference/qmk/' + name)
            output_paths.update(f'firmware/ports/gmmk3/p{size}/{layout}/{file}' for file in ('keyboard.json', 'keymaps/default/keymap.c', f'{layout}.c'))
            results.append(dict(id=device, layout='LAYOUT', matrixRows=14, matrixColumns=8, physicalKeys=key_count,
                                leds=led_count, sideLeds=len(sides), badgeLeds=badge, leftSideLedIndices=left,
                                rightSideLedIndices=right, encoderButtons=1, manufacturerSourceVerified=True))
    verify(len(provenance['outputs']) == len(output_paths) == 39, 'Normalized output catalog count')
    seen = set()
    for entry in provenance['outputs']:
        path = entry['path']
        verify(path in output_paths and path not in seen, 'Unexpected/duplicate normalization output'); seen.add(path)
        root, rel = (refs, path[len('reference/qmk/'):]) if path.startswith('reference/qmk/') else (ports, path[len('firmware/ports/gmmk3/'):])
        raw = inside(root, rel).read_bytes()
        verify(len(raw) == entry['bytes'] and sha(raw) == entry['sha256'], 'Normalized output provenance hash mismatch: ' + path)
    print(json.dumps(dict(result='PASS', checks=checks, originalFiles=42, normalizedFiles=39,
                          manufacturerCatalogSha256=CATALOG_SHA, provenanceSha256=sha((ports / 'provenance.json').read_bytes()),
                          hardwareAccess=False, models=results)))


if __name__ == '__main__':
    main()
