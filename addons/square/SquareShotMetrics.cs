namespace LaunchMonitors.Square;

public readonly record struct SquareShotMetrics(
    float BallSpeedMps,
    float VerticalAngle,
    float HorizontalAngle,
    int TotalSpinRpm,
    float SpinAxis,
    int BackSpinRpm,
    int SideSpinRpm,
    string ShotType)
{
    public SquareShotSources Sources { get; init; }
}

public enum SquareValueSource
{
    Missing,
    Packet,
    Derived
}

public readonly record struct SquareShotSources(
    SquareValueSource BallSpeed,
    SquareValueSource VerticalAngle,
    SquareValueSource HorizontalAngle,
    SquareValueSource TotalSpin,
    SquareValueSource SpinAxis,
    SquareValueSource BackSpin,
    SquareValueSource SideSpin);

/// <summary>
/// Club metrics from a <c>0x11 0x07</c> frame. A null field was not measured
/// this shot (sentinel, putter, or not reported by this device). Impact is mm
/// from face centre: horizontal negative toward the toe, vertical negative low
/// (right-handed; allsquare, checked against the vendor app).
/// </summary>
public readonly record struct SquareClubMetrics(
    float? PathDeg,
    float? FaceDeg,
    float? AttackDeg,
    float? DynamicLoftDeg,
    float? ImpactHorizontalMm,
    float? ImpactVerticalMm)
{
    /// <summary>False for an untracked shot (no club sticker, or a strike the device declined).</summary>
    public bool HasReading =>
        PathDeg.HasValue || FaceDeg.HasValue || AttackDeg.HasValue || DynamicLoftDeg.HasValue
        || ImpactHorizontalMm.HasValue || ImpactVerticalMm.HasValue;
}

public readonly record struct SquareSensorData(
    bool BallReady,
    bool BallDetected,
    int PositionX,
    int PositionY,
    int PositionZ);
