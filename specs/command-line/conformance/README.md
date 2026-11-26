# Command Line conformance files

The committed files of the conformance driver's `commandline/1` module
([`eng/conformance/README.md`](../../../eng/conformance/README.md#command-line)):

- `cases.json`: the generated case list, one line per case of the corpus in the directory above, keyed `<file>/<id>`.
  `bun eng/conformance/cli.ts cases --write` rewrites it after a corpus change, and CI checks that it is current.
- `status/<implementation>.json`: each implementation's expected result for every case.
