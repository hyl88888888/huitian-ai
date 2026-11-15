using System.Globalization;
using System.Text.Json;

namespace HuiTian.Infrastructure;

/// <summary>
/// Converts the MET Norway Locationforecast 2.0 compact response into seven
/// calendar days in China Standard Time. The daily values are a HuiTian
/// derivative: temperature extrema, shortest-horizon precipitation sums and a
/// duration-weighted representative symbol.
/// </summary>
public static class MetNorwayLocationForecastParser
{
    private static readonly TimeSpan ChinaStandardTimeOffset = TimeSpan.FromHours(8);

    public const string AttributionText =
        "天气数据：MET Norway Locationforecast 2.0（NLOD 2.0 / CC BY 4.0）；" +
        "地点坐标：GeoNames（CC BY 4.0）。回天按中国标准时间进行了逐日汇总和中文化处理。";

    public static SevenDayWeatherForecast Parse(
        string json,
        WeatherLocation location,
        Uri sourceUri,
        DateTimeOffset retrievedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(sourceUri);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        if (!document.RootElement.TryGetProperty("properties", out var properties) ||
            !properties.TryGetProperty("timeseries", out var timeseries) ||
            timeseries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "MET Norway response does not contain properties.timeseries.");
        }

        var updatedAtUtc = ReadUpdatedAt(properties, retrievedAtUtc);
        var readings = new List<ForecastReading>();
        foreach (var item in timeseries.EnumerateArray())
        {
            if (!item.TryGetProperty("time", out var timeValue) ||
                timeValue.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    timeValue.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utcTime) ||
                !item.TryGetProperty("data", out var data) ||
                !TryReadTemperature(data, out var temperature))
            {
                continue;
            }

