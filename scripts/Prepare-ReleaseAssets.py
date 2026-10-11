"""Stage an exact, checksum-verified set of public release downloads."""
import argparse
import hashlib
from pathlib import Path
import re
import shutil
import xml.etree.ElementTree as ET


def no_links(path: Path) -> None:
    for part in (path.absolute(), *path.absolute().parents):
        if part.is_symlink() or (hasattr(part, "is_junction") and part.is_junction()):
            raise ValueError("Linked release paths are not accepted.")


def file_digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def stage(source: Path, destination: Path, version: str, repository: Path) -> list[Path]:
    if not re.fullmatch(r"0|[1-9][0-9]*", version.split(".")[0]) or not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", version):
        raise ValueError("Use the numeric application version.")
    for project in ("Core", "Desktop"):
        actual = ET.parse(repository / f"src/InGlorious.{project}/InGlorious.{project}.csproj").findtext("PropertyGroup/Version")
        if actual != version:
            raise ValueError("Application and release versions differ.")
    for path in (source, destination, repository):
        no_links(path)
    source = source.resolve(strict=True)
    if destination.exists() or destination.is_symlink():
        raise ValueError("Use a new release staging directory.")
    expected = [f"INGLORIOUS-Advanced-{version}-{suffix}" for suffix in (
        "Setup.exe", "linux-x64.tar.gz", "osx-x64.tar.gz", "osx-arm64.tar.gz", "Source.zip")]
    files = [p for p in source.rglob("*") if p.is_file()]
    if any(p.is_symlink() for p in source.rglob("*")):
        raise ValueError("Linked release artifacts are not accepted.")
    allowed = set(expected + [name + ".sha256" for name in expected])
    if any(p.name not in allowed for p in files):
        raise ValueError("Unexpected release artifact. Download only the four public package artifacts.")
    staged = []
    digests = {}
    for name in expected:
        binary = [p for p in files if p.name == name]
        checksum = [p for p in files if p.name == name + ".sha256"]
        if len(binary) != 1 or len(checksum) != 1:
            raise ValueError("A release package or checksum is missing or duplicated: " + name)
        text = checksum[0].read_text(encoding="utf-8-sig").strip()
        match = re.fullmatch(r"([0-9a-fA-F]{64})  " + re.escape(name), text)
        if not match or binary[0].stat().st_size == 0:
            raise ValueError("Invalid release checksum: " + name)
        digest = file_digest(binary[0])
        if digest != match[1].lower():
            raise ValueError("Release checksum mismatch: " + name)
        staged.extend((binary[0], checksum[0]))
        digests[binary[0].name] = digest
        digests[checksum[0].name] = file_digest(checksum[0])
    changelog = (repository / "CHANGELOG.md").read_text(encoding="utf-8-sig")
    section = re.search(r"^## " + re.escape(version) + r"\s*\n(.*?)(?=^## |\Z)", changelog, re.M | re.S)
    if not section or not section[1].strip():
        raise ValueError("A short changelog entry is required.")
    destination.mkdir(parents=True, exist_ok=False)
    no_links(destination)
    for path in staged:
        no_links(path)
        shutil.copyfile(path, destination / path.name)
        if file_digest(destination / path.name) != digests[path.name]:
            raise ValueError("Release copy verification failed.")
    notes = (section[1].strip() + "\n\n"
             "Choose **Windows (win-x64) — Setup** for Windows, **osx-arm64** for Apple silicon, **osx-x64** for Intel Macs, "
             "or **linux-x64** for Linux. Each app package includes firmware and its USB tools.\n\n"
             "macOS and Linux are **Early Alpha**. Native builds and offline checks do not replace physical-device testing. "
             "Windows packages are unsigned; macOS packages are not notarized.\n\n"
             "**GMMK Numpad: USB only. Bluetooth does not work with INGLORIOUS firmware.** "
             "Additional model/layout variants marked experimental have not been tested on hardware.\n\n"
             "Updating the app keeps profiles and original-firmware backups. Keyboard firmware is installed separately in the app. "
             "Use at your own risk and keep a separate copy of your original backups.\n\n"
             "The Source archive includes the matching application and upstream firmware sources.\n")
    (destination / "release-notes.md").write_text(notes, encoding="utf-8")
    return [destination / path.name for path in staged]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parent.parent)
    arguments = parser.parse_args()
    paths = stage(arguments.input, arguments.output, arguments.version, arguments.repository)
    print(f"Verified {len(paths) // 2} packages with matching checksums. No upload performed.")
