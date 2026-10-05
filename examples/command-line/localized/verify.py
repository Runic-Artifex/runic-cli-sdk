#!/usr/bin/env python3
"""Verify the maintained localized example against its already-built assembly."""
import argparse
import json
import os
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--configuration", default="Release")
options = parser.parse_args()
project = Path(__file__).with_name("LocalizedCli.csproj")

def run(culture, arguments, expected=0):
    result = subprocess.run(
        ["dotnet", "run", "--project", str(project), "--configuration", options.configuration,
         "--no-build", "--", *arguments],
        env={**os.environ, "RCLI_EXAMPLE_CULTURE": culture, "RUNIC_COMMANDLINE_OUTPUT": "human"},
        capture_output=True, text=True, timeout=30, check=False)
    assert result.returncode == expected, (culture, arguments, result.returncode, result.stderr)
    return result

for culture, usage, description, greeting, error in [
    ("en", "Usage", "Greet a person", "Hello Ada", "An unrecognized option was supplied."),
    ("de", "Aufruf", "Eine Person begrüßen", "Hallo Ada", "Eine unbekannte Option wurde angegeben."),
]:
    help_text = run(culture, ["greet", "--help"]).stdout
    assert help_text.startswith(f"{usage}: localized greet <name>"), help_text
    assert description in help_text and "--output <human|json>" in help_text, help_text
    assert "commands.greet" not in help_text, help_text
    assert run(culture, ["greet", "Ada"]).stdout == greeting + "\n"
    for arguments, expected_code in [(["greet", "Ada", "--output=json"], 0),
                                      (["greet", "--unknown", "--output=json"], 2)]:
        output = run(culture, arguments, expected_code)
        assert output.stderr == "", output.stderr
        assert output.stdout.count("\n") == 1, output.stdout
        frame = json.loads(output.stdout)
        assert frame["protocol"] == "runic.commandline/1" and frame["command"] == "greet"
        assert frame["exitCode"] == expected_code
        if expected_code == 0:
            assert frame["payloadType"] == "runic.text/1" and frame["payload"] == greeting
        else:
            assert frame["fault"]["code"] == "RCLI1001" and frame["fault"]["message"] == error
            diagnostic = frame["diagnostics"][0]
            assert diagnostic["code"] == "RCLI1001" and diagnostic["messageKey"] == "diagnostics.unknown-option"
            assert diagnostic["arguments"] == ["--unknown"] and diagnostic["commandPath"] == ["greet"]
    print(f"PASS localized example {culture}: help, greeting, JSON identities and diagnostic")
