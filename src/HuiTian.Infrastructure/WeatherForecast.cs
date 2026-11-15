namespace HuiTian.Infrastructure;

public enum WeatherForecastFetchStatus
{
    Available,
    LocationNotFound,
    NetworkUnavailable,
    InvalidData,
}

public sealed record WeatherLocation(
    long GeoNameId,
    string RequestedName,
    string GeoNamesName,
    double Latitude,
    double Longitude,
    int? Elevation,
    string TimeZoneId);

public sealed record DailyWeatherForecast(
    DateOnly Date,
    double LowCelsius,
    double HighCelsius,
    string Condition,
    double PrecipitationMillimetres,
    string SymbolCode);

public sealed record SevenDayWeatherForecast(
    WeatherLocation Location,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<DailyWeatherForecast> Days,
    Uri SourceUri,
    string Attribution);

public sealed record WeatherForecastFetchResult(
    WeatherForecastFetchStatus Status,
    SevenDayWeatherForecast? Forecast,
    string Message)
{
    public bool Success =>
        Status == WeatherForecastFetchStatus.Available && Forecast is not null;
}

public interface IWeatherForecastProvider
{
    Task<WeatherForecastFetchResult> GetSevenDayForecastAsync(
        string location,
        CancellationToken cancellationToken = default);
}