            var period = ReadShortestForecastPeriod(data);
            readings.Add(new ForecastReading(
                utcTime.ToOffset(ChinaStandardTimeOffset),
                temperature,
                period.SymbolCode,
                period.PrecipitationMillimetres,
                period.DurationHours));
        }

        var days = readings
            .GroupBy(reading => DateOnly.FromDateTime(reading.LocalTime.DateTime))
            .OrderBy(group => group.Key)
            .Select(BuildDay)
            .Take(7)
            .ToArray();
        if (days.Length != 7)
        {
            throw new InvalidDataException(
                $"MET Norway response yielded {days.Length} daily forecasts; seven are required.");
        }

        return new SevenDayWeatherForecast(
            location,
            updatedAtUtc,
            retrievedAtUtc.ToUniversalTime(),
            days,
            sourceUri,
            AttributionText);
    }

    private static DateTimeOffset ReadUpdatedAt(
        JsonElement properties,
        DateTimeOffset fallback)
    {
        if (properties.TryGetProperty("meta", out var meta) &&
            meta.TryGetProperty("updated_at", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return fallback.ToUniversalTime();
    }

    private static bool TryReadTemperature(JsonElement data, out double temperature)
    {
        temperature = 0;
        return data.TryGetProperty("instant", out var instant) &&
               instant.TryGetProperty("details", out var details) &&
               details.TryGetProperty("air_temperature", out var value) &&
               value.TryGetDouble(out temperature) &&
               double.IsFinite(temperature);
    }

    private static ForecastPeriod ReadShortestForecastPeriod(JsonElement data)
    {
        foreach (var candidate in new[]
                 {
                     (Name: "next_1_hours", Hours: 1),
                     (Name: "next_6_hours", Hours: 6),
                     (Name: "next_12_hours", Hours: 12),
                 })
        {
            if (!data.TryGetProperty(candidate.Name, out var period) ||
                period.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? symbolCode = null;
            if (period.TryGetProperty("summary", out var summary) &&
                summary.TryGetProperty("symbol_code", out var symbol) &&
                symbol.ValueKind == JsonValueKind.String)
            {
                symbolCode = symbol.GetString();
            }

            var precipitation = 0d;
            if (period.TryGetProperty("details", out var details) &&
                details.TryGetProperty("precipitation_amount", out var amount) &&
                amount.TryGetDouble(out var parsedAmount) &&
                double.IsFinite(parsedAmount) &&
                parsedAmount >= 0)
            {
                precipitation = parsedAmount;
            }

            return new ForecastPeriod(
                NormalizeSymbol(symbolCode),
                precipitation,
                candidate.Hours);
        }

        return new ForecastPeriod(null, 0, 0);
    }

    private static DailyWeatherForecast BuildDay(
        IGrouping<DateOnly, ForecastReading> group)
    {
        var values = group.ToArray();
        var symbolCode = SelectRepresentativeSymbol(values);
        return new DailyWeatherForecast(
            group.Key,
            RoundOneDecimal(values.Min(value => value.TemperatureCelsius)),
            RoundOneDecimal(values.Max(value => value.TemperatureCelsius)),
            TranslateSymbol(symbolCode),
            RoundOneDecimal(values.Sum(value => value.PrecipitationMillimetres)),
            symbolCode);
    }

    private static string SelectRepresentativeSymbol(
        IReadOnlyList<ForecastReading> readings)
    {
        var candidate = readings
            .Where(reading => !string.IsNullOrWhiteSpace(reading.SymbolCode))
            .GroupBy(reading => reading.SymbolCode!, StringComparer.Ordinal)
            .Select(group => new
            {
                Code = group.Key,
                Duration = group.Sum(reading => Math.Max(reading.DurationHours, 1)),
                NoonDistance = group.Min(reading =>
                    Math.Abs(reading.LocalTime.TimeOfDay.TotalHours - 12)),
                Severity = SymbolSeverity(group.Key),
            })
            .OrderByDescending(value => value.Duration)
            .ThenBy(value => value.NoonDistance)
            .ThenByDescending(value => value.Severity)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .FirstOrDefault();
        return candidate?.Code ?? "unknown";
    }

    private static string? NormalizeSymbol(string? symbolCode)
    {
        if (string.IsNullOrWhiteSpace(symbolCode))
        {
            return null;
        }

        var value = symbolCode.Trim().ToLowerInvariant();
        foreach (var suffix in new[] { "_polartwilight", "_night", "_day" })
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }

    private static string TranslateSymbol(string code)
    {
        if (code.Contains("thunder", StringComparison.Ordinal))
        {
            if (code.Contains("snow", StringComparison.Ordinal))
            {
                return "雷阵雪";
            }

            if (code.Contains("sleet", StringComparison.Ordinal))
            {
                return "雷雨夹雪";
            }

            return "雷雨";
        }

        if (code.Contains("fog", StringComparison.Ordinal))
        {
            return "雾";
        }

        if (code.Contains("rain", StringComparison.Ordinal))
        {
            var showers = code.Contains("showers", StringComparison.Ordinal);
            if (code.Contains("heavy", StringComparison.Ordinal))
            {
                return showers ? "强阵雨" : "大雨";
            }

            if (code.Contains("light", StringComparison.Ordinal))
            {
                return showers ? "小阵雨" : "小雨";
            }

            return showers ? "阵雨" : "雨";
        }

        if (code.Contains("sleet", StringComparison.Ordinal))
        {
            return "雨夹雪";
        }

        if (code.Contains("snow", StringComparison.Ordinal))
        {
            if (code.Contains("heavy", StringComparison.Ordinal))
            {
                return "大雪";
            }

            if (code.Contains("light", StringComparison.Ordinal))
            {
                return "小雪";
            }

            return code.Contains("showers", StringComparison.Ordinal) ? "阵雪" : "雪";
        }

        return code switch
        {
            "clearsky" => "晴",
            "fair" => "晴间多云",
            "partlycloudy" => "多云",
            "cloudy" => "阴",
            _ => "天气情况待确认",
        };
    }

    private static int SymbolSeverity(string code)
    {
        if (code.Contains("thunder", StringComparison.Ordinal)) return 8;
        if (code.Contains("heavy", StringComparison.Ordinal)) return 7;
        if (code.Contains("snow", StringComparison.Ordinal)) return 6;
        if (code.Contains("sleet", StringComparison.Ordinal)) return 5;
        if (code.Contains("rain", StringComparison.Ordinal)) return 4;
        if (code.Contains("fog", StringComparison.Ordinal)) return 3;
        if (code == "cloudy") return 2;
        if (code == "partlycloudy") return 1;
        return 0;
    }

    private static double RoundOneDecimal(double value) =>
        Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private sealed record ForecastReading(
        DateTimeOffset LocalTime,
        double TemperatureCelsius,
        string? SymbolCode,
        double PrecipitationMillimetres,
        int DurationHours);

    private sealed record ForecastPeriod(
        string? SymbolCode,
        double PrecipitationMillimetres,
        int DurationHours);
}
