# Contributing

Issues and pull requests are welcome.

- Open an issue first for anything larger than a bug fix.
- `dotnet build AsyncScheduler.slnx && dotnet test AsyncScheduler.slnx` must pass; add a test for behaviour you change.
- Every configuration option must appear in the README config table (a test enforces this).
- Keep the repo self-contained: no references outside this tree.
- Record notable changes in `CHANGELOG.md`. Every push to `main` publishes, so keep `main` releasable.
