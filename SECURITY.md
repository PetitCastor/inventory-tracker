# Security policy

## Supported versions

Only the [latest release](https://github.com/PetitCastor/inventory-tracker/releases/latest) receives fixes. The app updates itself at startup, so fixes reach users through a new release.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Report it privately through GitHub instead: open the repository's **Security** tab and choose **Report a vulnerability**, or go directly to [this form](https://github.com/PetitCastor/inventory-tracker/security/advisories/new). Only the maintainer can see the report.

Please include:

- the app version (shown in **Settings**),
- what an attacker could do and under which conditions,
- steps or a proof of concept to reproduce it.

You should get a first reply within a week. Once a fix is released, the advisory is published and you are credited unless you prefer otherwise.

## Scope

In scope: the tracker itself — its local web interface on `localhost:5730`, the update mechanism, log parsing and the files it writes under `%LOCALAPPDATA%\InventoryTracker`.

Out of scope: Star Citizen, the RSI Launcher, the Star Citizen Wiki API and GitHub themselves. Report problems with those to their owners.
