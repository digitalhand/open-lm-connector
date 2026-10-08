using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LaunchMonitors.Common.Bluetooth;

namespace LaunchMonitors.Square;

internal sealed class SquareConnectionSession : IAsyncDisposable
{
    private const string DeviceNotReadyMessage = "Square device was detected but is not ready yet. Wait a moment and try connecting again.";
    private const int ClubRequestAttempts = 3;
    private static readonly TimeSpan ClubRequestRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _armLock = new(1, 1);
    private readonly IBluetoothGattClient _bluetoothClient;
    private readonly SquareConnectionOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<string> _logInfo;
    private readonly Action<string> _logError;
    private Timer? _heartbeatTimer;
    private byte _sequence;
    private string? _lastPayload;
    private int _shotCount;
    // The shot (by _shotCount) whose club frame is still expected; 0 = none.
    private int _awaitingClubForShot;
    private string _clubCode = SquareCommandBuilder.DriverClubCode;
    private int _handedness;
    private bool _swingStick;
    private int _spinMode = 1;
    private int _lastArmedSpinMode = 1;
    private bool _isArmed;
    private bool _shotPendingRearm;
    private bool _isConnected;

    public SquareConnectionSession(
        IBluetoothGattClient bluetoothClient,
        SquareConnectionOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<string>? logInfo = null,
        Action<string>? logError = null)
    {
        _bluetoothClient = bluetoothClient;
        _options = options ?? SquareConnectionOptions.Default;
        _delayAsync = delayAsync ?? Task.Delay;
        _logInfo = logInfo ?? (_ => { });
        _logError = logError ?? (_ => { });

        _bluetoothClient.DeviceDiscovered += OnDeviceDiscovered;
        _bluetoothClient.CharacteristicValueChanged += OnCharacteristicValueChanged;
    }

    public event Action<BluetoothDevice>? DeviceDiscovered;

    public event Action<string>? StatusChanged;

    public event Action<string>? ErrorOccurred;

    public event Action<int>? BatteryChanged;

    public event Action<string>? FirmwareChanged;

    public event Action<bool>? ReadyChanged;

    public event Action<SquareShotMetrics>? ShotReceived;

