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
import argparse

ROOT = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('build', nargs='?', type=Path, default=ROOT / 'build-candidates')
parser.add_argument('--engineering', action='store_true')
parser.add_argument('--board')
args = parser.parse_args()
if args.engineering and args.board != 'gmmk3-pro-100-iso':
    parser.error('Engineering verification requires --board gmmk3-pro-100-iso')
build = args.build.resolve()
load = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest().upper()
default_targets = load(ROOT / 'targets.json')
target_path = ROOT / ('engineering/targets.json' if args.engineering else 'targets.json')
targets = load(target_path)
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

verify(default_targets['schema'] == 1 and len(default_targets['targets']) == 16, 'Expected sixteen target definitions')
verify([t['boardId'] for t in default_targets['targets']] == list(range(1, 17)), 'Default board IDs changed')
verify([t['runtimeProductId'] for t in default_targets['targets']] == [f'1C{i:02X}' for i in range(1, 17)], 'Default runtime PIDs changed')
verify([t['verifiedStockProductId'] for t in default_targets['targets']] == ['5046', '5088'] + [None] * 13 + ['E3CE'], 'Stock hardware identities changed')
selected = [t for t in targets['targets'] if not args.board or args.board in (t['name'], t['deviceId'])]
verify(bool(selected), 'Unknown selected target')
if args.engineering:
    verify(targets['engineering'] is True and targets['releaseCandidate'] is False and len(targets['targets']) == 1, 'Explicit engineering manifest required')
    verify(targets['defaultManifestSha256'] == sha(ROOT/'targets.json'), 'Default manifest changed')
    t = targets['targets'][0]
    verify((t['deviceId'], t['boardId'], t['runtimeProductId'], t['qmk'], t['flashBytes'], t['ramBytes']) ==
           (args.board, 16, '1C10', 'gmmk/inglorious3pro/p100/iso', 262144, 36864), 'Engineering identity/geometry mismatch')
    verify(t['engineeringOnly'] is True and t['hardwareAcceptanceTested'] is False, 'Engineering limitations missing')
    verify(t['applicationOffsetBytes'] == t['protectedPrefixBytes'] == 0x7000, 'Resident-loader prefix must be protected')
    verify(t['identityBytes'] == 256 and manifest['identityBytes'] == 256, 'Identity page reservation missing')
    verify(manifest['engineering'] is True and manifest['releaseCandidate'] is False, 'Engineering build classification missing')
    verify(manifest['applicationOffsetBytes'] == manifest['protectedPrefixBytes'] == 0x7000 and manifest['includesResidentLoader'] is False,
           'Application-only build provenance missing')
verify(manifest['targetManifestSha256'] == sha(target_path), 'Target manifest changed after build')
verify(manifest['qmkCommit'] == targets['qmkCommit'], 'QMK commit mismatch')
verify(manifest['hardwareFlashed'] is False and manifest['hardwareAcceptanceTested'] is False, 'Offline provenance missing')
verify(manifest['configSchemas'] == [1, 2, 3, 4, 5, 6, 7], 'Declared configuration schemas differ from runtime support')
verify(manifest['targets'] == [t['deviceId'] for t in selected], 'Build selection differs from requested targets')
verify(manifest['engineering'] == args.engineering and manifest['includesResidentLoader'] is False, 'Application-only build classification differs')
verify([t['deviceId'] for t in manifest['targetBuilds']] == manifest['targets'], 'Per-target identity selection differs')
verify(manifest.get('profileBanks') == any(t.get('profileBankLayout') == 'profile-pairs-v1' for t in selected),
       'Build profile-bank summary differs from the selected targets')
