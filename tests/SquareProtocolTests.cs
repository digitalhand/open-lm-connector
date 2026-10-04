using System;
using System.Buffers.Binary;
using LaunchMonitors.Square;
using Xunit;

namespace OpenLaunchConnector.Tests;

public class SquareProtocolTests
{
    private const short InvalidReadingSentinel = unchecked((short)0x8000);

    // Builds a 17-byte 0x11 0x02 shot frame from already-encoded little-endian
    // Int16 fields, matching the offset map in PROTOCOL.md A.4.
    private static byte[] BuildShotFrame(
        byte shotType,
        short ballSpeedRaw,
        short verticalAngleRaw,
        short horizontalAngleRaw,
        short totalSpinRaw,
        short spinAxisRaw,
        short backSpinRaw,
        short sideSpinRaw)
    {
        var frame = new byte[17];
        frame[0] = 0x11;
        frame[1] = 0x02;
        frame[2] = shotType;
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(3), ballSpeedRaw);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(5), verticalAngleRaw);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(7), horizontalAngleRaw);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(9), totalSpinRaw);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(11), spinAxisRaw);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(13), backSpinRaw);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(15), sideSpinRaw);
        return frame;
    }

    private static byte[] BuildSensorFrame(byte readyByte, byte detectedByte, int x, int y, int z)
    {
        var frame = new byte[17];
        frame[0] = 0x11;
        frame[1] = 0x01;
        frame[3] = readyByte;
        frame[4] = detectedByte;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), x);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(9), y);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(13), z);
        return frame;
    }

    [Fact]
    public void TryParseShot_FullSwing_DecodesEveryField()
    {
        // 50.00 m/s, 15.00 VLA, 2.00 HLA, 3000 total spin, 5.00 spin axis,
        // 2900 back spin, 261 side spin. The device encodes spin axis and side
        // spin positive-left; both decode flipped (GSPro: positive = right).
        var frame = BuildShotFrame(0x37, 5000, 1500, 200, 3000, -500, 2900, -261);

        Assert.True(SquareProtocol.TryParseShot(frame, out var metrics));
        Assert.Equal(50.0f, metrics.BallSpeedMps, 3);
        Assert.Equal(15.0f, metrics.VerticalAngle, 3);
        Assert.Equal(2.0f, metrics.HorizontalAngle, 3);
        Assert.Equal(3000, metrics.TotalSpinRpm);
        Assert.Equal(5.0f, metrics.SpinAxis, 3);
        Assert.Equal(2900, metrics.BackSpinRpm);
        Assert.Equal(261, metrics.SideSpinRpm);
        Assert.Equal("full", metrics.ShotType);
        Assert.Equal(SquareValueSource.Packet, metrics.Sources.BackSpin);
        Assert.Equal(SquareValueSource.Packet, metrics.Sources.SideSpin);
    }

    [Theory]
    [InlineData((byte)0x37, "full")]
    [InlineData((byte)0x13, "putt")]
    [InlineData((byte)0x99, "unknown")]
    public void TryParseShot_MapsShotTypeByte(byte typeByte, string expected)
    {
        var frame = BuildShotFrame(typeByte, 5000, 1500, 200, 3000, -500, 2900, 261);

        Assert.True(SquareProtocol.TryParseShot(frame, out var metrics));
        Assert.Equal(expected, metrics.ShotType);
    }

    [Fact]
    public void TryParseShot_DerivesMissingSpinComponentsFromTotalAndAxis()
    {
        // Back/side spin unmeasured (sentinel) but total spin + axis known:
        // back = round(3000*cos(5°)) = 2989, side = round(3000*sin(5°)) = 261.
        var frame = BuildShotFrame(
            0x37, 5000, 1500, 200, 3000, -500,
            InvalidReadingSentinel, InvalidReadingSentinel);

        Assert.True(SquareProtocol.TryParseShot(frame, out var metrics));
        Assert.Equal(2989, metrics.BackSpinRpm);
        Assert.Equal(261, metrics.SideSpinRpm);
        Assert.Equal(SquareValueSource.Derived, metrics.Sources.BackSpin);
        Assert.Equal(SquareValueSource.Derived, metrics.Sources.SideSpin);
    }

    [Fact]
    public void TryParseShot_VerticalAngleSentinel_BecomesZeroAndStillParses()
    {
        // Documented asymmetry: an unmeasured VLA collapses to 0 and passes the
        // plausibility gate rather than dropping the shot.
        var frame = BuildShotFrame(0x37, 5000, InvalidReadingSentinel, 200, 3000, -500, 2900, 261);

        Assert.True(SquareProtocol.TryParseShot(frame, out var metrics));
        Assert.Equal(0.0f, metrics.VerticalAngle, 3);
        Assert.Equal(SquareValueSource.Missing, metrics.Sources.VerticalAngle);
    }

    [Theory]
    [InlineData((short)0)]      // zero ball speed
    [InlineData((short)25000)]  // 250 m/s, above the upper bound
    public void TryParseShot_ImplausibleBallSpeed_ReturnsFalse(short ballSpeedRaw)
    {
        var frame = BuildShotFrame(0x37, ballSpeedRaw, 1500, 200, 3000, -500, 2900, 261);

        Assert.False(SquareProtocol.TryParseShot(frame, out _));
    }

    [Fact]
    public void TryParseShot_PuttLaunchedSlightlyDown_Parses()
    {
        // A real Square Home putt: 3.85 m/s, launched 0.26 deg down, 4.01 deg right, no spin.
        var frame = Convert.FromHexString("1102138101E6FF91010000000000000000");

        Assert.True(SquareProtocol.TryParseShot(frame, out var metrics));
        Assert.Equal("putt", metrics.ShotType);
        Assert.Equal(3.85f, metrics.BallSpeedMps, 3);
        Assert.Equal(-0.26f, metrics.VerticalAngle, 3);
        Assert.Equal(4.01f, metrics.HorizontalAngle, 3);
        Assert.Equal(0, metrics.TotalSpinRpm);
    }

    [Theory]
    [InlineData((short)-9000)]  // straight down
    [InlineData((short)9000)]   // straight up
    public void TryParseShot_VerticalAngleOutsideAQuarterTurn_ReturnsFalse(short verticalAngleRaw)
    {
        var frame = BuildShotFrame(0x37, 5000, verticalAngleRaw, 200, 3000, -500, 2900, 261);

        Assert.False(SquareProtocol.TryParseShot(frame, out _));
    }

    [Fact]
    public void TryParseShot_RejectsNonShotFrames()
    {
        Assert.False(SquareProtocol.TryParseShot(new byte[17], out _));      // wrong markers
        Assert.False(SquareProtocol.TryParseShot(new byte[10], out _));      // too short
        Assert.False(SquareProtocol.TryParseShot(BuildSensorFrame(0x01, 0x01, 0, 0, 0), out _));
    }

    [Fact]
    public void TryParseSensor_BallPlacedAndDetected()
    {
        var frame = BuildSensorFrame(0x01, 0x01, 10, -20, 30);

        Assert.True(SquareProtocol.TryParseSensor(frame, out var sensor));
        Assert.True(sensor.BallReady);
        Assert.True(sensor.BallDetected);
        Assert.Equal(10, sensor.PositionX);
        Assert.Equal(-20, sensor.PositionY);
        Assert.Equal(30, sensor.PositionZ);
    }

    [Theory]
    [InlineData((byte)0x01, true)]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x00, false)]
    public void TryParseSensor_BallReadyFromByteThree(byte readyByte, bool expectedReady)
    {
        var frame = BuildSensorFrame(readyByte, 0x00, 0, 0, 0);

        Assert.True(SquareProtocol.TryParseSensor(frame, out var sensor));
        Assert.Equal(expectedReady, sensor.BallReady);
    }

    [Fact]
    public void TryParseSensor_RejectsShotFrame()
    {
        var shot = BuildShotFrame(0x37, 5000, 1500, 200, 3000, -500, 2900, 261);
        Assert.False(SquareProtocol.TryParseSensor(shot, out _));
    }
}
