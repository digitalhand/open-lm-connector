using System;
using System.Collections.Generic;

namespace LaunchMonitors.Square;

public static class SquareShotDataMapper
{
    private const float MetersPerSecondToMph = 2.23694f;

    public static IReadOnlyDictionary<string, object> ToBallData(SquareShotMetrics metrics)
    {
        // Spin components are already resolved by SquareProtocol (including
        // deriving missing back/side spin from total spin + axis), so only the
        // unsigned-total clamp remains here.
        var totalSpin = Math.Max(0, metrics.TotalSpinRpm);
        var backSpin = metrics.BackSpinRpm;
        var sideSpin = metrics.SideSpinRpm;

        return new Dictionary<string, object>
        {
            { "Speed", metrics.BallSpeedMps * MetersPerSecondToMph },
            { "VLA", metrics.VerticalAngle },
            { "HLA", metrics.HorizontalAngle },
            { "TotalSpin", totalSpin },
            { "SpinAxis", metrics.SpinAxis },
            { "BackSpin", backSpin },
            { "SideSpin", sideSpin },
            { "ShotType", metrics.ShotType }
        };
    }

    /// <summary>
    /// Maps club metrics onto GSPro <c>ClubData</c> key names (degrees, mm).
    /// Unmeasured fields are omitted rather than zeroed, so consumers can tell
    /// "square" from "no reading".
    /// </summary>
    public static IReadOnlyDictionary<string, object> ToClubData(SquareClubMetrics metrics)
    {
        var data = new Dictionary<string, object>();
        AddIfMeasured(data, "Path", metrics.PathDeg);
        AddIfMeasured(data, "FaceToTarget", metrics.FaceDeg);
        AddIfMeasured(data, "AngleOfAttack", metrics.AttackDeg);
        AddIfMeasured(data, "Loft", metrics.DynamicLoftDeg);
        AddIfMeasured(data, "HorizontalFaceImpact", metrics.ImpactHorizontalMm);
        AddIfMeasured(data, "VerticalFaceImpact", metrics.ImpactVerticalMm);
        return data;
    }

    private static void AddIfMeasured(Dictionary<string, object> data, string key, float? value)
    {
        if (value.HasValue)
        {
            data[key] = value.Value;
        }
    }
}
