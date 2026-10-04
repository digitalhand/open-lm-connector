# Open Launch Connector a Godot Addon

Launch-monitor integrations for Godot games. Each monitor is a Godot `Node` that emits `hit_ball(Dictionary)` and related lifecycle signals; the autoload `launch_monitor_manager.gd` orchestrates connections and forwards shot data to the host's gameplay layer.

## Layout

Paths below are relative to the addon root (wherever the addon is installed
under the host's `addons/`); the manager resolves its scripts relative to its
own location, so the install folder name doesn't matter.

```
<addon root>/
├── plugin.cfg / plugin.gd      # registers the LaunchMonitorManager autoload on enable
├── launch_monitor_manager.gd   # the autoload
├── square/                     # Square launch monitor (BLE)
│   ├── SquareLaunchMonitor.cs  # public Godot Node, signals + manager facade
│   ├── SquareConnectionSession.cs
│   ├── SquareProtocol.cs       # binary packet parser
│   ├── Square*.cs              # command builder, options, mappers
└── common/                     # shared plumbing — not launch monitors themselves
    ├── bluetooth/              # BLE GATT transport (LaunchMonitors.Common.Bluetooth)
    │   ├── IBluetoothGattClient.cs
    │   ├── BluetoothGattClientFactory.cs
    │   ├── linux/              # BlueZ over D-Bus (Tmds.DBus)
    │   └── windows/            # Windows.Devices.Bluetooth (compiled only on Windows builds)
    └── tcp_server/
        └── TcpServer.cs        # JSON-over-TCP shot listener (LaunchMonitors.Common.Tcp)
```

### Convention

- **`<monitor>/`** — one folder per physical launch monitor. Public Godot `Node` entrypoint plus implementation files.
- **`common/`** — shared plumbing used by monitors or by the gameplay layer to receive shots from external systems. **Not launch monitors themselves.**
  - `common/bluetooth/` — transport layer used by monitor implementations (currently only Square consumes it).
  - `common/tcp_server/` — inbound network listener; receives shot data from *external* launch monitors over TCP, not a monitor itself.

## Pieces

### `launch_monitor_manager.gd` (autoload)

Registered as the `LaunchMonitorManager` autoload when the plugin is enabled (`plugin.gd` calls `add_autoload_singleton`). Owns the active monitor instance, exposes scan/connect APIs to UI, and re-emits the monitor's signals so host gameplay code doesn't need to know which monitor is connected. Square also emits `club_data(Dictionary)` (GSPro `ClubData` keys, unmeasured keys omitted) shortly after each `hit_ball`; an empty dictionary means the device did not track the club (on a Home: no club sticker). A Home never reports impact location, club speed or smash — see [`square/README.md`](square/README.md#club-data).

The manager owns and persists its own settings (`user://launch_monitor.cfg`): `set_enabled`/`is_enabled`, `set_provider`/`get_provider` (`PiTrac` or `Square`), `set_tcp_port`, `set_club_code`, `set_handedness`, `set_selected_device_id` (+ matching getters). Every change emits `setting_changed(key, value)`. A host with its own settings system forwards values into these setters; a vanilla project needs no extra wiring.

### `square/`

`SquareLaunchMonitor` wraps `SquareConnectionSession`, which drives the BLE GATT lifecycle through `IBluetoothGattClient` (resolved at runtime by `BluetoothGattClientFactory`). Square is the only consumer of `common/bluetooth/` today.

See [`square/README.md`](square/README.md) for the integration overview and [`square/PROTOCOL.md`](square/PROTOCOL.md) for the reverse-engineered BLE protocol map.

### `common/bluetooth/`

Cross-platform BLE GATT abstraction. `BluetoothGattClientFactory.Create()` picks the platform implementation:

- **Linux** → `LinuxBluetoothGattClient` via BlueZ over D-Bus. Requires the BlueZ daemon to be running.
- **Windows** → `WindowsBluetoothGattClient` via WinRT (loaded reflectively; compiled only when `GodotTargetPlatform == windows`). The exclusion lives in the host project's `.csproj`.
- **Other** → `UnsupportedBluetoothGattClient` (throws on use).

`IBluetoothGattClient` is the seam unit tests mock against (`tests/SquareClubSessionTests.cs` drives a real `SquareConnectionSession` through a fake client).

### `common/tcp_server/`

`TcpServer` is a Godot `Node` that listens on TCP port `49152` for JSON shot payloads and emits `HitBall(Dictionary)`. This is how the host project accepts shots from external monitors (PiTrac and other network monitors) over the network. It can run in two modes:

- **Manager mode** — the `LaunchMonitorManager` autoload creates it when the PiTrac provider is active, and acks `{"Code":200}` as it forwards each shot.
- **Embedded mode** — attach the node to a scene and connect `HitBall` to your own handler. The server does **not** auto-ack a parsed shot; after validating, call `RespondShotAccepted()` (200) or `RespondShotRejected()` (501) so the sender's ack reflects real validation. Malformed payloads are always NACKed internally (`501`/`413`).

## Adding a new launch monitor

1. Create a `<monitor>/` folder under the addon root with a public Godot `Node` subclass that emits the same signals (`hit_ball`, `status_changed`, `error_occurred`, `battery_changed`, `firmware_changed`, `ready_changed`).
2. If the monitor uses BLE, depend on `LaunchMonitors.Common.Bluetooth.IBluetoothGattClient` (resolve via the factory). For other transports, add a sibling folder under `common/` (e.g. `common/serial/`) — don't put transports inside the monitor's folder.
3. Wire the monitor into `launch_monitor_manager.gd` so UI can select it.

## Adding a new transport under `common/`

Mirror the `bluetooth/` shape: an `I<Transport>Client.cs` interface, a `<Transport>ClientFactory.cs` that picks the platform impl, and per-platform subfolders (`linux/`, `windows/`, …) with the conditional-compile exclusion added to the host project's `.csproj` if needed. Namespace under `LaunchMonitors.Common.<Transport>`.