if len(selected) > 1:
    verify(all(manifest[field] is None for field in ('identityVersionHeaderPath','identityVersionHeaderSha256',
                                                  'applicationOffsetBytes','protectedPrefixBytes','identityBytes')), 'Mixed-target build has a misleading global identity/layout')
    for field in ('firmwareVersion','firmwareBuildId'):
        values = {t[field] for t in manifest['targetBuilds']}
        verify(manifest[field] == (next(iter(values)) if len(values)==1 else None), 'Global version differs from per-target identities')
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
for target in selected:
    name = target['name']
    retained = target['boardId'] == 16
    profile_banks = target.get('profileBankLayout') == 'profile-pairs-v1'
    verify(('profileBankLayout' in target) == profile_banks and profile_banks == retained,
           f'{name}: profile banks are enabled only for the exact reviewed board')
    identity = next(t for t in manifest['targetBuilds'] if t['deviceId'] == target['deviceId'])
    identity_header = target.get('versionHeaderPath', 'inglorious/ig_version.h')
    verify(identity['identityVersionHeaderPath'] == identity_header and identity['identityVersionHeaderSha256'] == sha(ROOT/identity_header),
           f'{name}: selected version identity header differs')
    identity_text = (ROOT/identity_header).read_text()
    version_parts = []
    for part in ('MAJOR', 'MINOR', 'PATCH'):
        values = re.findall(r'^#define IG_FW_VERSION_' + part + r' (\d+)$', identity_text, re.M)
        verify(len(values) == 1 and 0 <= int(values[0]) <= 65535, f'{name}: ambiguous or invalid firmware version component')
        version_parts.append(int(values[0]))
    build_ids = re.findall(r'^#define IG_FW_BUILD_ID 0x([0-9A-Fa-f]{8})UL$', identity_text, re.M)
    verify(len(build_ids) == 1 and identity['firmwareVersion'] == '.'.join(map(str,version_parts)) and
           identity['firmwareBuildId'] == build_ids[0].upper(), f'{name}: manifest version differs from selected header')
    expected_version = ('0.11.11','000B0B01') if retained else ('0.11.9','000B0901')
    verify((identity['firmwareVersion'],identity['firmwareBuildId']) == expected_version, f'{name}: release version changed')
    expected_bank = dict(id='profile-pairs-v1', offset=172032, length=49152, recordBytes=8192, recordCount=6, profileCount=3)
    verify(identity.get('profileBankLayout') == (expected_bank if profile_banks else None),
           f'{name}: per-target profile bank geometry differs')
    if len(selected) == 1:
        verify(all(manifest[field] == identity[field] for field in ('firmwareVersion','firmwareBuildId','identityVersionHeaderPath',
                   'identityVersionHeaderSha256','applicationOffsetBytes','protectedPrefixBytes','identityBytes')), 'Single-target identity/layout differs')
    binary = (build / (name + '.bin')).read_bytes()
    map_text = (build / (name + '.map')).read_text(encoding='utf-8-sig')
    log = (build / (name + '-build.log')).read_text(encoding='utf-8-sig')
    stack_text = (build / (name + '-stack-usage.txt')).read_text(encoding='utf-8-sig')
    application_offset = target.get('applicationOffsetBytes',0)
    application_base = 0x08000000 + application_offset
    storage_base = 0x08000000 + target['flashBytes'] - targets['storageBytes']
    identity_bytes = target.get('identityBytes',0)
    verify(identity['applicationOffsetBytes'] == application_offset and identity['protectedPrefixBytes'] == target.get('protectedPrefixBytes',0) and
           identity['identityBytes'] == identity_bytes, f'{name}: per-target image layout differs')
    code_end = storage_base - identity_bytes
    limit = target['flashBytes'] - targets['storageBytes'] - application_offset - identity_bytes
    if profile_banks:
        code_end = 0x0802A000
        limit = code_end - application_base
    verify(1024 <= len(binary) <= limit, f'{name}: code enters trailing storage')
    sp, pc = struct.unpack_from('<II', binary)
    verify(0x20000000 <= sp <= 0x20000000 + target['ramBytes'] and sp % 4 == 0, f'{name}: stack vector')
    verify(pc & 1 and application_base <= (pc & ~1) < application_base + len(binary), f'{name}: reset vector')
    flash = re.search(r'^flash0\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)', map_text, re.M)
    verify(flash is not None and int(flash[1], 16) == application_base and int(flash[2],16) == limit, f'{name}: linker prefix/storage reservations')
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
        offset = symbol_address(map_text, symbol) - application_base
        verify(0 <= offset <= len(binary) - count, f'{name}: {symbol} outside ROM')
        return binary[offset:offset+count]
    def flash_constant(section):
        matches = re.findall(r'^\s*\.rodata\.' + re.escape(section) + r'\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)', map_text, re.M)
        verify(len(matches) == 1, f'{name}: missing constant {section}')
        offset, count = int(matches[0][0], 16) - application_base, int(matches[0][1], 16)
        verify(0 <= offset <= len(binary)-count, f'{name}: constant range {section}')
        return binary[offset:offset+count]
    def runtime_local_constant(section):
        # GCC numbers function-local constants in declaration order. Anchor to
        # the runtime object rather than a suffix that changes with new locals.
        matches = re.findall(r'^\s*\.rodata\.' + re.escape(section) + r'\.[0-9]+\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)\s+[^\r\n]*keymaps/inglorious/inglorious\.o$', map_text, re.M)
        verify(len(matches) == 1, f'{name}: missing runtime-local constant {section}')
        offset, count = int(matches[0][0], 16) - application_base, int(matches[0][1], 16)
        verify(0 <= offset <= len(binary)-count, f'{name}: runtime-local constant range {section}')
        return binary[offset:offset+count]
    expected_identity = struct.pack('<HBB4HI', 1, target['boardId'], 0, *version_parts, 0, int(build_ids[0],16))
    if retained:
        verify(flash_constant('ig_fw_identity') == expected_identity, f'{name}: linked RAW13 identity differs from selected version')
    else:
        # GCC folds the 16-byte RAW13 copy into an anonymous constant in the
        # common runtime's own .rodata; the unused named identity may be GC'd.
        constants = re.findall(r'^\s*\.rodata\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)\s+[^\r\n]*keymaps/inglorious/inglorious\.o$',map_text,re.M)
        constants = [(int(address,16)-application_base,int(size,16)) for address,size in constants if int(address,16)>=application_base]
        verify(len(constants)==1 and constants[0][1]==16 and binary[constants[0][0]:constants[0][0]+16]==expected_identity,
               f'{name}: compiled RAW13 constant differs from selected version')
    verify(runtime_local_constant('color') == bytes((255,0,0)), f'{name}: fallback is not pure red in the image')
    if target['encoder']:
        verify(runtime_local_constant('factorial') == struct.pack('<8H', 5040,720,120,24,6,2,1,1), f'{name}: knob order factors differ in the image')
    keyboard_path = ROOT / 'qmk_firmware/keyboards' / target['qmk'] / 'keyboard.json'
    reference_id = target.get('sourceDerivative',target['deviceId'])
    reference_path = ROOT.parent / 'reference/qmk' / (reference_id + '.json')
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
        fixture_root = build/'fixtures' if retained else ROOT.parent / ('artifacts/gmmk3-port-fixtures' if target['boardId'] >= 10 else 'artifacts/port-fixtures')
        fixture_name = target['deviceId'] + '-default.bin'
        entries = [e for e in load(fixture_root/'manifest.json') if e['file'] == fixture_name]
        verify(len(entries) == 1, f'{name}: missing C# default fixture record')
        fixture_path = safe_file(fixture_root, fixture_name)
        verify(sha(fixture_path) == entries[0]['sha256'], f'{name}: C# fixture hash')
        cells_bytes = target['rows'] * target['cols'] * 2
        expected = fixture_path.read_bytes()[32:32+cells_bytes]
        verify(len(expected) == cells_bytes and flash_bytes('keymaps', cells_bytes) == expected,
               f'{name}: compiled base keymap differs from the C# default configuration')
    if retained:
        verify((target['deviceId'],target['qmk'],target['flashBytes'],target['ramBytes'],target['runtimeProductId']) ==
               ('gmmk3-pro-100-iso','gmmk/inglorious3pro/p100/iso',262144,36864,'1C10'), 'Retained-loader target identity/geometry differs')
        config = (ROOT/'keymaps'/name/'config.h').read_text()
        verify(re.search(r'^#define IG_RETAINED_LOADER_BUILD 1$', config, re.M) is not None, 'Retained-loader compile gate absent')
        verify(bool(re.search(r'^#define IG_ENGINEERING_BUILD 1$', config, re.M)) == args.engineering, 'Engineering compile classification differs')
        verify(identity['cleanTargetBuild'] is True, 'Board 16 must be clean-built')
        verify(re.search(r'^#define IG_BOARD 16$', config, re.M) is not None, 'Wrong compiled board ID')
        verify(re.search(r'^#define WEAR_LEVELING_EFL_FLASH_SIZE 262144$', config, re.M) is not None, 'Wrong storage capacity')
        verify(re.search(r'^#define IG_PROFILE_BANK_ENABLE 1$', config, re.M) is not None, 'Profile-bank compile gate absent')
        verify(application_base == 0x08007000 and limit == 0x23000 and code_end == 0x0802A000 and storage_base == 0x08038000 and targets['storageBytes'] == 0x8000,
               'Application must exclude resident prefix, profile bank, identity page and legacy storage')
        for hook in ('ig_pb_scan_bank', 'ig_pb_read_profile', 'ig_pb_save_profile', 'ig_profile_flash_ready',
                     'ig_profile_flash_bytes', 'ig_profile_flash_identity', 'ig_profile_flash_begin', 'ig_profile_flash_end', 'ig_profile_flash_io'):
            verify(application_base <= symbol_address(map_text,hook) < application_base+len(binary), 'Profile-bank implementation absent from linked image')
        verify(symbol_address(map_text, '_vectors') == application_base, 'Linked vector table is not at the application origin')
        entry = symbol_address(map_text, '_crt0_entry')
        reset_handler = symbol_address(map_text, 'Reset_Handler')
        verify((pc & ~1) == reset_handler, 'Reset vector does not enter the linked reset handler')
        reset_branch = struct.unpack_from('<H',binary,reset_handler-application_base)[0]
        displacement = reset_branch & 0x7FF
        if displacement & 0x400:
            displacement -= 0x800
        verify(reset_branch & 0xF800 == 0xE000 and reset_handler+4+displacement*2 == entry,
               'Actual reset handler does not branch to linked startup')
        startup_offset = entry - application_base
        def thumb_literal(at, instruction):
            literal = ((application_base + at + 4) & ~3) + (instruction & 255) * 4 - application_base
            verify(0 <= literal <= len(binary)-4, 'Startup literal outside image')
            return struct.unpack_from('<I', binary, literal)[0]
        vtor_set = False
        for at in range(startup_offset, startup_offset+64, 2):
            first, second, third = struct.unpack_from('<3H', binary, at)
            if first & 0xFF00 == 0x4800 and second & 0xFF00 == 0x4900 and third == 0x6008:
                if thumb_literal(at, first) == application_base and thumb_literal(at+2, second) == 0xE000ED08:
                    vtor_set = True
        verify(vtor_set, 'Actual startup does not initialize VTOR to relocated vectors')
        elf = (build/(name+'.elf')).read_bytes()
        verify(elf[:6] == b'\x7fELF\x01\x01' and struct.unpack_from('<H',elf,18)[0] == 40, 'Expected little-endian ARM ELF32')
        phoff = struct.unpack_from('<I',elf,28)[0]
        phsize, phcount = struct.unpack_from('<HH',elf,42)
        verify(phsize == 32 and phcount > 0, 'ELF program header geometry')
        load_addresses = []
        for i in range(phcount):
            kind, offset, vma, lma, filesz, memsz, flags, align = struct.unpack_from('<8I',elf,phoff+i*phsize)
            if kind == 1 and filesz:
                verify(application_base <= lma < code_end and lma+filesz <= code_end,
                       'ELF load segment enters protected prefix, identity page or storage')
                verify(elf[offset:offset+filesz] == binary[lma-application_base:lma-application_base+filesz], 'ELF load bytes differ from application BIN')
                load_addresses.append(lma)
        verify(load_addresses and min(load_addresses) == application_base, 'ELF has no application load segment')
        hex_base = 0
        hex_addresses = []
        for line in (build/(name+'.hex')).read_text().splitlines():
            verify(line.startswith(':'), 'Intel HEX record format')
            record = bytes.fromhex(line[1:])
            verify(len(record) == record[0]+5 and sum(record)%256 == 0, 'Intel HEX length/checksum')
            count, high, low, kind = record[:4]
            payload = record[4:4+count]
            if kind == 4:
                hex_base = int.from_bytes(payload,'big') << 16
            elif kind == 0:
                address = hex_base + (high<<8) + low
                verify(application_base <= address and address+count <= code_end, 'HEX enters protected prefix, identity page or storage')
                verify(payload == binary[address-application_base:address-application_base+count], 'HEX bytes differ from application BIN')
                hex_addresses.append(address)
            else:
                verify(kind in (1,5), 'Unsupported Intel HEX record')
        verify(hex_addresses and min(hex_addresses) == application_base, 'HEX is not an application-only image')
        efl = struct.unpack('<7I', flash_constant('efl_lld_descriptor'))
        verify(efl == (2, 8, 1024, 0, 256, 0x08000000, 262144), 'Actual EFL descriptor geometry/erase polarity differs')
        base_codes = struct.unpack('<112H', flash_bytes('keymaps', 224))
        recovery = [(base_codes.index(code)//8, base_codes.index(code)%8) for code in (0x2C, 0x05)]
        verify(recovery == [(9,4), (4,5)], 'Actual base Space/B recovery positions differ')
        for i, (row, col) in enumerate(recovery, 1):
            verify(re.search(rf'^#define IG_RECOVERY_KEY{i}_ROW {row}$', header, re.M) is not None and
                   re.search(rf'^#define IG_RECOVERY_KEY{i}_COL {col}$', header, re.M) is not None,
                   'Recovery header differs from physical base keys')
        verify('bootloader_jump' in map_text, 'ROM recovery entry absent from linked image')
        for hook in ('matrix_scan_user','pre_process_record_user'):
            verify(application_base <= symbol_address(map_text,hook) < application_base+len(binary), 'Physical diagnostic hook absent from linked image')
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
    led_offset = data_lma - application_base + led_address - data_vma
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
        applicationBase=f'{application_base:08X}', protectedPrefixBytes=application_offset,
        mainStackBytes=main_stack, processStackBytes=process_stack,
        maximumCompilerFrame=dict(bytes=maximum_frame[0], function=maximum_frame[1], qualifier=maximum_frame[2]),
        usbIdentityVerified=True, ledMappingVerified=True, physicalMaskVerified=True,
        lightingTestPositionsVerified=True, baseKeymapComparedToCSharp=target['boardId'] > 2,
        electricalDriverTableCompared=target['boardId'] >= 10,
        firmwareVersion=identity['firmwareVersion'], firmwareBuildId=identity['firmwareBuildId'], linkedVersionIdentityVerified=True,
        engineering=args.engineering, hardwareAccepted=False))

print(json.dumps(dict(schema=1, checks=checks, targets=results, buildManifestSha256=sha(build/'manifest.json'),
    hardwareAccess=False, note='Static map, USB descriptor and compiler frame evidence only; physical behavior and runtime stack high-water marks remain untested.'), indent=2))
