"""eng/ci/macos-changes.sh against eng/ci/macos-paths.txt and eng/ci/full-run-paths.txt (CONTRIBUTING.md, "CI triggers and
macOS jobs"): which pull requests and pushes to main run CI's macOS jobs, and which runs are full."""

import os
import pathlib
import subprocess
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parent / "macos-changes.sh"


def run(paths, event=None):
    env = {**os.environ, "GITHUB_OUTPUT": ""}
    arguments = ["--event", event] if event else []
    output = subprocess.run(["bash", str(SCRIPT), *arguments, "--stdin"], input="\n".join(paths), text=True,
                            capture_output=True, check=True, env=env).stdout
    return output.strip().splitlines()


def decide(paths, event=None):
    return run(paths, event)[-1]


def decision(paths, event=None):
    """Both decision lines: a run that runs the macOS jobs runs every job, so it is full."""
    return " ".join(run(paths, event)[-2:])


ORDINARY_MERGE = ["README.md", "examples/command-line/Program.cs", "specs/x.json", "eng/release/release.py"]


class MacosChangesTests(unittest.TestCase):
    def test_documentation_examples_and_release_tooling_skip_the_macos_jobs(self):
        self.assertEqual(decide(["README.md", "docs/guides/command-line/wpf.md", "specs/x.json",
                                 "packages/dotnet/Runic.CommandLine/README.md"]), "macos=false")
        self.assertEqual(decide(["examples/command-line/Program.cs", "eng/release/release.py",
                                 ".github/workflows/publish-preview.yml", "eng/branding/command-line/icon.png"]),
                         "macos=false")
        self.assertEqual(decide([]), "macos=false")

    def test_sources_tests_ci_files_and_pins_run_the_macos_jobs(self):
        for path in [
            "packages/dotnet/Runic.CommandLine.Processes/ProcessRunner.cs",
            "packages/dotnet/Runic.CommandLine/PublicAPI.Unshipped.txt",
            "tests/dotnet/Runic.CommandLine.Tests/HelpTests.cs",
            "tests/native/Runic.CommandLine.AotSmoke/Program.cs",
            "tests/fixtures/command-line/package-consumer/Program.cs",
            "eng/build/common.props",
            "eng/test.sh",
            ".github/workflows/ci.yml",
            "eng/ci/macos-paths.txt",
            "global.json",
            "eng/Versions.props",
            "Directory.Packages.props",
        ]:
            with self.subTest(path=path):
                self.assertEqual(decide(["README.md", path]), "macos=true")


class EventTests(unittest.TestCase):
    def test_a_pull_request_decides_by_the_macos_paths_alone_also_without_event(self):
        self.assertEqual(decision(ORDINARY_MERGE, "pull_request"), "full=false macos=false")
        self.assertEqual(decision(["eng/release/notes/0.15.0.md", ".github/workflows/publish-preview.yml"], "pull_request"),
                         "full=false macos=false")
        self.assertEqual(decision(["eng/release/notes/0.15.0.md", "eng/Versions.props"], "pull_request"), "full=true macos=true")
        self.assertEqual(decision(ORDINARY_MERGE), "full=false macos=false")

    def test_an_ordinary_merge_to_main_skips_the_macos_jobs_and_one_that_changes_a_macos_path_runs_them(self):
        self.assertEqual(decision(ORDINARY_MERGE, "push"), "full=false macos=false")
        self.assertEqual(decision([], "push"), "full=false macos=false")
        self.assertEqual(decision([*ORDINARY_MERGE, "packages/dotnet/Runic.CommandLine/CommandApp.cs"], "push"), "full=true macos=true")

    def test_a_version_or_release_commit_on_main_runs_every_job(self):
        for path in ["eng/Versions.props", ".github/workflows/publish-preview.yml"]:
            with self.subTest(path=path):
                output = run([*ORDINARY_MERGE, path], "push")
                self.assertEqual(" ".join(output[-2:]), "full=true macos=true")
                self.assertIn(f"Version or release path: {path}", output)
        # Ordinary pull requests write their entries into the release notes.
        self.assertEqual(decision(["eng/release/notes/0.15.0.md"], "push"), "full=false macos=false")

    def test_nightly_manual_and_multi_commit_push_runs_run_every_job(self):
        for event in ["schedule", "workflow_dispatch", "push-range"]:
            with self.subTest(event=event):
                output = run(ORDINARY_MERGE, event)
                self.assertEqual(" ".join(output[-2:]), "full=true macos=true")
                self.assertIn(f"A {event} run runs every job.", output)

    def test_the_changes_job_runs_the_rule_for_every_event_and_marks_only_full_runs_outside_pull_requests(self):
        workflow = (SCRIPT.parents[2] / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn('bash eng/ci/macos-changes.sh --event "$event" HEAD^1 HEAD', workflow)
        self.assertIn("name: ci-full-run", workflow)
        self.assertEqual(workflow.count("if: steps.paths.outputs.full == 'true' && github.event_name != 'pull_request'"), 2)


if __name__ == "__main__":
    unittest.main()