    public event Action<SquareClubMetrics>? ClubMetricsReceived;

    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        _logInfo("StartScan requested.");
        await StopScanAsync(cancellationToken);
        EmitStatus("Scanning");
        await _bluetoothClient.StartScanAsync(new BluetoothScanOptions(_options.DeviceNamePrefix), cancellationToken);
    }

    public async Task StopScanAsync(CancellationToken cancellationToken = default)
    {
        _logInfo("StopScan requested.");
        await _bluetoothClient.StopScanAsync(cancellationToken);
    }

    public async Task ConnectToDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            EmitError("No Square device was selected.");
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await StopScanAsync(cancellationToken);
            await DisconnectCoreAsync(cancellationToken);
            EmitStatus("Connecting");

            await _bluetoothClient.ConnectAsync(deviceId, CreateBluetoothConnectionOptions(), cancellationToken);
            _isConnected = true;

            await ReadDeviceInfoAsync(cancellationToken);
            await SubscribeToNotificationsAsync(cancellationToken);
            EmitStatus("Connected");
            await WriteCommandAsync(SquareCommandBuilder.Heartbeat(NextSequence()), cancellationToken);
            await _delayAsync(_options.ConnectionClubDelay, cancellationToken);
            await WriteCommandAsync(ClubCommand(), cancellationToken);
            await _delayAsync(_options.ConnectionReadyDelay, cancellationToken);
            await SetReadyAsync(cancellationToken);
            StartHeartbeat();
            _logInfo("Connection sequence complete.");
        }
        catch (Exception ex) when (IsTransientConnectFailure(ex))
        {
            EmitError(DeviceNotReadyMessage);
            EmitStatus("Disconnected");
            _logInfo($"ConnectToDeviceAsync deferred: {ex.Message}");
            await DisconnectCoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            EmitError($"Square connection failed: {ex.Message}");
            EmitStatus("Disconnected");
            _logError($"ConnectToDeviceAsync failed: {ex}");
            await DisconnectCoreAsync(CancellationToken.None);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logInfo("DisconnectAsync requested.");
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync(cancellationToken);
            EmitStatus("Disconnected");
            EmitReady(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task SetClubAsync(string clubCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubCode))
        {
            clubCode = SquareCommandBuilder.DriverClubCode;
        }

        _clubCode = clubCode;
        _logInfo($"SetClub requested. clubCode={_clubCode}");

        await SendClubAsync(cancellationToken);
    }

    /// <summary>Square's swing stick instead of a real club: the selected club is sent by its swing stick code.</summary>
    public async Task SetSwingStickAsync(bool swingStick, CancellationToken cancellationToken = default)
    {
        if (_swingStick == swingStick)
        {
            return;
        }

        _swingStick = swingStick;
        _logInfo($"SetSwingStick requested. swingStick={_swingStick}");
        await SendClubAsync(cancellationToken);
    }

    private async Task SendClubAsync(CancellationToken cancellationToken)
    {
        if (!_isConnected)
        {
            return;
        }

        await _armLock.WaitAsync(cancellationToken);
        try
        {
            await WriteCommandAsync(ClubCommand(), cancellationToken);
            // The club is the device's only shot-mode signal, and the reference
            // always follows it with DetectBall. A shot still waiting to re-arm
            // sends its own.
            if (_isArmed && !_shotPendingRearm)
            {
                await SendReadyCommandAsync(cancellationToken);
            }
        }
        finally
        {
            _armLock.Release();
        }
    }

    // A club with no swing stick code (the alignment stick) is sent as it is.
    private byte[] ClubCommand()
    {
        return _swingStick && SquareCommandBuilder.TryGetSwingStickCode(_clubCode, out _)
            ? SquareCommandBuilder.SwingStick(NextSequence(), _clubCode, _handedness)
            : SquareCommandBuilder.Club(NextSequence(), _clubCode, _handedness);
    }

    public void SetHandedness(int handedness)
    {
        _handedness = handedness == 1 ? 1 : 0;
        _logInfo($"SetHandedness requested. handedness={_handedness}");
    }

    public async Task SetSpinModeAsync(int spinMode, CancellationToken cancellationToken = default)
    {
        int normalized = spinMode == 0 ? 0 : 1;
        await _armLock.WaitAsync(cancellationToken);
        try
        {
            if (_spinMode == normalized)
                return;
            _spinMode = normalized;
            if (_isConnected && _isArmed && !_shotPendingRearm)
                await SendReadyCommandAsync(cancellationToken);
        }
        finally
        {
            _armLock.Release();
        }
    }

    public async Task SetReadyAsync(CancellationToken cancellationToken = default)
    {
        await _armLock.WaitAsync(cancellationToken);
        try
        {
            await SendReadyCommandAsync(cancellationToken);
        }
        finally
        {
            _armLock.Release();
        }
    }

    private async Task SendReadyCommandAsync(CancellationToken cancellationToken)
    {
        int spinMode = _spinMode;
        _logInfo($"Sending DetectBall ready command. spinMode={SpinModeName(spinMode)}");
        await WriteCommandAsync(SquareCommandBuilder.DetectBall(NextSequence(), mode: 1, spinMode), cancellationToken);
        _lastArmedSpinMode = spinMode;
        _shotPendingRearm = false;
        _isArmed = true;
        EmitReady(true);
        EmitStatus("Ready");
    }

    public async ValueTask DisposeAsync()
    {
        _bluetoothClient.DeviceDiscovered -= OnDeviceDiscovered;
        _bluetoothClient.CharacteristicValueChanged -= OnCharacteristicValueChanged;
        await DisconnectAsync();
        await _bluetoothClient.DisposeAsync();
        _connectionLock.Dispose();
        _writeLock.Dispose();
        _armLock.Dispose();
    }

    private async Task ReadDeviceInfoAsync(CancellationToken cancellationToken)
    {
        var battery = await _bluetoothClient.ReadCharacteristicAsync(_options.BatteryCharacteristicUuid, cancellationToken);
        if (battery.Length > 0)
        {
            EmitBattery(battery[0]);
        }

        var firmware = await _bluetoothClient.ReadCharacteristicAsync(_options.FirmwareCharacteristicUuid, cancellationToken);
        if (firmware.Length > 0)
        {
            EmitFirmware(ParseFirmware(firmware));
        }
    }

    private BluetoothConnectionOptions CreateBluetoothConnectionOptions()
    {
        return new BluetoothConnectionOptions(
            RequiredCharacteristicUuids:
            [
                _options.CommandCharacteristicUuid,
                _options.EventCharacteristicUuid
            ],
            OptionalCharacteristicUuids:
            [
                _options.BatteryCharacteristicUuid,
                _options.FirmwareCharacteristicUuid
            ],
            ServiceDiscoveryMaxAttempts: _options.ServiceDiscoveryMaxAttempts,
            ServiceDiscoveryRetryDelay: _options.ServiceDiscoveryRetryDelay);
    }

    private async Task SubscribeToNotificationsAsync(CancellationToken cancellationToken)
    {
        await _bluetoothClient.SubscribeToCharacteristicAsync(_options.EventCharacteristicUuid, cancellationToken);

        try
        {
            await _bluetoothClient.SubscribeToCharacteristicAsync(_options.BatteryCharacteristicUuid, cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task WriteCommandAsync(byte[] command, CancellationToken cancellationToken)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Square command channel is not available.");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _bluetoothClient.WriteCharacteristicAsync(
                _options.CommandCharacteristicUuid,
                command,
                BluetoothWriteMode.WithResponse,
                cancellationToken);
            _logInfo($"Wrote command ({command.Length} bytes).");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _lastPayload = null;
        Interlocked.Exchange(ref _awaitingClubForShot, 0);
        _isConnected = false;
        _isArmed = false;
        _shotPendingRearm = false;
        await _bluetoothClient.DisconnectAsync(cancellationToken);
    }

    private void StartHeartbeat()
    {
        if (_options.HeartbeatInterval <= TimeSpan.Zero || _options.HeartbeatInterval == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        _heartbeatTimer = new Timer(OnHeartbeatTimer, null, _options.HeartbeatInterval, _options.HeartbeatInterval);
    }

    private void OnHeartbeatTimer(object? state)
    {
        _ = RunAsync(async () =>
        {
            if (_isConnected)
            {
                await WriteCommandAsync(SquareCommandBuilder.Heartbeat(NextSequence()), CancellationToken.None);
            }
        });
    }

    private void OnDeviceDiscovered(BluetoothDevice device)
    {
        DeviceDiscovered?.Invoke(device);
    }

    private void OnCharacteristicValueChanged(BluetoothCharacteristicValue value)
    {
        if (value.CharacteristicUuid == _options.EventCharacteristicUuid)
        {
            _ = RunAsync(() => HandleNotificationAsync(value.Value));
            return;
        }

        if (value.CharacteristicUuid == _options.BatteryCharacteristicUuid && value.Value.Length > 0)
        {
            EmitBattery(value.Value[0]);
        }
    }

    private async Task HandleNotificationAsync(byte[] data)
    {
        if (SquareProtocol.TryParseSensor(data, out var sensor))
        {
            var ready = sensor.BallReady && sensor.BallDetected;
            EmitReady(ready);
            _logInfo($"Sensor packet parsed. ready={ready}");
            return;
        }

        if (SquareProtocol.TryParseClub(data, out var club))
        {
            HandleClubMetrics(data, club);
            return;
        }

        if (!SquareProtocol.IsShotPacket(data))
        {
            // Heartbeat acknowledgements (0x03), clock ticks (0x71) and the like.
            return;
        }

        var payload = Convert.ToHexString(data);
        if (payload == _lastPayload)
        {
            return;
        }

        _lastPayload = payload;
        if (!SquareProtocol.TryParseShot(data, out var metrics))
        {
            // The device stops detecting once it reports a strike, readable or
            // not: re-arm, or every later shot of the session is lost.
            _logInfo($"Ball packet rejected as implausible: {payload}");
            EmitReady(false);
            await _delayAsync(_options.ConnectionReadyDelay, CancellationToken.None);
            await SetReadyAsync();
            return;
        }

        await _armLock.WaitAsync();
        int armedSpinMode;
        try
        {
            _shotPendingRearm = true;
            armedSpinMode = _lastArmedSpinMode;
        }
        finally
        {
            _armLock.Release();
        }
        _logInfo($"Ball packet received: {payload}");
        _logInfo(FormatBallMetrics(metrics, armedSpinMode));
        EmitReady(false);
        ShotReceived?.Invoke(metrics);
        // The next club frame belongs to this shot, until the session re-arms.
        var shot = Interlocked.Increment(ref _shotCount);
        Volatile.Write(ref _awaitingClubForShot, shot);
        await RequestClubMetricsAsync(shot);
        await _delayAsync(_options.ConnectionReadyDelay, CancellationToken.None);
        // End only this shot's wait; a newer shot or a reconnect keeps its own.
        Interlocked.CompareExchange(ref _awaitingClubForShot, 0, shot);
        await SetReadyAsync();
    }

    // The club frame follows the ball frame once requested. BlueZ can fail the
    // write with org.bluez.Error.InProgress while the device still answers, so a
    // failed write is retried and never stops the wait; it must not block re-arming.
    private async Task RequestClubMetricsAsync(int shot)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteCommandAsync(SquareCommandBuilder.RequestClubMetrics(NextSequence()), CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                if (!IsAwaitingClubFor(shot))
                {
                    _logInfo($"Club metrics request failed ({ex.Message}); no longer waiting, not retrying.");
                    return;
                }

                if (attempt >= ClubRequestAttempts)
                {
                    _logError($"Club metrics request failed after {attempt} attempts: {ex.Message}; still accepting the club frame until re-arm.");
                    return;
                }

                _logInfo($"Club metrics request attempt {attempt} failed ({ex.Message}); retrying.");
            }

            await _delayAsync(ClubRequestRetryDelay, CancellationToken.None);
            if (!IsAwaitingClubFor(shot))
            {
                return;
            }
        }
    }

    private bool IsAwaitingClubFor(int shot) => Volatile.Read(ref _awaitingClubForShot) == shot;

    private void HandleClubMetrics(byte[] data, SquareClubMetrics club)
    {
        _logInfo($"Club packet received: {Convert.ToHexString(data)}");
        if (Interlocked.Exchange(ref _awaitingClubForShot, 0) == 0)
        {
            // Unsolicited or duplicate frame: it cannot be tied to a shot.
            return;
        }

        if (_clubCode == SquareCommandBuilder.PutterClubCode)
        {
            // Putter swings are not measured for club angles (matches squaregolf-connector).
            club = club with { PathDeg = null, FaceDeg = null, AttackDeg = null, DynamicLoftDeg = null };
        }

        if (!club.HasReading)
        {
            _logInfo("Club packet: shot not tracked (no club sticker, or the device declined the strike).");
        }

        ClubMetricsReceived?.Invoke(club);
        _logInfo($"Club packet parsed. path={club.PathDeg}, face={club.FaceDeg}, attack={club.AttackDeg}, loft={club.DynamicLoftDeg}, impact=({club.ImpactHorizontalMm}, {club.ImpactVerticalMm})");
    }

    private static string SpinModeName(int mode) => mode == 0 ? "Calculated" : "Measured";

    private static string FormatBallMetrics(SquareShotMetrics metrics, int spinMode)
    {
        var sources = metrics.Sources;
        return
            $"Ball packet parsed. mode={SpinModeName(spinMode)}, type={metrics.ShotType}, " +
            $"speed={Format(metrics.BallSpeedMps, "F2", "m/s", sources.BallSpeed)} " +
            $"({(metrics.BallSpeedMps * 2.23694f).ToString("F2", CultureInfo.InvariantCulture)} mph), " +
            $"launch={Format(metrics.VerticalAngle, "F2", "deg", sources.VerticalAngle)}, " +
            $"direction={Format(metrics.HorizontalAngle, "F2", "deg", sources.HorizontalAngle)}, " +
            $"totalSpin={Format(metrics.TotalSpinRpm, "F0", "rpm", sources.TotalSpin)}, " +
            $"spinAxis={Format(metrics.SpinAxis, "F2", "deg", sources.SpinAxis)}, " +
            $"backSpin={Format(metrics.BackSpinRpm, "F0", "rpm", sources.BackSpin)}, " +
            $"sideSpin={Format(metrics.SideSpinRpm, "F0", "rpm", sources.SideSpin)}";
    }

    private static string Format(float value, string format, string unit, SquareValueSource source) =>
        source == SquareValueSource.Missing
            ? "unavailable"
            : $"{value.ToString(format, CultureInfo.InvariantCulture)} {unit} [{source.ToString().ToLowerInvariant()}]";

    private byte NextSequence()
    {
        var sequence = _sequence;
        unchecked
        {
            _sequence++;
        }

        return sequence;
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            EmitError(ex.Message);
            _logError($"RunAsync failed: {ex}");
        }
    }

    private void EmitStatus(string status)
    {
        StatusChanged?.Invoke(status);
    }

    private void EmitError(string message)
    {
        ErrorOccurred?.Invoke(message);
    }

    private void EmitBattery(int level)
    {
        BatteryChanged?.Invoke(level);
    }

    private void EmitFirmware(string firmware)
    {
        FirmwareChanged?.Invoke(firmware);
    }

    private void EmitReady(bool ready)
    {
        ReadyChanged?.Invoke(ready);
    }

    private static string ParseFirmware(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("lm", out var launchMonitorVersion))
            {
                return launchMonitorVersion.GetString() ?? text;
            }
        }
        catch (JsonException)
        {
            return text;
        }

        return text;
    }

    private static bool IsTransientConnectFailure(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return true;
        }

        return ex is InvalidOperationException invalidOperationException
            && invalidOperationException.Message.Contains(
                "Could not open the selected Bluetooth device.",
                StringComparison.OrdinalIgnoreCase);
    }
}
