# Contributing

Thanks for helping improve GameArt Manager!

## Getting started

1. Fork the repository and create a branch from `main`.
2. Install the prerequisites (see the README's *Build & install locally*
   section): Visual Studio 2022 with the UWP workload.
3. `tools\build_local.ps1` builds a signed, installable package locally.

## Pull requests

- Keep PRs small and focused; describe what and why.
- CI must pass: a gitleaks secret scan and an unsigned Debug build run on
  every PR.
- Never commit signing keys (`.pfx`), build output, logs or personal
  data — `.gitignore` covers the usual suspects, but check `git status`
  before pushing.
- PRs are squash-merged into `main`; the source branch is deleted
  automatically after the merge.
- Match the existing code style (C# 7.3-era syntax, classic UWP).

## Licensing

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
