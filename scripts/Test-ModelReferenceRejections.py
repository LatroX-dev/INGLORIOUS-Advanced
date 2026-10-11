"""Isolated filesystem fixtures for the public model-reference checker; no hardware.

Production reference and port files are never modified. Even manifest hashes are
updated in selected fixtures to prove the independent source derivation detects
plausible but incorrect normalized data.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / 'artifacts'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def refresh(root, filenames):
    manifest = read(root / 'reference/qmk/sources.json')
    provenance = read(root / 'firmware/ports/gmmk3/provenance.json')
    for relative in filenames:
        raw = (root / relative).read_bytes()
        digest = hashlib.sha256(raw).hexdigest().upper()
        for entry in provenance['outputs']:
            if entry['path'] == relative:
                entry['bytes'], entry['sha256'] = len(raw), digest
        if relative.startswith('reference/qmk/'):
            for entry in manifest:
                if entry['file'] == Path(relative).name:
                    entry['bytes'], entry['sha256'] = len(raw), digest
    save(root / 'reference/qmk/sources.json', manifest)
    save(root / 'firmware/ports/gmmk3/provenance.json', provenance)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--powershell', default='pwsh')
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    ARTIFACTS.mkdir(exist_ok=True)
    fixture = Path(tempfile.mkdtemp(prefix='model-reference-fixture-', dir=ARTIFACTS)).resolve()
    if not fixture.is_relative_to(ARTIFACTS.resolve()):
        raise ValueError('Fixture is outside the intended artifact directory')
    results = []
    try:
        def reset():
            # Overwrite only our known fixture files; no production directory is a destination.
            for source in ('reference/qmk', 'reference/glorious-gmmk3', 'firmware/ports/gmmk3'):
                shutil.copytree(ROOT / source, fixture / source, dirs_exist_ok=True)

        def run(name, failure=None):
            command = [args.powershell, '-NoProfile', '-File', str(ROOT / 'scripts/Test-ModelReferences.ps1'),
                       '-ReferenceRoot', str(fixture / 'reference/qmk'),
                       '-ManufacturerRoot', str(fixture / 'reference/glorious-gmmk3'),
                       '-PortRoot', str(fixture / 'firmware/ports/gmmk3'),
                       '-QmkRoot', str(ROOT / 'firmware/qmk_firmware'), '-RequireCheckout']
            outcome = subprocess.run(command, capture_output=True, text=True, encoding='utf-8', timeout=45)
            text = outcome.stdout + outcome.stderr
            if failure is None:
                if outcome.returncode or read_json(text)['result'] != 'PASS':
                    raise AssertionError(f'{name}: positive isolated root failed: {text}')
            elif outcome.returncode == 0 or failure not in text:
                raise AssertionError(f'{name}: expected {failure!r}, got {text}')
            results.append(dict(name=name, passed=True, expectedRejection=failure))

        def read_json(text):
            return json.loads(text)

        reset(); run('valid isolated roots')
        geometry_paths = ['reference/qmk/gmmk3-75-iso.json', 'firmware/ports/gmmk3/p75/iso/keyboard.json']
        for path in geometry_paths:
            data = read(fixture / path)
            next(k for k in data['layouts']['LAYOUT']['layout'] if k['matrix'] == [10, 4])['y'] = 1.25
            save(fixture / path, data)
        refresh(fixture, geometry_paths)
        run('original 75 ISO Enter overlap restored with valid manifest hashes', 'physical key overlap')
        reset()
        for path in geometry_paths:
            data = read(fixture / path)
            key = next(k for k in data['layouts']['LAYOUT']['layout'] if k['matrix'] == [10, 2])
            key['x'], key['y'] = 11.25, 4.25
            save(fixture / path, data)
        refresh(fixture, geometry_paths)
        run('original 75 ISO hash/Slash overlap restored with valid manifest hashes', 'physical key overlap')
        reset()
        # Exercise the general geometry guard on an unaffected variant too.
        geometry_paths = ['reference/qmk/gmmk3-100-iso.json', 'firmware/ports/gmmk3/p100/iso/keyboard.json']
        for path in geometry_paths:
            data = read(fixture / path)
            next(k for k in data['layouts']['LAYOUT']['layout'] if k['matrix'] == [6, 1])['x'] += .125
            save(fixture / path, data)
        refresh(fixture, geometry_paths)
        run('adjacent bracket overlaps ISO Enter upper extension only', 'physical key overlap')
        reset()
        json_paths = ['reference/qmk/gmmk3-65-ansi.json', 'firmware/ports/gmmk3/p65/ansi/keyboard.json']
        for path in json_paths:
            data = read(fixture / path)
            leds = data['rgb_matrix']['layout']
            leds[0]['matrix'], leds[10]['matrix'] = leds[10]['matrix'], leds[0]['matrix']
            save(fixture / path, data)
        refresh(fixture, json_paths)
        run('swapped zero/Escape LEDs with valid rewritten manifest hashes', 'normalized LED map differs')
        reset()
        original = fixture / 'reference/glorious-gmmk3/p65/ansi/ansi.c'
        original.write_bytes(original.read_bytes() + b'\n')
        run('changed original source in supplied manufacturer root', 'Original source hash mismatch')
        manifest_path = fixture / 'reference/glorious-gmmk3/sources.json'
        catalog = read(manifest_path)
        for entry in catalog['files']:
            if entry['path'].endswith('/p65/ansi/ansi.c'):
                raw = original.read_bytes()
                entry['bytes'], entry['sha256'] = len(raw), hashlib.sha256(raw).hexdigest().upper()
        save(manifest_path, catalog)
        run('rewritten original source hash cannot replace pinned catalog', 'Pinned manufacturer catalog SHA256 mismatch')
        reset()
        driver = fixture / 'firmware/ports/gmmk3/p65/ansi/ansi.c'
        driver.write_text(driver.read_text(encoding='utf-8').replace('{0, SW1_CS1, SW1_CS2, SW1_CS3}', '{0, SW1_CS2, SW1_CS1, SW1_CS3}', 1), encoding='utf-8')
        refresh(fixture, ['firmware/ports/gmmk3/p65/ansi/ansi.c'])
        run('swapped red/green electrical channels with updated provenance hash', 'electrical RGB table changed')
        reset()
        defaults = ['reference/qmk/gmmk3-65-ansi-default.c', 'firmware/ports/gmmk3/p65/ansi/keymaps/default/keymap.c']
        for path in defaults:
            file = fixture / path
            file.write_text(file.read_text(encoding='utf-8').replace('KC_ESC', 'KC_A', 1), encoding='utf-8')
        refresh(fixture, defaults)
        run('changed normalized base key with valid manifest hashes', 'normalized defaults differ')
        reset()
        provenance_path = fixture / 'firmware/ports/gmmk3/provenance.json'
        provenance = read(provenance_path)
        provenance['normalizations'][0]['corrections'] = []
        save(provenance_path, provenance)
        run('missing declared mapping correction', 'normalization provenance mismatch')
    finally:
        # Resolved absolute target is checked again immediately before recursive cleanup.
        if fixture.is_relative_to(ARTIFACTS.resolve()) and fixture.name.startswith('model-reference-fixture-'):
            shutil.rmtree(fixture)
        else:
            raise ValueError('Refusing cleanup outside intended artifact fixture directory')
    report = dict(result='PASS', hardwareAccess=False, productionFilesChanged=False,
                  fixtureDirectoryRemoved=not fixture.exists(), checks=len(results), results=results)
    output = json.dumps(report, ensure_ascii=False, indent=2)
    if args.report:
        with args.report.open('x', encoding='utf-8') as file:
            file.write(output + '\n')
    print(output)


if __name__ == '__main__':
    main()
