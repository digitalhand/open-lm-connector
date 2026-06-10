using LaunchMonitors.Square;
using Xunit;

namespace OpenLaunchConnector.Tests;

public class SquareShotDataMapperTests
{
    [Fact]
    public void ToBallData_ConvertsSpeedToMphAndPassesAnglesThrough()
    {
        var metrics = new SquareShotMetrics(
            BallSpeedMps: 50.0f,
            VerticalAngle: 15.0f,
            HorizontalAngle: 2.0f,
            TotalSpinRpm: 3000,
            SpinAxis: 5.0f,
            BackSpinRpm: 2900,
            SideSpinRpm: 261,
            ShotType: "full");

        var data = SquareShotDataMapper.ToBallData(metrics);

        Assert.Equal(50.0f * 2.23694f, (float)data["Speed"], 3);
        Assert.Equal(15.0f, (float)data["VLA"]);
        Assert.Equal(2.0f, (float)data["HLA"]);
        Assert.Equal(5.0f, (float)data["SpinAxis"]);
        Assert.Equal("full", (string)data["ShotType"]);
    }

    [Fact]
    public void ToBallData_FloorsNegativeTotalSpinAtZero()
    {
        var metrics = new SquareShotMetrics(
            BallSpeedMps: 50.0f,
            VerticalAngle: 15.0f,
            HorizontalAngle: 2.0f,
            TotalSpinRpm: -120,
            SpinAxis: 0.0f,
            BackSpinRpm: 0,
            SideSpinRpm: 0,
            ShotType: "full");

        var data = SquareShotDataMapper.ToBallData(metrics);

        Assert.Equal(0, (int)data["TotalSpin"]);
    }
}
