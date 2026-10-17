# Third-party components

The battery-query modules under `engine/providers` are adapted from
[HeyOkay/HaloBattery](https://github.com/HeyOkay/HaloBattery), commit
`2251f0534a8e886c9692ead42056059b4f69d8c0` (MIT).
Its copyright and permission notice is preserved in
`engine/licenses/HaloBattery-MIT.txt`.

The upstream application, installer, updater, input hooks, controller mode
switches and user settings are not included. Provider imports are lazy; the
native ATK and HyperX adapters retain ownership of their existing device IDs.
Changes include marking voltage-derived Logitech levels as estimates.

Upstream protocol references and model verification notes are retained in
`engine/upstream-protocols.md`, `engine/upstream-devices.md` and
`engine/upstream-credits.md`. A protocol implementation or a listed USB ID does
not by itself establish hardware validation.

The Windows distribution bundles the official CPython embeddable runtime and
the `hidapi` Python wheel, with their original license files. Versions, original
download URLs and SHA256 hashes are pinned in `engine/dependencies.lock.json`.
No runtime download or package installation is performed on end-user PCs.
