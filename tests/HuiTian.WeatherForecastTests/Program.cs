using HuiTian.Infrastructure;

RunLocationResolverChecks();
RunCompactForecastParserChecks();

Console.WriteLine("PASS: offline China location resolution and MET Norway seven-day parsing");

static void RunLocationResolverChecks()
{
    AssertTrue(
        GeoNamesChinaCityResolver.TryResolve(" 北京市 ", out var beijing),
        "The Beijing city alias was not resolved.");
    AssertEqual("北京市", beijing.RequestedName, "RequestedName");
    AssertEqual("Beijing", beijing.GeoNamesName, "GeoNamesName");
    AssertEqual(1816670L, beijing.GeoNameId, "GeoNameId");
    AssertEqual("Asia/Shanghai", beijing.TimeZoneId, "TimeZoneId");
    AssertTrue(
        beijing.Latitude is >= 39d and <= 41d &&
        beijing.Longitude is >= 115d and <= 118d,
        "The resolved Beijing coordinates are outside the expected area.");

    AssertFalse(
        GeoNamesChinaCityResolver.TryResolve("   ", out _),
        "A blank location unexpectedly resolved.");
    AssertFalse(
        GeoNamesChinaCityResolver.TryResolve("不存在的测试城市", out _),
        "An unknown location unexpectedly resolved.");
}

static void RunCompactForecastParserChecks()
{
    var fixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "locationforecast-compact.json");
    var json = File.ReadAllText(fixturePath);
    var location = new WeatherLocation(
        1816670,
        "北京市",
        "Beijing",
        39.9042,
        116.4074,
        43,
        "Asia/Shanghai");
    var sourceUri = new Uri(
        "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=39.9042&lon=116.4074");
    var retrievedAtUtc = new DateTimeOffset(2026, 8, 26, 18, 0, 0, TimeSpan.Zero);

    var forecast = MetNorwayLocationForecastParser.Parse(
        json,
        location,
        sourceUri,
        retrievedAtUtc);

    AssertEqual(location, forecast.Location, "Location");
    AssertEqual(sourceUri, forecast.SourceUri, "SourceUri");
    AssertEqual(retrievedAtUtc, forecast.RetrievedAtUtc, "RetrievedAtUtc");
    AssertEqual(
        new DateTimeOffset(2026, 8, 27, 0, 15, 0, TimeSpan.Zero),
        forecast.UpdatedAtUtc,
        "UpdatedAtUtc");
    AssertTrue(
        forecast.Attribution.Contains("MET Norway", StringComparison.OrdinalIgnoreCase),
        "The forecast attribution does not credit MET Norway.");

    ExpectedDay[] expectedDays =
    [
        new(new DateOnly(2026, 8, 27), 19.5, 27.0, "晴", 0.3, "clearsky"),
        new(new DateOnly(2026, 8, 28), 18.0, 25.4, "多云", 1.0, "partlycloudy"),
        new(new DateOnly(2026, 8, 29), 17.2, 24.1, "晴间多云", 0.2, "fair"),
        new(new DateOnly(2026, 8, 30), 16.4, 22.6, "阴", 1.0, "cloudy"),
        new(new DateOnly(2026, 8, 31), 15.3, 20.2, "小雨", 2.5, "lightrain"),
        new(new DateOnly(2026, 9, 1), 14.8, 19.1, "雷雨", 14.0, "heavyrainandthunder"),
        new(new DateOnly(2026, 9, 2), -2.2, 1.8, "雪", 2.5, "snow"),
    ];

    AssertEqual(7, forecast.Days.Count, "Days.Count");
    for (var index = 0; index < expectedDays.Length; index++)
    {
        var expected = expectedDays[index];
        var actual = forecast.Days[index];
        var label = $"Days[{index}]";
        AssertEqual(expected.Date, actual.Date, $"{label}.Date");
        AssertNearlyEqual(expected.LowCelsius, actual.LowCelsius, $"{label}.LowCelsius");
        AssertNearlyEqual(expected.HighCelsius, actual.HighCelsius, $"{label}.HighCelsius");
        AssertEqual(expected.Condition, actual.Condition, $"{label}.Condition");
        AssertNearlyEqual(
            expected.PrecipitationMillimetres,
            actual.PrecipitationMillimetres,
            $"{label}.PrecipitationMillimetres");
        AssertEqual(expected.SymbolCode, actual.SymbolCode, $"{label}.SymbolCode");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}

static void AssertFalse(bool condition, string message) =>
    AssertTrue(!condition, message);

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidDataException(
            $"{label}: expected '{expected}', actual '{actual}'.");
    }
}

static void AssertNearlyEqual(double expected, double actual, string label)
{
    if (Math.Abs(expected - actual) > 0.0001d)
    {
        throw new InvalidDataException(
            $"{label}: expected {expected:0.####}, actual {actual:0.####}.");
    }
}

internal sealed record ExpectedDay(
    DateOnly Date,
    double LowCelsius,
    double HighCelsius,
    string Condition,
    double PrecipitationMillimetres,
    string SymbolCode);
