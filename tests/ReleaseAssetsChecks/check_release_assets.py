import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release_assets", ROOT / "scripts/Prepare-ReleaseAssets.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseAssetsChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="inglorious-release-checks-")
        self.root = Path(self.temp.name).resolve()
        self.source = self.root / "input"
        self.source.mkdir()
        self.target = self.root / "staged"
        self.repo = self.root / "repo"
        for project in ("Core", "Desktop"):
            path = self.repo / f"src/InGlorious.{project}/InGlorious.{project}.csproj"
            path.parent.mkdir(parents=True)
            path.write_text("<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>")
        (self.repo / "CHANGELOG.md").write_text("# Changelog\n\n## 1.2.3\n\n- Test release.\n\n## 1.2.2\n\n- Old release.\n")
        self.names = [f"INGLORIOUS-Advanced-1.2.3-{suffix}" for suffix in ("win-x64-Setup.exe", "Source.zip", "linux-x64.tar.gz", "osx-x64.tar.gz", "osx-arm64.tar.gz")]
        for name in self.names:
            data = ("Synthetic package " + name).encode()
            (self.source / name).write_bytes(data)
            (self.source / (name + ".sha256")).write_text(hashlib.sha256(data).hexdigest() + "  " + name + "\n")

    def tearDown(self):
        self.temp.cleanup()

    def stage(self):
        return module.stage(self.source, self.target, "1.2.3", self.repo)

    def test_exact_files_and_notes(self):
        staged = self.stage()
        self.assertEqual(len(staged), 10)
        self.assertEqual({path.name for path in staged}, set(self.names + [name + ".sha256" for name in self.names]))
        notes = (self.target / "release-notes.md").read_text()
        self.assertTrue(notes.startswith("- Test release.\n"))
        self.assertEqual(notes.count("Early Alpha"), 1)
        self.assertIn("macOS and Linux are **Early Alpha**.", notes)
        self.assertIn("INGLORIOUS-Advanced-1.2.3-win-x64-Setup.exe", notes)
        self.assertNotIn("Windows (win-x64)", notes)
        self.assertIn("USB only", notes)
        self.assertIn("- Test release.", notes)
        self.assertNotIn("- Old release.", notes)

    def test_windows_package_migration_note_is_only_in_0_6_49(self):
        notice = "Windows 0.6.48 and earlier: download and run the installer once to switch to the new package names. Your profiles and backups are kept."
        for version in ("0.6.48", "0.6.49", "0.6.50"):
            with self.subTest(version=version):
                source = self.root / ("input-" + version)
                source.mkdir()
                for name in self.names:
                    versioned_name = name.replace("1.2.3", version)
                    data = (self.source / name).read_bytes()
                    (source / versioned_name).write_bytes(data)
                    (source / (versioned_name + ".sha256")).write_text(hashlib.sha256(data).hexdigest() + "  " + versioned_name + "\n")
                for project in ("Core", "Desktop"):
                    path = self.repo / f"src/InGlorious.{project}/InGlorious.{project}.csproj"
                    path.write_text(f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>")
                (self.repo / "CHANGELOG.md").write_text(f"# Changelog\n\n## {version}\n\n- Test release.\n")
                destination = self.root / ("staged-" + version)
                module.stage(source, destination, version, self.repo)
                notes = (destination / "release-notes.md").read_text()
                self.assertEqual(notes.count(notice), 1 if version == "0.6.49" else 0)

    def test_windows_installer_requires_the_architecture_in_its_name(self):
        expected = self.names[0]
        for suffix in ("Setup.exe", "win-arm64-Setup.exe"):
            with self.subTest(suffix=suffix):
                alias = "INGLORIOUS-Advanced-1.2.3-" + suffix
                package = self.source / expected
                checksum = self.source / (expected + ".sha256")
                package.rename(self.source / alias)
                checksum.rename(self.source / (alias + ".sha256"))
                (self.source / (alias + ".sha256")).write_text(hashlib.sha256((self.source / alias).read_bytes()).hexdigest() + "  " + alias + "\n")
                try:
                    with self.assertRaises(ValueError): self.stage()
                    self.assertFalse(self.target.exists())
                finally:
                    (self.source / alias).rename(package)
                    (self.source / (alias + ".sha256")).rename(checksum)
                    checksum.write_text(hashlib.sha256(package.read_bytes()).hexdigest() + "  " + expected + "\n")

    def test_wrong_hash_preserves_output(self):
        (self.source / self.names[0]).write_bytes(b"changed")
        with self.assertRaises(ValueError): self.stage()
        self.assertFalse(self.target.exists())

    def test_missing_package(self):
        (self.source / self.names[0]).unlink()
        with self.assertRaises(ValueError): self.stage()

    def test_unexpected_private_file(self):
        (self.source / "original-backup.bin").write_bytes(b"synthetic")
        with self.assertRaises(ValueError): self.stage()
        self.assertFalse(self.target.exists())

    def test_duplicate(self):
        nested = self.source / "duplicate"
        nested.mkdir()
        (nested / self.names[0]).write_bytes(b"duplicate")
        with self.assertRaises(ValueError): self.stage()

    def test_version_mismatch(self):
        with self.assertRaises(ValueError): module.stage(self.source, self.target, "1.2.4", self.repo)

    def test_no_overwrite(self):
        self.stage()
        with self.assertRaises(ValueError): self.stage()

    def test_invalid_checksum_filename(self):
        (self.source / (self.names[0] + ".sha256")).write_text("0" * 64 + "  ../" + self.names[0])
        with self.assertRaises(ValueError): self.stage()

    def test_modified_copy_source_is_rejected(self):
        original_copy = module.shutil.copyfile
        def changed_copy(source, destination):
            if source.name == self.names[0]: source.write_bytes(b"changed during copy")
            return original_copy(source, destination)
        with patch.object(module.shutil, "copyfile", side_effect=changed_copy):
            with self.assertRaises(ValueError): self.stage()

    def test_linked_input_is_rejected(self):
        link = self.root / "linked-input"
        try: link.symlink_to(self.source, target_is_directory=True)
        except OSError: self.skipTest("Host does not permit directory symlinks.")
        with self.assertRaises(ValueError): module.stage(link, self.target, "1.2.3", self.repo)

    def test_linked_output_parent_is_rejected(self):
        link = self.root / "linked-output"
        try: link.symlink_to(self.root, target_is_directory=True)
        except OSError: self.skipTest("Host does not permit directory symlinks.")
        with self.assertRaises(ValueError): module.stage(self.source, link / "staged", "1.2.3", self.repo)


if __name__ == "__main__":
    unittest.main()
