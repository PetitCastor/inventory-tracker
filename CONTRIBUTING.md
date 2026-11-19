# Contributing

Thanks for your interest in Inventory Tracker. Bug reports, log samples and pull requests are all welcome.

## Where to go

| You want to… | Use |
|---|---|
| Report a bug or a wrong item location | [Open an issue](https://github.com/PetitCastor/inventory-tracker/issues/new/choose) with the bug template |
| Suggest a feature | [Open an issue](https://github.com/PetitCastor/inventory-tracker/issues/new/choose) with the feature template |
| Ask a question or share an idea that is not ready yet | [Discussions](https://github.com/PetitCastor/inventory-tracker/discussions) |
| Report a security problem | Privately, as described in [SECURITY.md](SECURITY.md) |

Before opening an issue, search the existing ones and read the [tracking limits](README.md#tracking-limits): some gaps come from what the game logs, not from the tracker.

## Sharing logs

Log excerpts make most bugs fixable. `tracker.log` is in `%LOCALAPPDATA%\InventoryTracker`; `Game.log` is in your Star Citizen `LIVE` or `PTU` folder.

`Game.log` contains your RSI handle and other account details. Share only the lines around the problem, and remove anything you do not want public before posting.

## Pull requests

1. For anything larger than a small fix, open an issue first so we can agree on the approach.
2. Fork the repository and create a branch from `main`.
3. Build and test as described in the [development guide](docs/development.md). All tests must pass.
4. Add tests for new behaviour.
5. Open the pull request against `main`.

Pull requests are squash-merged, and **the PR title becomes the release note** shown in the app's **What's new** popup. Use [Conventional Commits](https://www.conventionalcommits.org/) and write `feat:` and `fix:` titles for players, for example `fix: keep ship lockers after a character reset`. Other types (`docs:`, `test:`, `ci:`, `chore:`) stay out of the app.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
