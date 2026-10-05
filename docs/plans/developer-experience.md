# Developer experience implementation plan

Recorded 2026-10-05 before implementation. Baseline: `b93314229b1136cac0838c523fe173eb817a2186`.
The user selected developer and translator experience, then authorized implementing all findings using GPT-6.1 Sol subagents. This document is the durable scope and progress record across context compaction.

## Findings and acceptance criteria

| ID | Finding and implementation | Completion criteria | Status |
| --- | --- | --- | --- |
| C1 | Root help omits declared global options (`CommandHelpFormatter.cs:47`). Render visible global options once at root; retain command help semantics. | Regression tests verify root/command help, aliases, hidden options and human/JSON presentation. | Implemented; reviewed |
| C2 | Converter/validator metadata failures surface as errors inside generated code; Boolean flag converters are silently bypassed. Validate closed accessible types and matching converter/validator contracts; diagnose unsupported combinations at attributes. | Invalid metadata gives useful generator diagnostics; valid generated output compiles and executes. | Implemented; reviewed |
| C3 | Completion flattens unrelated commands/options/choices. Add a catalog-driven context query and shell integration, respecting current path/value position, spaces and `--option=value`. | Focused candidate and real shell fixtures exercise nested commands, unrelated options, choices and path hints. | Implemented; reviewed; native shell fixtures passed |
| C4 | Localization keys exist but help prints keys and high-level presentation lacks explicit culture/text resolution. Add dependency-free resolver/context APIs, generated key metadata and an optional English/German Translations example. | Existing APIs/default behavior preserved; localized help/errors verified; command tokens, diagnostic codes and protocol identities unchanged; no mandatory Translations dependency. | Implemented; reviewed |
| C5 | Process execution inherits stdin and environment with no bounded-input or isolated-environment controls. Add inherit/closed/bounded-byte stdin modes and explicit environment inheritance. | Existing defaults preserved; tool-chain example plus pressure/cancellation/input-limit tests pass. | Implemented; reviewed |
| C6 | `eng/verify.sh` hardcodes preview.2; README claims a cross-platform NativeAOT matrix but workflow is Ubuntu-only. Read committed version or explicit argument and implement the promised supported-platform coverage. | Changed version authority is used consistently; focused script checks and actual cross-platform workflow are reviewable. | Implemented; reviewed; Linux final candidates passed; Windows/macOS CI pending |
| D1 | Root/examples onboarding assumes source checkout. Add a package-only console quick-start using method-first commands and human/JSON execution, based on maintained verified examples. | Commands work against packed candidates in isolation; published vs candidate versions accurately distinguished. | Implemented; reviewed; isolated final candidates passed |

## Architecture and scope

Preserve immutable catalogs, explicit composition, reflection-free generated binding, shared standalone/hosted execution, sanitized exceptions, invocation-local consoles and existing adversarial protocol/process coverage. Keep CLI and Translations independently versioned. Localized text is presentation; machine identifiers remain stable. Process input must remain bounded and cancellation-safe.

Related Translations work is recorded in `Runic-Artifex/runic-translations-sdk`, `docs/plans/authoring-experience.md`: MF2-native composer persistence, semantic quality checks/suggestions, translator context/examples, precise diagnostics/shared quick fixes, reusable IDE previews, and onboarding corrections.

## Working protocol

- Root orchestrates; subagents own disjoint implementation areas and meaningful focused tests. Agents do not stage, commit, push or modify another owner's files without coordination. Root alone integrates Git changes.
- Read the locked flake/env/development instructions; reuse caches. No global toolchain installs, path flakes, duplicate full matrices or broad cleanup.
- Do focused checks while iterating; one coordinated final local verification per repo and one GitHub CI run per final push. New changes/failures justify affected reruns.
- Independent review is required for completed units. Record owner, validation, review findings and final PR/commit below.
- Publishing registry packages, release tags and merging PRs are not part of this implementation.

## Progress and evidence

Initial plan committed as `f781845` before implementation. Active owners: `cli_help_completion_impl` C1/C3 and core test registration; `cli_authoring_localization_impl` C2/C4; `cli_process_impl` C5; `cli_docs_release_impl` C6/D1, solution/example integration and flake shell tools. Root alone stages/commits; `cli_independent_review` reviews completed units.

Contract decisions: dependency-free `CommandTextContext`/resolver with compatible formatter overload; native Bash/zsh/fish/PowerShell completion engines instead of a hidden Bash bridge; process stdin defaults inherit, bounded immutable bytes/closed modes and explicit environment inheritance; fish/zsh supplied by pinned development shell. New example directories are excluded from the parent project's compile glob and added to solution by one owner.

C5 completed: final Verification-mode harness passed 39 process tests with zero warnings, including copied bounded bytes/EOF/inherited stdin, 2 MiB duplex pressure, blocked-input timeout/cancellation, broken pipe, descendant-held stdin and isolated effective PATH. ProcessInput example produced expected normalized text/count/health. Independent source/API/docs/example review approved. Final integrated candidate verification remains pending. C1/C3 core syntax checkpoint builds with zero warnings/errors; actual completion fixtures pending. No full verification matrix has been repeated.

Root updates statuses as units receive completed checks/review. Initial source-review claims are not browser/shell end-to-end proof until acceptance checks run.

C1 root/scoped human+JSON help independently approved. C2/C4 Verification tests passed 10/10 with nullable converter/validator output compilation and execution, hosted culture isolation and sanitized localized errors; actual English/German example builds against public Translations preview.1 with zero warnings/errors. C6/D1 focused tests cover committed/explicit version, host RID, valid SemVer metadata normalization and real isolated tutorial installation. C3 native shell fixtures passed initial paths, but independent real terminal tests found spaced-choice quoting, cursor position and multivalue edge cases; affected corrections/regressions remain in progress. Do not run final candidate verification until C3 signoff.

Final checkpoint: C3 corrections received independent approval. Focused completion/help suite passed 7/7, including native Bash/zsh terminal insertion, fish completion, and PowerShell completion/AST round trips and cursor handling. The tests cover quoting, spaced values, option ownership and multivalue boundaries. Local Bash was 5.3; macOS Bash 3 was not locally executed.

The coordinated final `direnv exec . ./eng/verify.sh` passed: 294 managed tests (36 contracts, 17 hosting, 39 process, 202 core), maintained examples, English/German localization assertions, source Linux NativeAOT, four fresh NuGet candidates, and isolated managed/NativeAOT package consumers and package-only tutorial. Evidence is retained in `artifacts/verification/developer-experience-final.log` and `developer-experience-final-report.json`. A final completion README correction was repacked into the core package; assemblies and dependency metadata were hash-identical. Windows/macOS verification remains GitHub CI work. All C1–C6 and D1 source changes are implemented and reviewed; earlier pending entries above describe intermediate checkpoints.

PR #1's first hosted producer exposed a fixture initialization prompt: zsh `compinit -D` asks about insecure hosted-runner completion directories. The fixture now uses `compinit -i -D`, retaining the audit and excluding insecure paths. A deliberately unsafe task-owned directory and completion sentinel verify exclusion before the unchanged 11 real ZLE insertion cases. The old prompt was reproduced; the corrected affected fixture passed with no skips, zero build warnings/errors, and independent review. Shipping completion code is unchanged by this CI-only repair.
