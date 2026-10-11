"""Read-only ARM artifact audit. No device, compiler, subprocess or network access.

Usage: python firmware/Verify-CandidateBuild.py firmware/build-candidates
Stdout is a JSON report; redirect it to keep independent verification evidence.
Compiler frame sizes are not a measurement of complete runtime stack usage.
"""
from pathlib import Path
import hashlib
import json
import re
import struct
import sys

ROOT = Path(__file__).resolve().parent
build = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else ROOT / 'build-candidates'
load = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
targets = load(ROOT / 'targets.json')
manifest = load(build / 'manifest.json')
checks = 0

def verify(condition, description):
    global checks
    if not condition:
        raise ValueError(description)
    checks += 1

def safe_file(base, relative):
    p = base / relative
    verify(not Path(relative).is_absolute() and '..' not in Path(relative).parts, 'Unsafe artifact path')
    verify(p.resolve().is_relative_to(base.resolve()), 'Artifact escapes its directory')
    verify(p.is_file() and not p.is_symlink(), 'Artifact missing or symbolic')
    return p

def symbol_address(text, name):
    matches = re.findall(r'^\s*(0x[0-9a-fA-F]+)\s+' + re.escape(name) + r'(?:\s*=.*)?\s*$', text, re.M)
    verify(len(matches) == 1, f'Expected one linked symbol: {name}')
    return int(matches[0], 16)

verify(targets['schema'] == 1 and len(targets['targets']) == 15, 'Expected fifteen target definitions')
verify([t['boardId'] for t in targets['targets']] == list(range(1, 16)), 'Board IDs changed')
verify([t['runtimeProductId'] for t in targets['targets']] == [f'1C{i:02X}' for i in range(1, 16)], 'Runtime PIDs changed')
verify([t['verifiedStockProductId'] for t in targets['targets']] == ['5046', '5088'] + [None] * 13, 'New stock hardware must remain unapproved')
verify(manifest['targetManifestSha256'] == sha(ROOT / 'targets.json'), 'Target manifest changed after build')
verify(manifest['qmkCommit'] == targets['qmkCommit'], 'QMK commit mismatch')
verify(manifest['hardwareFlashed'] is False and manifest['hardwareAcceptanceTested'] is False, 'Offline provenance missing')
verify(manifest['targets'] == [t['deviceId'] for t in targets['targets']], 'Manifest does not contain all fifteen builds')
provenance_path = ROOT/'ports/gmmk3/provenance.json'
original_catalog_path = ROOT.parent/'reference/glorious-gmmk3/sources.json'
verify(manifest['manufacturerProvenanceSha256'] == sha(provenance_path), 'Normalized manufacturer provenance changed')
verify(manifest['manufacturerSourcesCatalogSha256'] == sha(original_catalog_path) == '50BB0ABF4FF6B627318F8DF0496C232DAFB29C1A1A5E01D92B6DA4F49CC1AD88', 'Pinned manufacturer source catalog changed')
provenance = load(provenance_path)
for record in provenance['originalSources'] + provenance['outputs']:
    path = safe_file(ROOT.parent, record['path'])
    verify(path.stat().st_size == record['bytes'] and sha(path) == record['sha256'], 'Manufacturer input/output integrity')
verify(provenance['originalSources'] == load(original_catalog_path)['files'], 'Original source catalog differs')
for entry in manifest['artifacts']:
    p = safe_file(build, entry['path'])
    verify(p.stat().st_size == entry['bytes'] and sha(p) == entry['sha256'], f'Artifact changed: {p.name}')
for entry in manifest['generatedSources']:
    p = safe_file(ROOT, entry['path'])
    verify(p.stat().st_size == entry['bytes'] and sha(p) == entry['sha256'], f'Generated source changed: {entry["path"]}')
for entry in manifest['sourceSnapshot']:
    verify(sha(safe_file(ROOT, entry['path'])) == entry['sha256'], f'Input snapshot changed: {entry["path"]}')
