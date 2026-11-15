namespace HuiTian.Infrastructure;

public sealed class HuiTianPaths
{
    public HuiTianPaths(string? rootOverride = null)
    {
        Root = Path.GetFullPath(rootOverride ?? ResolveDefaultRoot());
        Project = Path.Combine(Root, "Project");
        Models = Path.Combine(Root, "Models");
        Runtime = Path.Combine(Root, "Runtime");
        Cache = Path.Combine(Root, "Cache");
        Downloads = Path.Combine(Root, "Downloads");
        Temp = Path.Combine(Root, "Temp");
        Build = Path.Combine(Root, "Build");
        UserContent = Path.Combine(Root, "UserContent");
        AppState = Path.Combine(Cache, "AppState");
        AgentHome = Path.Combine(Runtime, "Agent", "Home");
        AgentRuntime = Path.Combine(Runtime, "Agent", "huitian-agent.exe");
        BrainRuntime = Path.Combine(Runtime, "Brain");
    }

    public string Root { get; }

    public string Project { get; }

    public string Models { get; }

    public string Runtime { get; }

    public string Cache { get; }

    public string Downloads { get; }

    public string Temp { get; }

    public string Build { get; }

    public string UserContent { get; }

    public string AppState { get; }

    public string AgentHome { get; }

    public string AgentRuntime { get; }

    public string BrainRuntime { get; }

    public void EnsureDirectories()
    {
        foreach (var path in new[]
                 {
                     Root, Project, Models, Runtime, Cache, Downloads, Temp, Build, UserContent,
                     AppState, AgentHome, BrainRuntime,
                 })
        {
            Directory.CreateDirectory(path);
        }
    }

    private static string ResolveDefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("HUITIAN_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (DriveInfo.GetDrives().Any(drive =>
                drive.IsReady &&
                string.Equals(drive.Name, "D:\\", StringComparison.OrdinalIgnoreCase)))
        {
            return @"D:\HuiTian";
        }

        var executableDirectory = AppContext.BaseDirectory;
        return Path.Combine(executableDirectory, "HuiTianData");
    }
}
