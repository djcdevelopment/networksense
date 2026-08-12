# Extraction provenance

Status: **VERIFIED** on 2026-08-12.

This repository was extracted from
`https://github.com/djcdevelopment/baseline` at the immutable tag
`split-base-20260811`, source commit
`aceb2eb48d770885a2c4171b926867f4ee82b4a4`.

The source tag's final roadmap-only commit contains no NetworkSense-owned path,
so it maps to zero in the filtered history. The last included source commit,
`70982ebcbd3921332374b16ba3b87f49b1339964`, maps to the filtered tip recorded
in [`docs/provenance/commit-map.txt`](docs/provenance/commit-map.txt).

## Filter commands

The clone was pinned before filtering:

```powershell
git clone --no-local --branch split-base-20260811 --single-branch C:\work\baseline C:\work\_extract\networksense
git switch -c main
```

Unrelated source tags were deleted locally. The history filters were:

```text
git filter-repo --force --path network/mod/ComfyNetworkSense --path network/mod/ComfyNetworkSense.Tests --path network/mod/.gitattributes --path network/README.md --path network/tuning-ledger.md --path network/telemetry-and-scores.md --path network/player-opt-in-modes.md --path network/research-framing.md --path network/observability-and-experiments.md --path network/authority-negotiation-ui.md --path network/contracts --path network/design_mocks --path network/tools --path tools/i5 --path tools/am4 --path tools/modpack --path tools/synthetic-baseline-extractor
git filter-repo --force --invert-paths --path network/mcp --path network/mod/ComfyQuestLab --path network/mod/ComfyQuestRuntime --path network/mod/ComfyQuestContracts --path network/mod/ComfyQuestLab.Tests --path tools/i5/Invoke-I5QuestLabBatch.ps1
git filter-repo --force --replace-text history-replacements.txt
```

The replacement changed the deliberate invalid-enrollment fixture literal
`c7-invalid-enrollment-key` to `invalid-enrollment-fixture` throughout history.
Gitleaks classified the old literal as a generic API-key lookalike; source
context proves it is selected only by the `invalid_enrollment` negative test.
No real credential was found.

## Scrub evidence

- `gitleaks 8.30.1 git . --log-opts=--all`: 249 commits and about 2.37 MB
  scanned; **no leaks found** after the fixture rewrite.
- `git filter-repo --analyze`: 253 filtered commits and 865 blob sizes; largest
  unpacked blob 115,412 bytes, with no unexpected large object.
- [`docs/provenance/commit-map.txt`](docs/provenance/commit-map.txt) is the final
  baseline-to-NetworkSense mapping. The three stage maps are retained beside it
  for auditability.