for key, relative in {
    'commonSourceSha256': 'inglorious/inglorious.c', 'versionHeaderSha256': 'inglorious/ig_version.h',
    'effectsSourceSha256': 'inglorious/ig_effects.h', 'colorOutputHeaderSha256': 'inglorious/ig_color.h',
    'timingSourceSha256': 'inglorious/ig_timing.c', 'timingHeaderSha256': 'inglorious/ig_timing.h',
    'wb32BulkSourceSha256': 'inglorious/wb32_bulk.c', 'storageHeaderSha256': 'inglorious/ig_storage.h',
    'storageImplementationSha256': 'inglorious/ig_storage.inc', 'storagePatchScriptSha256': 'Patch-QmkStorage.ps1',
    'wearLevelingSourceSha256': 'qmk_firmware/quantum/wear_leveling/wear_leveling.c',
    'descriptorSourceSha256': 'qmk_firmware/tmk_core/protocol/usb_descriptor.c',
    'descriptorPatchScriptSha256': 'Patch-QmkTiming.ps1',
}.items():
    verify(manifest[key] == sha(ROOT / relative), f'Build/source mismatch: {relative}')

results = []
for target in targets['targets']:
    name = target['name']
    binary = (build / (name + '.bin')).read_bytes()
    map_text = (build / (name + '.map')).read_text(encoding='utf-8-sig')
    log = (build / (name + '-build.log')).read_text(encoding='utf-8-sig')
    stack_text = (build / (name + '-stack-usage.txt')).read_text(encoding='utf-8-sig')
    limit = target['flashBytes'] - targets['storageBytes']
    verify(1024 <= len(binary) <= limit, f'{name}: code enters trailing storage')
    sp, pc = struct.unpack_from('<II', binary)
    verify(0x20000000 <= sp <= 0x20000000 + target['ramBytes'] and sp % 4 == 0, f'{name}: stack vector')
    verify(pc & 1 and 0x08000000 <= (pc & ~1) < 0x08000000 + len(binary), f'{name}: reset vector')
    flash = re.search(r'^flash0\s+0x0*8000000\s+(0x[0-9a-f]+)', map_text, re.M)
    verify(flash is not None and int(flash[1], 16) == limit, f'{name}: linker storage reservation')
    verify(not re.search(r'\b(?:warning|error):', log, re.I), f'{name}: compiler warning/error')
    addresses = {s: symbol_address(map_text, s) for s in [
        '__heap_base__', '__heap_end__', '__main_stack_base__', '__main_stack_end__',
        '__process_stack_base__', '__process_stack_end__']}
    heap = addresses['__heap_end__'] - addresses['__heap_base__']
    main_stack = addresses['__main_stack_end__'] - addresses['__main_stack_base__']
    process_stack = addresses['__process_stack_end__'] - addresses['__process_stack_base__']
    verify(heap >= 1024 and main_stack >= 1024 and process_stack >= 2048, f'{name}: insufficient static RAM/stack reservation')
    verify(addresses['__heap_end__'] == 0x20000000 + target['ramBytes'], f'{name}: RAM extent')
    frames = [(int(m[1]), m[0], m[2]) for m in re.findall(r'^([^#\r\n]+)\t(\d+)\t([^\r\n]+)', stack_text, re.M)]
    verify(bool(frames), f'{name}: no compiler frame-size evidence')
    verify(any('inglorious.c:' in f[1] and 'raw_hid_receive' in f[1] for f in frames), f'{name}: missing runtime stack evidence')
    maximum_frame = max(frames)
    verify(maximum_frame[0] < process_stack, f'{name}: individual stack frame exceeds process stack')
    # A pointer into the inactive cache slot must replace the former complete staging copy.
    staging = re.search(r'\.bss\.staging\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)', map_text)
    verify(staging is None or int(staging[2], 16) <= 4, f'{name}: full RAM staging buffer remains')
    verify('ig_storage_stage_begin' in map_text, f'{name}: cache staging API absent')
    verify('spi_flash.o' not in map_text and not re.search(r'\bflash_(?:init|write_range|erase_range)\b', map_text), f'{name}: external SPI flash driver linked')
    if target['mcu'].startswith('WB32'):
        verify(re.search(r'backing_store_write_bulk\s+.*keymaps/inglorious/wb32_bulk.o', map_text) is not None, f'{name}: bulk writer is not the explicit override')
    if target['mcu'] == 'WB32FQ95':
        verify('WB32FQ95xx/hal_efl_lld.o' in map_text and 'WB32F3G71xx/hal_efl_lld.o' not in map_text, f'{name}: wrong MCU flash driver')
        verify('dip_switch.o' not in map_text, f'{name}: unreviewed OS-switch behavior linked')
    def flash_bytes(symbol, count):
        offset = symbol_address(map_text, symbol) - 0x08000000
        verify(0 <= offset <= len(binary) - count, f'{name}: {symbol} outside ROM')
        return binary[offset:offset+count]
    def flash_constant(section):
        matches = re.findall(r'^\s*\.rodata\.' + re.escape(section) + r'\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)', map_text, re.M)
        verify(len(matches) == 1, f'{name}: missing constant {section}')
        offset, count = int(matches[0][0], 16) - 0x08000000, int(matches[0][1], 16)
        verify(0 <= offset <= len(binary)-count, f'{name}: constant range {section}')
        return binary[offset:offset+count]
    keyboard_path = ROOT / 'qmk_firmware/keyboards' / target['qmk'] / 'keyboard.json'
    reference_path = ROOT.parent / 'reference/qmk' / (target['deviceId'] + '.json')
    verify(keyboard_path.read_bytes() == reference_path.read_bytes(), f'{name}: primary reference differs from build input')
    keyboard = load(reference_path)
    physical = bytearray(target['rows'] * target['cols'])
    for key in keyboard['layouts'][target['layout']]['layout']:
        physical[key['matrix'][0] * target['cols'] + key['matrix'][1]] = 1
    verify(flash_constant('ig_physical') == physical, f'{name}: binary physical-key mask differs from QMK')
    header = (ROOT / 'keymaps' / name / 'ig_board.h').read_text()
    test_keys = re.search(r'^#define IG_LIGHT_TEST_KEYS (.+)$', header, re.M)
    verify(test_keys is not None, f'{name}: missing generated lighting positions')
    positions = bytes(map(int, re.findall(r'\d+', test_keys[1])))
    verify(len(positions) == 20 and flash_constant('light_test_keys') == positions, f'{name}: actual test positions differ from validated generated header')
    if target['boardId'] > 2:
        fixture_root = ROOT.parent / ('artifacts/gmmk3-port-fixtures' if target['boardId'] >= 10 else 'artifacts/port-fixtures')
        fixture_name = target['deviceId'] + '-default.bin'
        entries = [e for e in load(fixture_root/'manifest.json') if e['file'] == fixture_name]
        verify(len(entries) == 1, f'{name}: missing C# default fixture record')
        fixture_path = safe_file(fixture_root, fixture_name)
        verify(sha(fixture_path) == entries[0]['sha256'], f'{name}: C# fixture hash')
        cells_bytes = target['rows'] * target['cols'] * 2
        expected = fixture_path.read_bytes()[32:32+cells_bytes]
        verify(len(expected) == cells_bytes and flash_bytes('keymaps', cells_bytes) == expected,
               f'{name}: compiled base keymap differs from the C# default configuration')
    # g_led_config lives in initialized RAM. Resolve its *load* address to compare
    # the actual BIN initializers; the RAM symbol address is not a BIN offset.
    data = re.search(r'^\.data\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)\s+load address (0x[0-9a-f]+)', map_text, re.M)
    verify(data is not None, f'{name}: initialized RAM load mapping absent')
    data_vma, data_size, data_lma = (int(part, 16) for part in data.groups())
    led_address = symbol_address(map_text, 'g_led_config')
    matrix = bytearray([255]) * (target['rows'] * target['cols'])
    points, flags = bytearray(), bytearray()
    for i, led in enumerate(keyboard['rgb_matrix']['layout']):
        if 'matrix' in led:
            matrix[led['matrix'][0] * target['cols'] + led['matrix'][1]] = i
        points.extend((led['x'], led['y']))
        flags.append(led['flags'])
    expected_led = matrix + points + flags
    verify(data_vma <= led_address <= data_vma + data_size - len(expected_led), f'{name}: LED config outside initialized data')
    led_offset = data_lma - 0x08000000 + led_address - data_vma
    verify(binary[led_offset:led_offset+len(expected_led)] == expected_led,
           f'{name}: compiled LED matrix, coordinates or flags differ from primary QMK data')
    if target['boardId'] >= 10:
        # Prove the actual electrical driver initializer survived the old-fork
        # import unchanged, independently from the generated visual LED map.
        variant = target['qmk'].split('/')[-1]
        source = (keyboard_path.parent/(variant+'.c')).read_text()
        table = re.search(r'g_aw20216s_leds\[AW20216S_LED_COUNT\]\s*=\s*\{(.*?)\n\};', source, re.S)
        verify(table is not None, f'{name}: electrical LED initializer absent')
        entries = re.findall(r'\{\s*(\d),\s*SW(\d+)_CS(\d+),\s*SW(\d+)_CS(\d+),\s*SW(\d+)_CS(\d+)\s*\}', table[1])
        electrical = bytearray()
        for entry in entries:
            driver,rw,rc,gw,gc,bw,bc=map(int,entry)
            electrical.extend((driver,(rw-1)*18+rc-1,(gw-1)*18+gc-1,(bw-1)*18+bc-1))
        verify(len(entries)==target['leds'] and flash_bytes('g_aw20216s_leds',len(electrical))==electrical, f'{name}: actual electrical RGB channels differ from source')
    descriptor = flash_bytes('DeviceDescriptor', 18)
    verify(descriptor[:2] == bytes([18, 1]) and struct.unpack_from('<HH', descriptor, 8) == (0xFEED, int(target['runtimeProductId'], 16)), f'{name}: actual USB VID/PID')
    for symbol, expected in [('ProductString', target['product']), ('ManufacturerString', 'INGLORIOUS')]:
        size = flash_bytes(symbol, 2)
        verify(size[1] == 3 and size[0] >= 2, f'{name}: {symbol} header')
        verify(flash_bytes(symbol, size[0])[2:].decode('utf-16le') == expected, f'{name}: actual {symbol}')
    results.append(dict(deviceId=target['deviceId'], boardId=target['boardId'], imageBytes=len(binary),
        imageSha256=sha(build/(name+'.bin')), codeLimit=limit, heapBytes=heap,
        mainStackBytes=main_stack, processStackBytes=process_stack,
        maximumCompilerFrame=dict(bytes=maximum_frame[0], function=maximum_frame[1], qualifier=maximum_frame[2]),
        usbIdentityVerified=True, ledMappingVerified=True, physicalMaskVerified=True,
        lightingTestPositionsVerified=True, baseKeymapComparedToCSharp=target['boardId'] > 2,
        electricalDriverTableCompared=target['boardId'] >= 10,
        hardwareAccepted=False))

print(json.dumps(dict(schema=1, checks=checks, targets=results, buildManifestSha256=sha(build/'manifest.json'),
    hardwareAccess=False, note='Static map, USB descriptor and compiler frame evidence only; physical behavior and runtime stack high-water marks remain untested.'), indent=2))
