#!/usr/bin/env python3
"""Offline release-gate and installer payload regression tests."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
WEB = ROOT / 'src/CwyLs.ModLoader.Maker.Web'
MAC = (ROOT / 'tools/release/mac/install.command').read_text()
WIN = (ROOT / 'tools/release/win/install.ps1').read_text(encoding='utf-8-sig')
REQUIRED = MAC.split("done <<'MAKER_REQUIRED'\n", 1)[1].split('\nMAKER_REQUIRED', 1)[0].splitlines()


def fake_release_root(root):
    """A minimal tree the release script runs in: the script, the version props, a passing build.sh, one page test."""
    release = root / 'tools/release'
    release.mkdir(parents=True)
    shutil.copy(ROOT / 'tools/release/make-release.sh', release)
    shutil.copy(ROOT / 'Directory.Build.props', root)
    (root / 'build.sh').write_text('#!/bin/bash\nexit 0\n')
    (root / 'build.sh').chmod(0o755)
    tests = root / 'src/CwyLs.ModLoader.Maker.Web/tests/ui'
    tests.mkdir(parents=True)
    (tests / 'nested.test.mjs').touch()
    return release


def commit_all(root):
    """Commit everything, so the release script's clean-tree gate passes; the fake tools in bin/ and the log stay ignored."""
    (root / '.gitignore').write_text('bin/\nlog\nout/\n')
    git = ['git', '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'commit.gpgsign=false']
    subprocess.run(['git', 'init', '-q'], cwd=root, check=True)
    subprocess.run(git + ['add', '-A'], cwd=root, check=True)
    subprocess.run(git + ['commit', '-q', '-m', 'fixture'], cwd=root, check=True)


class ReleaseGates(unittest.TestCase):
    def test_payload_lists_cover_shipped_runtime_and_match(self):
        files = sorted(p.relative_to(WEB).as_posix() for p in WEB.rglob('*')
                       if p.is_file() and (p.suffix in {'.js', '.css', '.svg', '.ttf'}
                       or p.name.startswith('OFL-') or p.name == 'index.html')
                       and not set(p.relative_to(WEB).parts) & {'tests', 'tools'}
                       and not p.name.startswith('_'))
        windows_list = WIN.split('function Maker-PayloadComplete {', 1)[1].split('    )', 1)[0]
        self.assertEqual(REQUIRED, files)
        self.assertEqual(REQUIRED, re.findall(r"^        '([^']+)'", windows_list, re.M))
        for content, names in [(MAC, ('full_install()', 'act_install()', 'do_loader()', 'do_maker()')),
                               (WIN, ('function Full-Install {', 'function Act-Install {', 'function Do-Loader {', 'function Do-Maker('))]:
            for name in names:
                body = content.split(name, 1)[1].split('\n}', 1)[0]
                self.assertRegex(body, r'maker_payload_complete|Maker-PayloadComplete')

    def test_mac_rejects_each_missing_file_and_swaps_complete_page(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / 'files/CwyLsModLoader/maker'
            shutil.copytree(WEB, src)
            dest = root / 'game/BepInEx/plugins/CwyLsModLoader'
            (dest / 'maker').mkdir(parents=True)
            (dest / 'maker/stale').write_text('old')
            functions = (MAC.split('maker_payload_complete() {', 1)[1].split('\ndo_loader()', 1)[0])
            functions = 'maker_payload_complete() {' + functions
            functions += '\n' + MAC.split('drop_staged() {', 1)[1].split('\ndo_mods_folder()', 1)[0].join(['drop_staged() {', ''])
            script = root / 'check.sh'
            script.write_text('FILES="$1/files"\nGAME_ROOT="$1/game"\nPLUGIN_DIR_NAME=CwyLsModLoader\n' + functions + '\ndo_maker\n')
            for file in REQUIRED:
                p = src / file
                saved = p.read_bytes()
                p.unlink()
                result = subprocess.run(['bash', str(script), str(root)], capture_output=True, timeout=5)
                self.assertNotEqual(result.returncode, 0, file)
                self.assertEqual((dest / 'maker/stale').read_text(), 'old', file)
                p.write_bytes(saved)
            bindir = root / 'bin'
            bindir.mkdir()
            (bindir / 'cp').write_text('#!/bin/bash\nexit 1\n')
            (bindir / 'cp').chmod(0o755)
            result = subprocess.run(['bash', str(script), str(root)], capture_output=True, timeout=10,
                                    env=dict(os.environ, PATH=str(bindir) + ':' + os.environ['PATH']))
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual((dest / 'maker/stale').read_text(), 'old')
            subprocess.run(['bash', str(script), str(root)], check=True, timeout=10)
            self.assertFalse((dest / 'maker/stale').exists())
            self.assertTrue((dest / 'maker/index.html').is_file())

    def test_gate_propagates_each_failure_and_skip_requires_dry_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            release = root / 'tools/release'
            release.mkdir(parents=True)
            shutil.copy(ROOT / 'tools/release/make-release.sh', release)
            shutil.copy(ROOT / 'Directory.Build.props', root)
            (root / 'build.sh').write_text('#!/bin/bash\nexit 0\n')
            (root / 'build.sh').chmod(0o755)
            tests = root / 'src/CwyLs.ModLoader.Maker.Web/tests/ui'
            tests.mkdir(parents=True)
            (tests / 'nested.test.mjs').touch()
            bindir = root / 'bin'
            bindir.mkdir()
            node = bindir / 'node'
            node.write_text('#!/bin/bash\nprintf "%s\\n" "$*" >> "$LOG"\ncase "$*" in *"$FAIL_CASE"*) exit 9;; esac\n')
            node.chmod(0o755)
            curl = bindir / 'curl'
            curl.write_text('#!/bin/bash\nexit 93\n')
            curl.chmod(0o755)
            env = dict(os.environ, PATH=str(bindir) + ':' + os.environ['PATH'], LOG=str(root / 'log'))
            commit_all(root)
            for fail, count in [('--test', 1), ('tests/e2e/smoke.mjs', 2), ('tests/e2e/smoke-fake.mjs', 3)]:
                log = root / 'log'
                log.unlink(missing_ok=True)
                result = subprocess.run(['bash', str(release / 'make-release.sh')], env=dict(env, FAIL_CASE=fail), capture_output=True, timeout=10)
                self.assertEqual(result.returncode, 9, result.stderr)
                lines = log.read_text().splitlines()
                self.assertEqual(len(lines), count)
                self.assertIn('tests/ui/nested.test.mjs', lines[0])
            for flag in ('--skip-build', '--skip-page-tests'):
                result = subprocess.run(['bash', str(release / 'make-release.sh'), flag], env=env, capture_output=True, timeout=10)
                self.assertEqual(result.returncode, 2)
            result = subprocess.run(['bash', str(release / 'make-release.sh'), '--dry-run', '--output-dir', str(root / 'out'), '--skip-build', '--skip-page-tests'], env=env, capture_output=True, timeout=10)
            self.assertIn(b'missing local vendor archive for dry run', result.stderr)

    def test_release_refuses_a_dirty_tree_and_a_dry_run_only_warns(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            release = fake_release_root(root)
            commit_all(root)
            (root / 'stray.txt').write_text('not committed')
            result = subprocess.run(['bash', str(release / 'make-release.sh')], capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 3, result.stderr)
            self.assertIn(b'stray.txt', result.stderr)
            result = subprocess.run(['bash', str(release / 'make-release.sh'), '--dry-run', '--output-dir', str(root / 'out'),
                                     '--skip-build', '--skip-page-tests'], capture_output=True, timeout=10)
            self.assertIn(b'warning: the working tree is not clean', result.stderr)
            self.assertIn(b'missing local vendor archive for dry run', result.stderr)

    def test_a_file_naming_this_machines_home_stops_even_a_dry_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            release = fake_release_root(root)
            (root / 'leak.mjs').write_text("const shots = '" + os.environ['HOME'] + "/shots';\n")
            (root / 'generic.md').write_text('/Users/someone/Library is fine\n')
            commit_all(root)
            result = subprocess.run(['bash', str(release / 'make-release.sh'), '--dry-run', '--output-dir', str(root / 'out'),
                                     '--skip-build', '--skip-page-tests'], capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 4, result.stderr)
            self.assertIn(b'leak.mjs', result.stderr)
            self.assertNotIn(b'generic.md', result.stderr)

    def test_stale_generated_docs_stop_the_release(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            release = fake_release_root(root)
            bindir = root / 'bin'
            bindir.mkdir()
            node = bindir / 'node'
            node.write_text('#!/bin/bash\nprintf "node %s\\n" "$*" >> "$LOG"\ncase "$*" in *"$FAIL_CASE"*) exit 9;; esac\n')
            node.chmod(0o755)
            apidoc = root / 'tools/apidoc/run.sh'
            apidoc.parent.mkdir(parents=True)
            apidoc.write_text('#!/bin/bash\nprintf "apidoc %s\\n" "$*" >> "$LOG"\nexit 7\n')
            apidoc.chmod(0o755)
            commit_all(root)
            env = dict(os.environ, PATH=str(bindir) + ':' + os.environ['PATH'], LOG=str(root / 'log'))
            result = subprocess.run(['bash', str(release / 'make-release.sh')], env=dict(env, FAIL_CASE='generate_api.mjs'),
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 9, result.stderr)
            self.assertIn('node generate_api.mjs --check', (root / 'log').read_text().splitlines()[-1])
            (root / 'log').unlink()
            result = subprocess.run(['bash', str(release / 'make-release.sh')], env=dict(env, FAIL_CASE='no-such-case'),
                                    capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 7, result.stderr)
            self.assertEqual((root / 'log').read_text().splitlines()[-1], 'apidoc --check')

    def test_font_notices_are_in_inventory_and_payload(self):
        notice = (ROOT / 'THIRD-PARTY-NOTICES.md').read_text()
        for name in ('Outfit', 'NotoSansThai', 'Kanit'):
            self.assertIn('OFL-' + name + '.txt', notice)
            self.assertIn('fonts/OFL-' + name + '.txt', REQUIRED)

    def test_player_maker_excludes_maintainer_docs_and_matches_build(self):
        release_sh = (ROOT / 'tools/release/make-release.sh').read_text()
        build_sh = (ROOT / 'build.sh').read_text()
        rel_block = release_sh.split('rsync -a --delete', 1)[1].split('"$web/"', 1)[0]
        bld_block = build_sh.split('rsync -a --delete --delete-excluded', 1)[1].split('"$WEB_SRC/"', 1)[0]
        self.assertEqual(re.findall(r"--exclude '([^']+)'", rel_block), re.findall(r"--exclude '([^']+)'", bld_block))
        self.assertIn("*.md", re.findall(r"--exclude '([^']+)'", rel_block))


if __name__ == '__main__':
    unittest.main(verbosity=2)
