# Code Signing Policy

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

This policy applies to release artifacts produced from the RdpSwitcher source repository:

- `RdpSwitcher.exe`
- `RdpSwitcher.Plugin.dll`
- `RdpSwitcher-*-win-x64.msi`

Only artifacts built from this repository's source code and release workflow may be submitted for code signing. Third-party or upstream binaries must not be signed using the RdpSwitcher signing subscription.

## Team Roles

The RdpSwitcher project is currently maintained by the repository owner.

- Committers and reviewers: [@idas4you](https://github.com/idas4you)
- Approvers: [@idas4you](https://github.com/idas4you)

Changes from outside contributors must be reviewed by a trusted project maintainer before they are merged. Release signing requests must be approved by an approver.

## Signing Process

Release artifacts are built by GitHub Actions from tagged commits. The intended signing flow is:

1. A release tag is pushed.
2. GitHub Actions builds the native RDC plug-in, publishes the tray app, and packages the MSI.
3. The release artifact is submitted to SignPath for signing with origin verification.
4. A signing approver reviews and approves the signing request.
5. Signed artifacts are attached to the GitHub Release.

The project will not intentionally bypass SignPath.io origin verification, approval requirements, or other technical constraints required for Open Source code signing.

## Privacy Policy

RdpSwitcher does not collect telemetry and does not transfer information to other networked systems unless specifically requested by the user or the person installing or operating it.

Runtime communication is local to the current Windows/RDP environment:

- The remote instance sends a message through the `RDPSWCH` Remote Desktop Dynamic Virtual Channel.
- The host-side `mstsc.exe` plug-in forwards that message to the tray app through the local named pipe `RdpSwitcher.Signal`.
- Logs are written locally to `%LOCALAPPDATA%\RdpSwitcher\RdpSwitcher-yyyy-MM-dd.log`.

RdpSwitcher does not use file-based signaling or RDP drive redirection.

## Security Notes

RdpSwitcher registers a per-user COM in-process server for `RdpSwitcher.Plugin.dll` so that `mstsc.exe` can load the RDC Dynamic Virtual Channel plug-in. The RDC AddIn registry key is removed when the host tray app exits normally. COM registration may remain so the next startup can verify or update it.

Release builds require the plug-in to be installed under `Program Files` or `Program Files (x86)` before automatic COM registration is allowed. Debug builds relax this check for local development.
