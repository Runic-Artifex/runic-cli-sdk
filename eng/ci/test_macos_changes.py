"""eng/ci/macos-changes.sh against eng/ci/macos-paths.txt (CONTRIBUTING.md, "CI triggers and macOS jobs"): which pull
request changes run CI's macOS jobs."""

import os
import pathlib
import subprocess
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parent / "macos-changes.sh"


def decide(paths):
    env = {**os.environ, "GITHUB_OUTPUT": ""}
    output = subprocess.run(["bash", str(SCRIPT), "--stdin"], input="\n".join(paths), text=True,
                            capture_output=True, check=True, env=env).stdout
    return output.strip().splitlines()[-1]


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


if __name__ == "__main__":
    unittest.main()
