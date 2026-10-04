using System;
using System.Buffers.Binary;
using LaunchMonitors.Square;
using Xunit;

namespace OpenLaunchConnector.Tests;

// Club metrics path: the 0x11 0x87 request, the 0x11 0x07 frame decode
// (sentinel = no reading, impact gated by the byte-2 validity bitmask), the
// GSPro ClubData mapping, and the measured side-spin sign.
public class SquareClubProtocolTests
{
    private const short Sentinel = unchecked((short)0x8000);

    private static byte[] BuildClubFrame(byte bitmask, short path, short face, short attack, short loft,
        short impactH = 0, short impactV = 0, int length = 19)
    {
        var frame = new byte[length];
        frame[0] = 0x11;
        frame[1] = 0x07;
        frame[2] = bitmask;
        short[] fields = { path, face, attack, loft, impactH, impactV };
        for (var i = 0; i < fields.Length; i++)
        {
            var offset = 3 + i * 2;
            if (offset + 2 <= length)
            {
                BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(offset, 2), fields[i]);
            }
        }

        return frame;
    }

    [Fact]
    public void TryParseClub_DecodesAnglesInDegrees()
    {
        var frame = BuildClubFrame(0x0F, path: 160, face: -60, attack: -310, loft: 2250);

        Assert.True(SquareProtocol.TryParseClub(frame, out var club));
        Assert.Equal(1.6f, club.PathDeg!.Value, 3);
        Assert.Equal(-0.6f, club.FaceDeg!.Value, 3);
        Assert.Equal(-3.1f, club.AttackDeg!.Value, 3);
        Assert.Equal(22.5f, club.DynamicLoftDeg!.Value, 3);
    }

    [Fact]
    public void TryParseClub_SentinelFieldIsNull()
    {
        var frame = BuildClubFrame(0x0F, path: Sentinel, face: 100, attack: Sentinel, loft: 1500);

        Assert.True(SquareProtocol.TryParseClub(frame, out var club));
        Assert.Null(club.PathDeg);
        Assert.Equal(1.0f, club.FaceDeg!.Value, 3);
        Assert.Null(club.AttackDeg);
    }

    [Fact]
    public void TryParseClub_RejectsShortOrForeignFrames()
    {
        Assert.False(SquareProtocol.TryParseClub(new byte[] { 0x11, 0x07 }, out _));

        var shot = BuildClubFrame(0x0F, 1, 2, 3, 4);
        shot[1] = 0x02;
        Assert.False(SquareProtocol.TryParseClub(shot, out _));
    }

    [Theory]
    [InlineData("110700FFFFFFFFFFFFFFFF")]                    // Square Home, captured on hardware
    [InlineData("110700")]                                    // bare "no data" form
    [InlineData("110700FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")] // Omni: full length, all sentinels
    public void TryParseClub_UntrackedShotHasNoReading(string hex)
    {
        Assert.True(SquareProtocol.TryParseClub(Convert.FromHexString(hex), out var club));
        Assert.False(club.HasReading);
        Assert.Null(club.PathDeg);
        Assert.Null(club.DynamicLoftDeg);
    }

    [Fact]
    public void TryParseClub_NonZeroMaskGatesEachAngle()
    {
        // Bits 0 and 2: path and attack measured, face and loft not.
        var frame = BuildClubFrame(0x05, path: 160, face: 50, attack: -310, loft: 2250);

        Assert.True(SquareProtocol.TryParseClub(frame, out var club));
        Assert.Equal(1.6f, club.PathDeg!.Value, 3);
        Assert.Null(club.FaceDeg);
        Assert.Equal(-3.1f, club.AttackDeg!.Value, 3);
        Assert.Null(club.DynamicLoftDeg);
    }

    [Fact]
    public void TryParseClub_ZeroMaskFallsBackToTheSentinel()
    {
        // A device that never fills the mask must not lose real readings.
        var frame = BuildClubFrame(0x00, path: 160, face: -1, attack: -310, loft: 2250, length: 11);

        Assert.True(SquareProtocol.TryParseClub(frame, out var club));
        Assert.True(club.HasReading);
        Assert.Equal(1.6f, club.PathDeg!.Value, 3);
        Assert.Null(club.FaceDeg);
    }

    [Fact]
    public void TryParseClub_HomeLengthFrameHasAnglesButNoImpact()
    {
        var frame = BuildClubFrame(0xFF, 100, 200, 300, 400, length: 11);

        Assert.True(SquareProtocol.TryParseClub(frame, out var club));
        Assert.Equal(4.0f, club.DynamicLoftDeg!.Value, 3);
        Assert.Null(club.ImpactHorizontalMm);
        Assert.Null(club.ImpactVerticalMm);
    }

    [Fact]
    public void TryParseClub_ImpactOnlyWhenBitmaskSetsIt()
    {
        var flagged = BuildClubFrame(0x30, 0, 0, 0, 0, impactH: 850, impactV: -420);
        Assert.True(SquareProtocol.TryParseClub(flagged, out var hit));
        Assert.Equal(8.5f, hit.ImpactHorizontalMm!.Value, 3);
        Assert.Equal(-4.2f, hit.ImpactVerticalMm!.Value, 3);

        var unflagged = BuildClubFrame(0x0F, 0, 0, 0, 0, impactH: 850, impactV: -420);
        Assert.True(SquareProtocol.TryParseClub(unflagged, out var none));
        Assert.Null(none.ImpactHorizontalMm);
        Assert.Null(none.ImpactVerticalMm);
    }

    [Fact]
    public void TryParseClub_ImpactOutsideTheFaceIsNull()
    {
        var frame = BuildClubFrame(0x30, 0, 0, 0, 0, impactH: 9000, impactV: Sentinel);

        Assert.True(SquareProtocol.TryParseClub(frame, out var club));
        Assert.Null(club.ImpactHorizontalMm);
        Assert.Null(club.ImpactVerticalMm);
    }

    [Fact]
    public void RequestClubMetrics_MatchesReferenceCommand()
    {
        var command = SquareCommandBuilder.RequestClubMetrics(0x2A);

        Assert.Equal(new byte[] { 0x11, 0x87, 0x2A, 0, 0, 0, 0, 0, 0 }, command);
    }

    [Fact]
    public void ToClubData_UsesGsProKeysAndOmitsUnmeasured()
    {
        var club = new SquareClubMetrics(1.6f, -0.6f, null, 22.5f, null, null);

        var data = SquareShotDataMapper.ToClubData(club);

        Assert.Equal(1.6f, (float)data["Path"]);
        Assert.Equal(-0.6f, (float)data["FaceToTarget"]);
        Assert.Equal(22.5f, (float)data["Loft"]);
        Assert.False(data.ContainsKey("AngleOfAttack"));
        Assert.False(data.ContainsKey("HorizontalFaceImpact"));
    }

    [Fact]
    public void TryParseShot_MeasuredSideSpinSharesTheSpinAxisSign()
    {
        // Device convention: positive axis and positive side spin both curve left.
        var frame = new byte[17];
        frame[0] = 0x11;
        frame[1] = 0x02;
        frame[2] = 0x37;
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(3, 2), 6000);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(5, 2), 1550);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(7, 2), 0);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(9, 2), 5701);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(11, 2), 880);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(13, 2), 5634);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(15, 2), 872);

        Assert.True(SquareProtocol.TryParseShot(frame, out var shot));
        Assert.Equal(-8.8f, shot.SpinAxis, 3);
        Assert.Equal(-872, shot.SideSpinRpm);
    }
}
