using System.Diagnostics;
using System.IO;
using System.Reflection;
using HuiTian.Infrastructure;

internal static class LocalBackendProxyRegression
{
    internal static void Run()
    {
        var configure = typeof(CodexAppServerClient).GetMethod("ConfigureLocalBackendProxy", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parent = Environment.GetEnvironmentVariable("NO_PROXY");
        foreach (var endpoint in new[] { "http://127.0.0.1:18181/v1", "http://localhost:18293/v1", "http://[::1]:18181/v1" })
        {
            var child = new ProcessStartInfo();
            child.Environment["NO_PROXY"] = "internal.example, localhost";
            child.Environment["HTTPS_PROXY"] = "http://proxy.example:9000";
            configure.Invoke(null, new object[] { child, endpoint });
            var exclusions = child.Environment["NO_PROXY"]!.Split(',');
            Check(exclusions.Contains("internal.example") && exclusions.Contains("127.0.0.1") &&
                exclusions.Contains("::1") && exclusions.Count(value => value.Equals("localhost", StringComparison.OrdinalIgnoreCase)) == 1,
                "Local backends must preserve existing exclusions and cover both loopback families without duplicates.");
            Check(child.Environment["HTTPS_PROXY"] == "http://proxy.example:9000", "The proxy itself must remain unchanged.");
        }
        foreach (var endpoint in new[] { "https://model.example/v1", "http://192.168.1.2/v1", "invalid", "file:///local" })
        {
            var child = new ProcessStartInfo();
            child.Environment.Remove("NO_PROXY");
            configure.Invoke(null, new object[] { child, endpoint });
            Check(!child.Environment.ContainsKey("NO_PROXY"), "Non-loopback or invalid backends must not receive proxy overrides.");
        }
        Check(Environment.GetEnvironmentVariable("NO_PROXY") == parent, "The parent environment must remain unchanged.");
        Console.WriteLine("PASS local backend proxy: IPv4/IPv6/localhost direct, existing exclusions preserved, remote backends and parent unchanged");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
