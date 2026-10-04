# Square Launch Monitor

BLE integration for the **Square Golf** launch monitor (Home device). Implemented
in C# behind the addon's common Bluetooth transport, exposed to GDScript as a
single Godot `Node` (`SquareLaunchMonitor`) that the autoload
`launch_monitor_manager.gd` drives.

> **Protocol reference:** see [`PROTOCOL.md`](PROTOCOL.md) for the full BLE
> protocol map — transport, handshake, command/notification frame layouts, club
> codes, and how the protocol was reverse-engineered.

## Scope

- Supports the Square **"Home"** device. The **Omni** variant is intentionally
  **not** supported.
- Targets both **Windows** (WinRT) and **Linux** (BlueZ/D-Bus) via the shared
  `common/bluetooth/` transport.

## Files

| File | Role |
| ---- | ---- |
| `SquareLaunchMonitor.cs` | Public Godot `Node`. Exposes `StartScan`/`ConnectToDevice`/`SetClub`/… and re-emits session events as Godot signals (`ShotReceived`, `ClubDataReceived`, `ReadyChanged`, `StatusChanged`, …). |
| `SquareConnectionSession.cs` | Connection lifecycle + state machine: handshake, heartbeat, club/ready commands, notification routing, the per-shot club-data request, shot re-arm. |
| `SquareProtocol.cs` | Binary frame parser (`11 01` sensor, `11 02` shot, `11 07` club) incl. invalid-reading sentinel handling and spin decomposition. |
| `SquareCommandBuilder.cs` | Builds outbound command byte frames (`Heartbeat`, `DetectBall`, `Club`, `RequestClubMetrics`). |
| `SquareConnectionOptions.cs` | UUIDs, device-name prefix, and connection/heartbeat timing constants. |
| `SquareShotMetrics.cs` | Parsed shot/sensor/club value records (`SquareClubMetrics`: `null` = not measured). |
| `SquareShotDataMapper.cs` | Maps `SquareShotMetrics` → ball-data dictionary (units + clamp) and `SquareClubMetrics` → GSPro `ClubData` keys. |
| `SquareGodotMapper.cs` | Wraps the mapper output into a Godot `Dictionary`. |
| `square_club_catalog.gd` | Club label → 2-byte Square code lookup (`SquareClubCatalog`). |
| [`PROTOCOL.md`](PROTOCOL.md) | Reverse-engineered BLE protocol notes. |

## Signal flow

```
Square device ──BLE──▶ IBluetoothGattClient ──▶ SquareConnectionSession
                                                       │ events
                                                       ▼
                          SquareLaunchMonitor (Godot Node, [Signal]s)
                                                       │
                                                       ▼
                 launch_monitor_manager.gd (re-emits hit_ball, club_data, etc.)
                                                       │
                                                       ▼
                                            gameplay (Range, Player)
```

`SquareLaunchMonitor` resolves the platform BLE client via
`BluetoothGattClientFactory.Create()` — see [`../README.md`](../README.md) for the
addon's transport contract and how to add a new monitor.

## Club data

After each shot the session asks for the club frame (`11 87` → `11 07`) and
emits it as `ClubDataReceived`; the manager re-emits it as `club_data`. What a
**Home** reports (firmware 1.10.29, verified on hardware):

- **Path, face to target, attack angle and dynamic loft** — only when the club
  face carries Square's reflective **club sticker**. Without it every shot comes
  back *untracked* (all fields "no reading"), which is still emitted so a host can
  say so.
- **No impact location, club speed or smash factor** — the Home's club frame ends
  after dynamic loft; those fields are Omni-only.
- With the putter selected the angles are dropped.

The club frame is tied to the shot frame before it, not to our request write:
on Linux BlueZ can fail that write with `org.bluez.Error.InProgress` while the
device still answers. See [`PROTOCOL.md` §A.3/A.4](PROTOCOL.md).

## Clubs

Club codes live in `square_club_catalog.gd` (`SquareClubCatalog`) and mirror the
`RegularCode` values of the `squaregolf-connector` reference project. The full
table is in [`PROTOCOL.md` §A.5](PROTOCOL.md). Notes:

- `0b06` is the **Approach/Gap wedge (GW)** — the hardware has no distinct lob
  wedge.
- The **alignment stick** (`0008`, `ALIGNMENT_STICK_CODE`) is a special mode
  trigger, not a selectable shot club; the alignment flow that uses it is not yet
  implemented.

## Building & testing

The integration is C#. Build with the host project's C# solution
(`dotnet build <HostProject>.csproj`) or by opening the project in Godot.
`IBluetoothGattClient` is the seam to mock for unit tests; protocol parsing in
`SquareProtocol` is pure and unit-testable without hardware, and
`SquareConnectionSession` is exercised against a fake GATT client (see this
repo's `tests/` project: `cd tests && dotnet test`).
