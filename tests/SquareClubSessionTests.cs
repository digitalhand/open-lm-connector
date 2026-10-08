using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LaunchMonitors.Common.Bluetooth;
using LaunchMonitors.Square;
using Xunit;

namespace OpenLaunchConnector.Tests;

// Session-level club metrics: a 0x07 club frame belongs to the shot frame before
// it whether or not our 0x87 request write succeeded (BlueZ can report
// org.bluez.Error.InProgress while the device still answers), until the session
// re-arms for the next shot. Frames are the real ones from a Square Home.
public class SquareClubSessionTests
{
    private const string ShotHex = "1102370A0A9A09C8001A16E8FF7C15D0FF";
    private const string SecondShotHex = "1102370A0B9A09C8001A16E8FF7C15D0FF";
    private const string TrackedClubHex = "11070FC0FB1A00A1FEFB0C";
    // 3.85 m/s putt launched 0.26 deg down, as the device sends it with the putter selected.
    private const string PuttHex = "1102138101E6FF91010000000000000000";
    // A strike the device could not measure: ball speed carries the 0x8000 sentinel.
    private const string UnreadableShotHex = "11023700809A09C8001A16E8FF7C15D0FF";

    [Fact]
    public async Task ClubFrameIsKeptWhenTheRequestWriteFails()
    {
        await using var rig = await SessionRig.ConnectAsync();
        rig.Client.FailRequestWrites = int.MaxValue;

        rig.Notify(ShotHex);
        rig.Notify(TrackedClubHex);

        var club = Assert.Single(rig.Clubs);
        Assert.Equal(-10.88f, club.PathDeg!.Value, 2);
        Assert.Equal(33.23f, club.DynamicLoftDeg!.Value, 2);
    }

    [Fact]
    public async Task RequestIsRetriedAfterATransientWriteFailure()
    {
        await using var rig = await SessionRig.ConnectAsync();
        rig.Client.FailRequestWrites = 1;

        rig.Notify(ShotHex);

        Assert.Equal(2, rig.Client.RequestAttempts);
    }

    [Fact]
    public async Task ClubFrameArrivingDuringAFailedWriteStopsTheRetries()
    {
        await using var rig = await SessionRig.ConnectAsync();
        rig.Client.FailRequestWrites = int.MaxValue;
        rig.Client.DuringRequestWrite = () => rig.Notify(TrackedClubHex);

        rig.Notify(ShotHex);

        Assert.Equal(1, rig.Client.RequestAttempts);
        Assert.Single(rig.Clubs);
    }

    [Fact]
    public async Task DuplicateClubFramesAreReportedOnce()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify(ShotHex);
        rig.Notify(TrackedClubHex);
        rig.Notify(TrackedClubHex);

