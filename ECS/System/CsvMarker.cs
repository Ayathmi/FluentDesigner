using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FluentDesigner.ECS.System;

public enum CsvMarkerType
{
    Cue,
    Cart,
    Track,
    Subclip
}

public enum CsvTimeFormat
{
    Decimal
}

public sealed class CsvMarker
{
    public string Name { get; set; }
    public float StartTimeSeconds { get; set; }
    public float DurationSeconds { get; set; }
    public float EndTimeSeconds => StartTimeSeconds + DurationSeconds;
    public bool HasDuration => DurationSeconds > 0.001f;
    public CsvTimeFormat TimeFormat { get; set; }
    public CsvMarkerType Type { get; set; }
    public string Description { get; set; }
}

internal sealed class CsvMarkerRaw
{
    public string Name { get; set; }
    public string Start { get; set; }
    public string Duration { get; set; }
    public string TimeFormat { get; set; }
    public string Type { get; set; }
    public string Description { get; set; }
}

internal sealed class CsvMarkerRawMap : ClassMap<CsvMarkerRaw>
{
    public CsvMarkerRawMap()
    {
        Map(m => m.Name).Name("Name");
        Map(m => m.Start).Name("Start");
        Map(m => m.Duration).Name("Duration");
        Map(m => m.TimeFormat).Name("Time Format");
        Map(m => m.Type).Name("Type");
        Map(m => m.Description).Name("Description").Optional();
    }
}

public static class CsvMarkerTimeParser
{
    private static readonly Regex TimeRegex = new(
        @"^(\d{1,2}):(\d{2})\.(\d{1,3})$",
        RegexOptions.Compiled);

    public static bool TryParse(string timeString, out float seconds)
    {
        seconds = 0f;

        if (string.IsNullOrWhiteSpace(timeString))
        {
            return false;
        }

        var match = TimeRegex.Match(timeString.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out int minutes) ||
            !int.TryParse(match.Groups[2].Value, out int secs) ||
            !int.TryParse(match.Groups[3].Value, out int millis))
        {
            return false;
        }

        string millisStr = match.Groups[3].Value;
        if (millisStr.Length == 1)
        {
            millis *= 100;
        }
        else if (millisStr.Length == 2)
        {
            millis *= 10;
        }

        seconds = minutes * 60f + secs + millis / 1000f;
        return true;
    }
}

public static class CsvMarkerTypeParser
{
    public static bool TryParse(string typeString, out CsvMarkerType markerType)
    {
        markerType = CsvMarkerType.Cue;

        if (string.IsNullOrWhiteSpace(typeString))
        {
            return false;
        }

        return typeString.Trim().ToLowerInvariant() switch
        {
            "cue" => SetAndReturn(CsvMarkerType.Cue, out markerType),
            "cart" => SetAndReturn(CsvMarkerType.Cart, out markerType),
            "track" => SetAndReturn(CsvMarkerType.Track, out markerType),
            "subclip" => SetAndReturn(CsvMarkerType.Subclip, out markerType),
            _ => false
        };
    }

    private static bool SetAndReturn(CsvMarkerType value, out CsvMarkerType result)
    {
        result = value;
        return true;
    }

    public static string[] ValidTypeNames => ["Cue", "Cart", "Track", "Subclip"];
}

public static class CsvTimeFormatParser
{
    public static bool TryParse(string formatString, out CsvTimeFormat timeFormat)
    {
        timeFormat = CsvTimeFormat.Decimal;

        if (string.IsNullOrWhiteSpace(formatString))
        {
            return false;
        }

        return formatString.Trim().ToLowerInvariant() switch
        {
            "decimal" => true,
            _ => false
        };
    }
    public static string[] ValidFormatNames => ["decimal"];
}

public sealed class CsvImportResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public int MarkerCount { get; set; }
    public float MaxTimeSeconds { get; set; }
    public int ErrorLineNumber { get; set; }
    public string ErrorFieldName { get; set; }
}