using System;
using System.Buffers.Binary;

namespace LaunchMonitors.Square;

public static class SquareProtocol
{
    public static bool IsSensorPacket(ReadOnlySpan<byte> data)
    {
        return data.Length >= 17 && data[0] == 0x11 && data[1] == 0x01;
    }

    public static bool IsShotPacket(ReadOnlySpan<byte> data)
    {
        return data.Length >= 17 && data[0] == 0x11 && data[1] == 0x02;
    }

    public static bool IsClubPacket(ReadOnlySpan<byte> data)
    {
        return data.Length >= ClubHeaderLength && data[0] == 0x11 && data[1] == 0x07;
    }

    public static bool TryParseSensor(ReadOnlySpan<byte> data, out SquareSensorData sensor)
    {
        sensor = default;
        if (!IsSensorPacket(data))
        {
            return false;
        }

        sensor = new SquareSensorData(
            data[3] is 0x01 or 0x02,
            data[4] == 0x01,
            BinaryPrimitives.ReadInt32LittleEndian(data[5..9]),
            BinaryPrimitives.ReadInt32LittleEndian(data[9..13]),
            BinaryPrimitives.ReadInt32LittleEndian(data[13..17]));

        return true;
    }

    // The device sends 0x8000 (-32768) for a field it could not measure this
    // shot. Treated as "no reading" rather than a real value, otherwise the
    // sentinel leaks through as a huge negative spin / angle and can drop the
    // whole shot at the plausibility gate.
    private const short InvalidReadingSentinel = unchecked((short)0x8000);

    public static bool TryParseShot(ReadOnlySpan<byte> data, out SquareShotMetrics metrics)
    {
        metrics = default;
        if (!IsShotPacket(data))
        {
            return false;
        }

        // Byte[2] is opaque metadata on the Home device; 0x37 is observed on
        // full-swing frames and 0x13 on putts. ShotType is informational only.
        var shotType = data[2] switch
        {
            0x37 => "full",
            0x13 => "putt",
            _ => "unknown"
        };

        var (ballSpeed, ballSpeedValid) = ReadScaledInt16(data, 3, 100.0f);
        var (verticalAngle, verticalAngleValid) = ReadScaledInt16(data, 5, 100.0f);
        var (horizontalAngle, horizontalAngleValid) = ReadScaledInt16(data, 7, 100.0f);
        var (totalSpin, totalSpinValid) = ReadInt16(data, 9);
        var (spinAxis, spinAxisValid) = ReadScaledInt16(data, 11, -100.0f);
        var (backSpin, backSpinValid) = ReadInt16(data, 13);
        var (sideSpin, sideSpinValid) = ReadInt16(data, 15);
        var backSpinSource = backSpinValid ? SquareValueSource.Packet : SquareValueSource.Missing;
        var sideSpinSource = sideSpinValid ? SquareValueSource.Packet : SquareValueSource.Missing;

        // The device's side spin shares its spin-axis convention (positive = left);
        // flip it too so both match the GSPro convention the axis is decoded into.
        if (sideSpinValid)
        {
            sideSpin = (short)-sideSpin;
        }

        // When total spin and spin axis are known but a spin component was not
        // measured, derive it from the axis (matches squaregolf-connector).
        if (totalSpinValid && spinAxisValid)
        {
            var spinAxisRadians = MathF.PI * spinAxis / 180.0f;
            if (!backSpinValid)
            {
                backSpin = (short)MathF.Round(totalSpin * MathF.Cos(spinAxisRadians));
                backSpinSource = SquareValueSource.Derived;
            }

            if (!sideSpinValid)
            {
                sideSpin = (short)MathF.Round(totalSpin * MathF.Sin(spinAxisRadians));
                sideSpinSource = SquareValueSource.Derived;
            }
        }

        metrics = new SquareShotMetrics(
            ballSpeed,
            verticalAngle,
            horizontalAngle,
            totalSpin,
            spinAxis,
            backSpin,
            sideSpin,
            shotType)
        {
            Sources = new SquareShotSources(
                Source(ballSpeedValid),
                Source(verticalAngleValid),
                Source(horizontalAngleValid),
                Source(totalSpinValid),
                Source(spinAxisValid),
                backSpinSource,
                sideSpinSource)
        };

        return IsPlausible(metrics);
    }

