using System;
using System.Collections.Generic;
using System.Globalization;

namespace LaunchMonitors.Square;

public static class SquareCommandBuilder
{
    public const string DriverClubCode = "0204";
    public const string PutterClubCode = "0107";

    // Regular club code -> the code the device expects for Square's swing stick
    // (the squaregolf-connector reference's SwingStickCode). The reference lists
    // 0900 for the 8 Iron, the 9 Iron's code; 0800 follows every other iron.
    private static readonly Dictionary<string, string> SwingStickCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0204"] = "0202",
        ["0107"] = "0103",
        ["0305"] = "0301",
        ["0505"] = "0501",
        ["0705"] = "0701",
        ["0406"] = "0400",
        ["0506"] = "0500",
        ["0606"] = "0600",
        ["0706"] = "0700",
        ["0806"] = "0800",
        ["0906"] = "0900",
        ["0a06"] = "0a00",
        ["0b06"] = "0b00",
        ["0c06"] = "0c00",
    };

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

    /// <summary>False for a code with no swing stick form (the alignment stick, an unknown code).</summary>
    public static bool TryGetSwingStickCode(string clubCode, out string swingStickCode)
    {
        swingStickCode = string.Empty;
        return !string.IsNullOrWhiteSpace(clubCode) && SwingStickCodes.TryGetValue(clubCode, out swingStickCode!);
    }

    /// <summary>
    /// Selects <paramref name="clubCode"/> (a regular club code) as swung with Square's swing stick.
    /// One byte shorter than <see cref="Club"/>, as in the reference's <c>SwingStickCommand</c>.
    /// </summary>
    public static byte[] SwingStick(byte sequence, string clubCode, int handedness)
    {
        if (!TryGetSwingStickCode(clubCode, out var swingStickCode))
        {
            throw new ArgumentException($"Club code '{clubCode}' has no swing stick code.", nameof(clubCode));
        }

        return FromHex($"1182{sequence:X2}{swingStickCode}0{handedness}0000");
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
