using System.Collections.Generic;
using Godot;
using GodotDictionary = Godot.Collections.Dictionary;

namespace LaunchMonitors.Square;

public static class SquareGodotMapper
{
    public static GodotDictionary ToBallData(SquareShotMetrics metrics)
    {
        return ToGodotDictionary(SquareShotDataMapper.ToBallData(metrics));
    }

    public static GodotDictionary ToClubData(SquareClubMetrics metrics)
    {
        return ToGodotDictionary(SquareShotDataMapper.ToClubData(metrics));
    }

    private static GodotDictionary ToGodotDictionary(IReadOnlyDictionary<string, object> values)
    {
        var data = new GodotDictionary();

        foreach (var item in values)
        {
            data[item.Key] = ToVariant(item.Value);
        }

        return data;
    }

    private static Variant ToVariant(object value)
    {
        return value switch
        {
            float floatValue => Variant.From(floatValue),
            int intValue => Variant.From(intValue),
            string stringValue => Variant.From(stringValue),
            _ => Variant.From(value.ToString() ?? string.Empty)
        };
    }
}
