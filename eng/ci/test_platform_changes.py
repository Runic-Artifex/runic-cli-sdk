"""eng/ci/platform-changes.sh against eng/ci/macos-paths.txt, eng/ci/windows-paths.txt and eng/ci/full-run-paths.txt
(CONTRIBUTING.md, "CI triggers and platform jobs"): which pull requests and pushes to main run CI's macOS and Windows
jobs, and which runs are full."""

import fnmatch
import os
import pathlib
import re
import subprocess
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parent / "platform-changes.sh"
ROOT = SCRIPT.parents[2]
NONE, MACOS, WINDOWS, BOTH = "false false false", "true false false", "false true false", "true true true"
ORDINARY_MERGE = ["README.md", "examples/command-line/Program.cs", "specs/x.json", "eng/release/release.py"]


def run(paths, event=None):
    env = {**os.environ, "GITHUB_OUTPUT": ""}
    arguments = ["--event", event] if event else []
    output = subprocess.run(["bash", str(SCRIPT), *arguments, "--stdin"], input="\n".join(paths), text=True,
                            capture_output=True, check=True, env=env).stdout
    return output.strip().splitlines()


def decide(paths, event=None):
    """The three decision lines as `macos windows full`, each true or false."""
    return " ".join(line.split("=", 1)[1] for line in run(paths, event)[-3:])


class PathTests(unittest.TestCase):
    def test_platform_neutral_changes_skip_both_platforms(self):
        for paths in [
            ["README.md", "docs/guides/command-line/wpf.md", "specs/x.json", "packages/dotnet/Runic.CommandLine/README.md",
             "packages/dotnet/Runic.CommandLine/PublicAPI.Unshipped.txt"],
            ["examples/command-line/Program.cs", "eng/release/release.py", ".github/workflows/publish-preview.yml",
             "eng/branding/command-line/icon.png", "eng/release/notes/0.15.0.md"],
            # Parsing, binding, help, output envelopes, the catalog, the generator, the hosting adapter, terminal cells.
            ["packages/dotnet/Runic.CommandLine/Parsing/CommandLineParser.cs",
             "packages/dotnet/Runic.CommandLine/Application/CommandHelpFormatter.cs",
             "packages/dotnet/Runic.CommandLine/Application/TerminalCells.cs",
             "packages/dotnet/Runic.CommandLine/Output/CommandJsonEnvelopeWriter.cs",
             "packages/dotnet/Runic.CommandLine/Hosting/CommandLineHostingAdapter.cs",
             "packages/dotnet/Runic.CommandLine.Generators/CommandLineGenerator.cs",
             "packages/dotnet/Runic.CommandLine.Testing/CommandTestHelpers.cs"],
            # The corpus tests of the spec pull requests, the conformance driver, the rule itself, Linux-only CI scripts.
            ["tests/dotnet/Runic.CommandLine.Tests/GrammarCorpusTests.cs", "tests/dotnet/Runic.CommandLine.Tests/ProtocolCorpusTests.cs",
             "tests/dotnet/Runic.CommandLine.Tests/CorpusModel.cs", "eng/conformance/core/driver.ts",
             "eng/ci/platform-changes.sh", "eng/ci/macos-paths.txt", "eng/ci/native-musl.sh", "eng/ci/apt-install.sh"],
            ["tests/dotnet/Runic.CommandLine.Tests/packages.lock.json", "NuGet.config", "Runic.CommandLine.slnx", "eng/pack.sh"],
            [],
        ]:
            with self.subTest(paths=paths):
                self.assertEqual(decide(paths), NONE)

    def test_processes_console_paths_nativeaot_ci_and_pins_run_both_platforms(self):
        for path in [
            "packages/dotnet/Runic.CommandLine.Processes/ProcessRunner.cs",
            "packages/dotnet/Runic.CommandLine.Processes/Internal/ChildProcess.cs",
            "packages/dotnet/Runic.CommandLine/Application/CommandApp.cs",
            "packages/dotnet/Runic.CommandLine/Application/SystemCommandConsole.cs",
            "packages/dotnet/Runic.CommandLine/Application/PromptEngine.cs",
            "packages/dotnet/Runic.CommandLine.Spectre/SpectreCommandConsole.cs",
            "packages/dotnet/Runic.CommandLine/Parsing/CommandResponseFiles.cs",
            "packages/dotnet/Runic.CommandLine/Execution/CommandInputValidation.cs",
            "tests/dotnet/Runic.CommandLine.Processes.Tests/ProcessRunnerTests.cs",
            "tests/dotnet/Runic.CommandLine.Tests/SignalTests.cs",
            "tests/dotnet/Runic.CommandLine.Tests/Runic.CommandLine.Tests.csproj",
            "tests/fixtures/command-line/CommandLine.Kernel/Program.cs",
            "tests/native/Runic.CommandLine.AotSmoke/Program.cs",
            "tests/fixtures/command-line/package-consumer/Runic.CommandLine.PackageConsumer/Program.cs.in",
            ".github/workflows/ci.yml",
            "eng/test.sh",
            "eng/ci/native-aot.ps1",
            "eng/build/common.props",
            "global.json",
            "Directory.Packages.props",
        ]:
            with self.subTest(path=path):
                self.assertEqual(decide(["README.md", path]), BOTH)

    def test_one_platforms_native_code_runs_only_that_platform(self):
        self.assertEqual(decide(["packages/dotnet/Runic.CommandLine.Processes/Internal/UnixNative.cs"]), MACOS)
        self.assertEqual(decide(["packages/dotnet/Runic.CommandLine.Processes/Internal/UnixGroupProcess.cs"]), MACOS)
        self.assertEqual(decide(["packages/dotnet/Runic.CommandLine.Processes/Internal/WindowsNative.cs"]), WINDOWS)
        self.assertEqual(decide([".gitattributes"]), WINDOWS)

    def test_every_path_entry_of_both_lists_names_a_tracked_file(self):
        tracked = subprocess.run(["git", "-C", str(ROOT), "ls-files"], capture_output=True, text=True, check=True).stdout.splitlines()
        for name in ["macos-paths.txt", "windows-paths.txt"]:
            for line in (ROOT / "eng/ci" / name).read_text(encoding="utf-8").splitlines():
                pattern = line.split("#", 1)[0].strip()
                if not pattern or pattern.startswith("!"):
                    continue
                with self.subTest(list=name, pattern=pattern):
                    # fnmatch's `*` also matches `/`, as the script's globs do.
                    self.assertTrue(any(fnmatch.fnmatchcase(path, pattern) for path in tracked), f"{pattern} matches no tracked file")


