namespace HuiTian.Infrastructure;

public sealed record AgentBackendConfiguration(
    string ModelName,
    string ProviderName,
    string ResponsesBaseUrl,
    bool IsProductDefault)
{
    public static AgentBackendConfiguration ProductDefault { get; } = new(
        "huitian-local",
        "huitian_local",
        "http://127.0.0.1:18181/v1",
        true);

    public static AgentBackendConfiguration External(string modelName, string baseUrl) => new(
        string.IsNullOrWhiteSpace(modelName) ? "local-model" : modelName.Trim(),
        "huitian_external",
        baseUrl.TrimEnd('/'),
        false);
}
