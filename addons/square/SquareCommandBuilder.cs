using System;
using System.Globalization;

namespace LaunchMonitors.Square;

public static class SquareCommandBuilder
{
    public const string DriverClubCode = "0204";
    public const string PutterClubCode = "0107";

    public static byte[] Heartbeat(byte sequence)
    {
        return FromHex($"1183{sequence:X2}0000000000");
    }

    public static byte[] DetectBall(byte sequence, int mode, int spinMode)
    {
        return FromHex($"1181{sequence:X2}0{mode}1{spinMode}00000000");
    }

    public static byte[] Club(byte sequence, string clubCode, int handedness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clubCode);
        return FromHex($"1182{sequence:X2}{clubCode}0{handedness}000000");
    }

    /// <summary>Asks the device for the <c>0x11 0x07</c> club frame of the last shot.</summary>
    public static byte[] RequestClubMetrics(byte sequence)
    {
        return FromHex($"1187{sequence:X2}000000000000");
    }

    public static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex values must have an even number of characters.", nameof(hex));
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }
}