class EventTests(unittest.TestCase):
    def test_a_pull_request_decides_by_the_platform_lists_alone_also_without_event(self):
        self.assertEqual(decide(ORDINARY_MERGE, "pull_request"), NONE)
        self.assertEqual(decide([".github/workflows/publish-preview.yml"], "pull_request"), NONE)
        self.assertEqual(decide(["eng/Versions.props"], "pull_request"), NONE)
        self.assertEqual(decide(ORDINARY_MERGE), NONE)

    def test_an_ordinary_merge_to_main_skips_both_and_a_platform_path_runs_that_platform(self):
        self.assertEqual(decide(ORDINARY_MERGE, "push"), NONE)
        self.assertEqual(decide([], "push"), NONE)
        self.assertEqual(decide([*ORDINARY_MERGE, "packages/dotnet/Runic.CommandLine/Application/CommandApp.cs"], "push"), BOTH)
        output = run([*ORDINARY_MERGE, "packages/dotnet/Runic.CommandLine.Processes/Internal/WindowsNative.cs"], "push")
        self.assertEqual(" ".join(line.split("=", 1)[1] for line in output[-3:]), WINDOWS)
        self.assertIn("Windows path: packages/dotnet/Runic.CommandLine.Processes/Internal/WindowsNative.cs", output)
        self.assertFalse(any(line.startswith("macOS path:") for line in output))

    def test_a_version_or_release_commit_on_main_runs_every_job(self):
        for path in ["eng/Versions.props", ".github/workflows/publish-preview.yml"]:
            with self.subTest(path=path):
                output = run([*ORDINARY_MERGE, path], "push")
                self.assertEqual(decide([*ORDINARY_MERGE, path], "push"), BOTH)
                self.assertIn(f"Version or release path: {path}", output)
        # Ordinary pull requests write their entries into the release notes.
        self.assertEqual(decide(["eng/release/notes/0.15.0.md"], "push"), NONE)

    def test_nightly_manual_and_multi_commit_push_runs_run_every_job(self):
        for event in ["schedule", "workflow_dispatch", "push-range"]:
            with self.subTest(event=event):
                self.assertEqual(decide(ORDINARY_MERGE, event), BOTH)
                self.assertIn(f"A {event} run runs every job.", run(ORDINARY_MERGE, event))

    def test_the_changes_job_gates_both_platforms_and_marks_only_full_runs_outside_pull_requests(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn('bash eng/ci/platform-changes.sh --event "$event" HEAD^1 HEAD', workflow)
        self.assertIn("name: ci-full-run", workflow)
        self.assertEqual(workflow.count("if: steps.paths.outputs.full == 'true' && github.event_name != 'pull_request'"), 2)
        self.assertEqual(workflow.count("if: needs.changes.outputs.macos == 'true'"), 2)
        self.assertEqual(workflow.count("if: needs.changes.outputs.windows == 'true'"), 2)
        self.assertIn("MACOS_JOBS: managed-macos native-aot-macos", workflow)
        self.assertIn("WINDOWS_JOBS: managed-windows native-aot-windows", workflow)
        # The always-run native-aot job keeps only Linux legs; the Windows legs are in native-aot-windows.
        legs = re.search(r"^  native-aot:\n(.*?)^  native-aot-windows:\n", workflow, re.M | re.S).group(1)
        self.assertNotIn("os: windows", legs)
        self.assertNotIn("rid: win-", legs)


if __name__ == "__main__":
    unittest.main()