        Assert.Single(rig.Clubs);
    }

    [Fact]
    public async Task ClubFrameAfterReArmIsStale()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify(ShotHex);
        await rig.ReArmNextAsync();
        rig.Notify(TrackedClubHex);

        Assert.Empty(rig.Clubs);
    }

    [Fact]
    public async Task AnEarlierShotsReArmDoesNotEndTheNextShotsWait()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify(ShotHex);
        rig.Notify(SecondShotHex);
        await rig.ReArmNextAsync();
        rig.Notify(TrackedClubHex);

        Assert.Equal(2, rig.Shots);
        Assert.Single(rig.Clubs);
    }

    [Fact]
    public async Task APuttLaunchedSlightlyDownIsReported()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify(PuttHex);

        Assert.Equal(1, rig.Shots);
    }

    // The device stops detecting once it reports a strike, so a frame the
    // parser rejects must still re-arm it or every later shot is lost.
    [Fact]
    public async Task AnUnreadableShotFrameStillReArmsTheDevice()
    {
        await using var rig = await SessionRig.ConnectAsync();
        var armedAtConnect = rig.Client.DetectBallCommands.Count;

        rig.Notify(UnreadableShotHex);
        rig.Notify(UnreadableShotHex);
        await rig.ReArmNextAsync();

        Assert.Equal(0, rig.Shots);
        Assert.Equal(armedAtConnect + 1, rig.Client.DetectBallCommands.Count);
        Assert.Single(rig.Logs, entry => entry == $"Ball packet rejected as implausible: {UnreadableShotHex}");
    }

    // The club is the device's only shot-mode signal (putter = putting), and the
    // reference connector always follows a club change with DetectBall.
    [Fact]
    public async Task AClubChangeWhileArmedReArmsTheDevice()
    {
        await using var rig = await SessionRig.ConnectAsync();
        var armed = rig.Client.DetectBallCommands.Count;

        await rig.Session.SetClubAsync("0107");

        Assert.Equal(armed + 1, rig.Client.DetectBallCommands.Count);
        Assert.Equal(new byte[] { 0x82, 0x81 }, rig.Client.CommandIds.ToArray()[^2..]);
    }

    [Fact]
    public async Task AClubChangeDuringAShotLeavesTheReArmToTheShot()
    {
        await using var rig = await SessionRig.ConnectAsync();
        rig.Notify(ShotHex);
        var duringShot = rig.Client.DetectBallCommands.Count;

        await rig.Session.SetClubAsync("0107");

        Assert.Equal(duringShot, rig.Client.DetectBallCommands.Count);
        Assert.Equal(0x82, rig.Client.CommandIds[^1]);

        await rig.ReArmNextAsync();
        Assert.Equal(duringShot + 1, rig.Client.DetectBallCommands.Count);
    }

    [Fact]
    public async Task ClubFrameWithoutAShotIsIgnored()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify(TrackedClubHex);

        Assert.Empty(rig.Clubs);
    }

    [Fact]
    public async Task SpinModeChangesAreSentWhenReadyAndDeferredDuringAShot()
    {
        await using var rig = await SessionRig.ConnectAsync();
        Assert.Equal(0x11, rig.Client.DetectBallCommands[^1][4]);

        await rig.Session.SetSpinModeAsync(0);
        Assert.Equal(0x10, rig.Client.DetectBallCommands[^1][4]);

        rig.Notify(ShotHex);
        var countDuringShot = rig.Client.DetectBallCommands.Count;
        await rig.Session.SetSpinModeAsync(1);
        Assert.Equal(countDuringShot, rig.Client.DetectBallCommands.Count);
        Assert.Contains(rig.Logs, entry => entry.Contains("Ball packet parsed. mode=Calculated"));

        await rig.ReArmNextAsync();
        Assert.Equal(0x11, rig.Client.DetectBallCommands[^1][4]);
    }

    [Fact]
    public async Task BallLogsContainRawAndDecodedDataOnlyOnce()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify(ShotHex);
        rig.Notify(ShotHex);

        Assert.Single(rig.Logs, entry => entry == $"Ball packet received: {ShotHex}");
        var parsed = Assert.Single(rig.Logs, entry => entry.StartsWith("Ball packet parsed."));
        Assert.Contains("mode=Measured", parsed);
        Assert.Contains("speed=25.70 m/s", parsed);
        Assert.Contains("launch=24.58 deg", parsed);
        Assert.Contains("direction=2.00 deg", parsed);
        Assert.Contains("totalSpin=5658 rpm", parsed);
        Assert.Contains("spinAxis=0.24 deg", parsed);
        Assert.Contains("backSpin=5500 rpm", parsed);
        Assert.Contains("sideSpin=48 rpm", parsed);
    }

    [Fact]
    public async Task BallLogsDistinguishMissingAndLocallyDerivedReadings()
    {
        await using var rig = await SessionRig.ConnectAsync();

        rig.Notify("1102370A0A0080C8001A16E8FF00800080");

        var parsed = Assert.Single(rig.Logs, entry => entry.StartsWith("Ball packet parsed."));
        Assert.Contains("launch=unavailable", parsed);
        Assert.Contains("backSpin=5658 rpm [derived]", parsed);
        Assert.Contains("sideSpin=24 rpm [derived]", parsed);
    }

    [Fact]
    public async Task SwingStickIsUsedOnConnectAndReconnect()
    {
        await using var rig = await SessionRig.ConnectAsync(swingStick: true);
        Assert.Equal("0202000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));

        await rig.ReconnectAsync();
        Assert.Equal("0202000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));
    }

    [Fact]
    public async Task SwingStickChangesResendTheClubAndReArm()
    {
        await using var rig = await SessionRig.ConnectAsync();
        Assert.Equal("020400000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));
        var clubs = rig.Client.ClubCommands.Count;
        var detects = rig.Client.DetectBallCommands.Count;

        await rig.Session.SetSwingStickAsync(true);
        Assert.Equal(clubs + 1, rig.Client.ClubCommands.Count);
        Assert.Equal("0202000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));
        Assert.Equal(detects + 1, rig.Client.DetectBallCommands.Count);

        await rig.Session.SetSwingStickAsync(true);
        Assert.Equal(clubs + 1, rig.Client.ClubCommands.Count);

        await rig.Session.SetClubAsync("0107");
        Assert.Equal("0103000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));

        await rig.Session.SetSwingStickAsync(false);
        Assert.Equal("010700000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));
    }

    [Fact]
    public async Task SwingStickFallsBackToTheClubCodeWhenTheClubHasNone()
    {
        await using var rig = await SessionRig.ConnectAsync(swingStick: true);

        await rig.Session.SetClubAsync("0008");

        Assert.Equal("000800000000", Convert.ToHexString(rig.Client.ClubCommands[^1][3..]));
    }

    [Fact]
    public async Task SpinModeIsUsedOnConnectAndReconnect()
    {
        await using var rig = await SessionRig.ConnectAsync(0);
        Assert.Equal(0x10, rig.Client.DetectBallCommands[^1][4]);

        await rig.ReconnectAsync();
        Assert.Equal(0x10, rig.Client.DetectBallCommands[^1][4]);
    }

    // A connected session whose post-shot re-arm delays each wait on their own
    // gate, so a test can deliver frames while the session awaits club data.
    private sealed class SessionRig : IAsyncDisposable
    {
        private static readonly SquareConnectionOptions Options = SquareConnectionOptions.Default with
        {
            ConnectionClubDelay = TimeSpan.Zero,
            HeartbeatInterval = Timeout.InfiniteTimeSpan,
        };

        private readonly Queue<TaskCompletionSource> _pendingReArms = new();
        private TaskCompletionSource? _reArmed;
        private bool _connected;

        private SessionRig()
        {
            Session = new SquareConnectionSession(Client, Options, DelayAsync, Logs.Add);
            Session.ShotReceived += _ => Shots++;
            Session.ClubMetricsReceived += Clubs.Add;
            Session.ReadyChanged += ready =>
            {
                if (ready)
                {
                    _reArmed?.TrySetResult();
                }
            };
        }

        public FakeGattClient Client { get; } = new();

        public SquareConnectionSession Session { get; }

        public int Shots { get; private set; }

        public List<SquareClubMetrics> Clubs { get; } = new();

        public List<string> Logs { get; } = new();

        public static async Task<SessionRig> ConnectAsync(int? spinMode = null, bool swingStick = false)
        {
            var rig = new SessionRig();
            if (spinMode.HasValue)
                await rig.Session.SetSpinModeAsync(spinMode.Value);
            if (swingStick)
                await rig.Session.SetSwingStickAsync(true);
            await rig.Session.ConnectToDeviceAsync("square-home");
            Assert.True(rig.Client.Connected, "the session did not connect");
            rig._connected = true;
            return rig;
        }

        public async Task ReconnectAsync()
        {
            _connected = false;
            await Session.DisconnectAsync();
            await Session.ConnectToDeviceAsync("square-home");
            _connected = true;
        }

        public void Notify(string hex) =>
            Client.Notify(Options.EventCharacteristicUuid, Convert.FromHexString(hex));

        // Lets the oldest pending post-shot delay elapse, then waits for its
        // DetectBall re-arm.
        public async Task ReArmNextAsync()
        {
            _reArmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReArms.Dequeue().TrySetResult();
            await _reArmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        // Only post-shot waits block; connect and retry delays return at once.
        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (!_connected || delay != Options.ConnectionReadyDelay)
            {
                return Task.CompletedTask;
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReArms.Enqueue(gate);
            return gate.Task;
        }
    }

    private sealed class FakeGattClient : IBluetoothGattClient
    {
        public event Action<BluetoothDevice>? DeviceDiscovered { add { } remove { } }

        public event Action<BluetoothCharacteristicValue>? CharacteristicValueChanged;

        public bool Connected { get; private set; }

        // How many club-request writes fail with BlueZ's InProgress error.
        public int FailRequestWrites { get; set; }

        public int RequestAttempts { get; private set; }

        public List<byte[]> DetectBallCommands { get; } = new();

        public List<byte[]> ClubCommands { get; } = new();

        // The command id (byte 1) of every write, in order.
        public List<byte> CommandIds { get; } = new();

        // Runs inside a club-request write, e.g. the device answering mid-write.
        public Action? DuringRequestWrite { get; set; }

        public void Notify(Guid characteristicUuid, byte[] value) =>
            CharacteristicValueChanged?.Invoke(new BluetoothCharacteristicValue(characteristicUuid, value));

        public Task WriteCharacteristicAsync(
            Guid characteristicUuid, byte[] value, BluetoothWriteMode writeMode, CancellationToken cancellationToken)
        {
            if (value.Length > 1 && value[0] == 0x11)
            {
                CommandIds.Add(value[1]);
            }
            if (value.Length > 4 && value[0] == 0x11 && value[1] == 0x81)
            {
                DetectBallCommands.Add((byte[])value.Clone());
            }
            if (value.Length > 4 && value[0] == 0x11 && value[1] == 0x82)
            {
                ClubCommands.Add((byte[])value.Clone());
            }
            if (value.Length < 2 || value[0] != 0x11 || value[1] != 0x87)
            {
                return Task.CompletedTask;
            }

            RequestAttempts++;
            DuringRequestWrite?.Invoke();
            return RequestAttempts > FailRequestWrites
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("org.bluez.Error.InProgress: In Progress"));
        }

        public Task ConnectAsync(string deviceId, BluetoothConnectionOptions options, CancellationToken cancellationToken)
        {
            Connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Connected = false;
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadCharacteristicAsync(Guid characteristicUuid, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<byte>());

        public Task SubscribeToCharacteristicAsync(Guid characteristicUuid, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StartScanAsync(BluetoothScanOptions options, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopScanAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