    // Club frame: `11 07 {mask} {path} {face} {attack} {loft} {impactH} {impactV}
    // {clubSpeed} {smash}`, i16 LE ÷ 100 (see PROTOCOL.md; allsquare WIRE.md §6.6).
    // Byte 2 is a per-field validity mask (bit n = field n). A field the device
    // did not measure carries the 0xFFFF sentinel — an untracked shot (no club
    // sticker, or a strike the device declined) is mask 0 with every field
    // 0xFFFF. The Home sends fewer fields than the Omni (11 bytes observed, a
    // bare 3-byte frame reported), so absent fields are simply null.
    private const int ClubHeaderLength = 3;
    private const int ClubFieldsOffset = 3;
    private const short ClubSentinel = -1;
    private const int ImpactHorizontalField = 4;
    private const int ImpactVerticalField = 5;
    private const float MaxImpactHorizontalMm = 40.0f;
    private const float MaxImpactVerticalMm = 30.0f;

    public static bool TryParseClub(ReadOnlySpan<byte> data, out SquareClubMetrics metrics)
    {
        metrics = default;
        if (!IsClubPacket(data))
        {
            return false;
        }

        metrics = new SquareClubMetrics(
            ReadClubAngle(data, 0),
            ReadClubAngle(data, 1),
            ReadClubAngle(data, 2),
            ReadClubAngle(data, 3),
            ReadClubImpact(data, ImpactHorizontalField, MaxImpactHorizontalMm),
            ReadClubImpact(data, ImpactVerticalField, MaxImpactVerticalMm));
        return true;
    }

    // Angles: a non-zero mask must flag the field. A zero mask falls back to the
    // sentinel alone, so a device that never fills the mask cannot lose readings
    // (an untracked frame is all-sentinel either way).
    private static float? ReadClubAngle(ReadOnlySpan<byte> data, int field)
    {
        var mask = data[2];
        if (mask != 0 && !IsFieldFlagged(mask, field))
        {
            return null;
        }

        return ReadClubField(data, field);
    }

    // Impact must be flagged: a device that zero-pads these bytes would
    // otherwise report a false centre strike.
    private static float? ReadClubImpact(ReadOnlySpan<byte> data, int field, float maxAbsMm)
    {
        if (!IsFieldFlagged(data[2], field))
        {
            return null;
        }

        var value = ReadClubField(data, field);
        return value.HasValue && MathF.Abs(value.Value) <= maxAbsMm ? value : null;
    }

    private static bool IsFieldFlagged(byte mask, int field) => (mask & (1 << field)) != 0;

    private static SquareValueSource Source(bool valid) => valid ? SquareValueSource.Packet : SquareValueSource.Missing;

    private static float? ReadClubField(ReadOnlySpan<byte> data, int field)
    {
        var offset = ClubFieldsOffset + field * 2;
        if (data.Length < offset + 2)
        {
            return null;
        }

        var raw = BinaryPrimitives.ReadInt16LittleEndian(data[offset..(offset + 2)]);
        return raw is ClubSentinel or InvalidReadingSentinel ? null : raw / 100.0f;
    }

    private static (float Value, bool Valid) ReadScaledInt16(ReadOnlySpan<byte> data, int offset, float scale)
    {
        var (raw, valid) = ReadInt16(data, offset);
        return (raw / scale, valid);
    }

    private static (short Value, bool Valid) ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        var raw = BinaryPrimitives.ReadInt16LittleEndian(data[offset..(offset + 2)]);
        return raw == InvalidReadingSentinel ? ((short)0, false) : (raw, true);
    }

    // A putt or a topped shot leaves the face slightly downward, so a negative
    // launch angle is a real reading; only a quarter turn or more is garbage.
    private const float MaxAbsVerticalAngleDeg = 90.0f;

    private static bool IsPlausible(SquareShotMetrics metrics)
    {
        return metrics.BallSpeedMps > 0
            && metrics.BallSpeedMps < 250
            && metrics.TotalSpinRpm >= 0
            && metrics.TotalSpinRpm < 30_000
            && MathF.Abs(metrics.VerticalAngle) < MaxAbsVerticalAngleDeg;
    }
}
