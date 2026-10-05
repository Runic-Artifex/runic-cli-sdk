# Developer experience implementation plan

Recorded 2026-10-05 before implementation. Baseline: `b93314229b1136cac0838c523fe173eb817a2186`.
The user selected developer and translator experience, then authorized implementing all findings using GPT-6.1 Sol subagents. This document is the durable scope and progress record across context compaction.

## Findings and acceptance criteria

| ID | Finding and implementation | Completion criteria | Status |
| --- | --- | --- | --- |
| C1 | Root help omits declared global options (`CommandHelpFormatter.cs:47`). Render visible global options once at root; retain command help semantics. | Regression tests verify root/command help, aliases, hidden options and human/JSON presentation. | Planned |
| C2 | Converter/validator metadata failures surface as errors inside generated code; Boolean flag converters are silently bypassed. Validate closed accessible types and matching converter/validator contracts; diagnose unsupported combinations at attributes. | Invalid metadata gives useful generator diagnostics; valid generated output compiles and executes. | Planned |
| C3 | Completion flattens unrelated commands/options/choices. Add a catalog-driven context query and shell integration, respecting current path/value position, spaces and `--option=value`. | Focused candidate and real shell fixtures exercise nested commands, unrelated options, choices and path hints. | Planned |
| C4 | Localization keys exist but help prints keys and high-level presentation lacks explicit culture/text resolution. Add dependency-free resolver/context APIs, generated key metadata and an optional English/German Translations example. | Existing APIs/default behavior preserved; localized help/errors verified; command tokens, diagnostic codes and protocol identities unchanged; no mandatory Translations dependency. | Planned |
| C5 | Process execution inherits stdin and environment with no bounded-input or isolated-environment controls. Add inherit/closed/bounded-byte stdin modes and explicit environment inheritance. | Existing defaults preserved; tool-chain example plus pressure/cancellation/input-limit tests pass. | Planned |
| C6 | `eng/verify.sh` hardcodes preview.2; README claims a cross-platform NativeAOT matrix but workflow is Ubuntu-only. Read committed version or explicit argument and implement the promised supported-platform coverage. | Changed version authority is used consistently; focused script checks and actual cross-platform workflow are reviewable. | Planned |
| D1 | Root/examples onboarding assumes source checkout. Add a package-only console quick-start using method-first commands and human/JSON execution, based on maintained verified examples. | Commands work against packed candidates in isolation; published vs candidate versions accurately distinguished. | Planned |

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

Implementation has not started at recording time. Root will update this section and the status table as units complete. Claims from initial source review are not browser/shell end-to-end proof until the acceptance checks run.
