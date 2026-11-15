using System.Globalization;
using System.Text;

namespace HuiTian.Infrastructure;

/// <summary>
/// Resolves names only from the embedded, China-only derivative of the
/// GeoNames cities15000 dump. Ambiguous or unknown names deliberately fail so
/// that callers can hand the request to the normal research workflow.
/// </summary>
internal static class GeoNamesChinaCityResolver
{
    private const string ResourceSuffix = "Data.geonames-cn-cities.tsv";

    private static readonly string[] AdministrativeSuffixes =
    [
        "特别行政区",
        "维吾尔自治区",
        "壮族自治区",
        "回族自治区",
        "自治区",
        "自治州",
        "地区",
        "市",
        "县",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<CityRecord>>> Lookup =
        new(BuildLookup, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryResolve(string location, out WeatherLocation resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var requestedName = location.Trim();
        var exactKey = Normalize(requestedName);
        if (TryResolveUnique(exactKey, requestedName, out resolved))
        {
            return true;
        }

        var shortenedKey = RemoveAdministrativeSuffix(exactKey);
        return !string.Equals(shortenedKey, exactKey, StringComparison.Ordinal) &&
               TryResolveUnique(shortenedKey, requestedName, out resolved);
    }

    private static bool TryResolveUnique(
        string key,
        string requestedName,
        out WeatherLocation resolved)
    {
        resolved = null!;
        if (!Lookup.Value.TryGetValue(key, out var candidates))
        {
            return false;
        }

        var unique = candidates
            .GroupBy(candidate => candidate.GeoNameId)
            .Select(group => group.First())
            .ToArray();
        if (unique.Length != 1)
        {
            return false;
        }

        var city = unique[0];
        resolved = new WeatherLocation(
            city.GeoNameId,
            requestedName,
            city.Name,
            city.Latitude,
            city.Longitude,
            city.Elevation,
            city.TimeZoneId);
        return true;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<CityRecord>> BuildLookup()
    {
        var assembly = typeof(GeoNamesChinaCityResolver).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                "The embedded GeoNames China city data resource is missing.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                "The embedded GeoNames China city data resource could not be opened.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var mutable = new Dictionary<string, List<CityRecord>>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var columns = line.Split('\t');
            if (columns.Length < 9 || columns[0] == "geoname_id")
            {
                continue;
            }

            if (!long.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                !double.TryParse(columns[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(columns[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                continue;
            }

            var population = long.TryParse(
                columns[6],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedPopulation)
                ? parsedPopulation
                : 0;
            int? elevation = int.TryParse(
                columns[7],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedElevation)
                ? parsedElevation
                : null;
            var record = new CityRecord(
                id,
                columns[1],
                latitude,
                longitude,
                population,
                elevation,
                string.IsNullOrWhiteSpace(columns[8]) ? "Asia/Shanghai" : columns[8]);

            foreach (var alias in EnumerateAliases(columns[1], columns[2]))
            {
                AddAlias(mutable, Normalize(alias), record);
                AddAlias(mutable, RemoveAdministrativeSuffix(Normalize(alias)), record);
            }
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CityRecord>)pair.Value
                .OrderByDescending(city => city.Population)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateAliases(string name, string aliases)
    {
        yield return name;
        foreach (var alias in aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return alias;
        }
    }

    private static void AddAlias(
        Dictionary<string, List<CityRecord>> lookup,
        string key,
        CityRecord record)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!lookup.TryGetValue(key, out var values))
        {
            values = [];
            lookup.Add(key, values);
        }

        if (values.All(value => value.GeoNameId != record.GeoNameId))
        {
            values.Add(record);
        }
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToLowerInvariant();
        if (normalized.StartsWith("中国", StringComparison.Ordinal) && normalized.Length > 2)
        {
            normalized = normalized[2..];
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsWhiteSpace(character) &&
                character is not '-' and not '_' and not '·' and not '•' and
                not '\'' and not '’' and not '.' and not '。')
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string RemoveAdministrativeSuffix(string value)
    {
        foreach (var suffix in AdministrativeSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) && value.Length > suffix.Length + 1)
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }

    private sealed record CityRecord(
        long GeoNameId,
        string Name,
        double Latitude,
        double Longitude,
        long Population,
        int? Elevation,
        string TimeZoneId);
}
