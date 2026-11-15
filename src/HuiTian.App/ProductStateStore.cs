using System.Text;
using System.Text.Json;
using System.IO;
using HuiTian.Core;

namespace HuiTian.App;

internal sealed class ProductState
{
    public bool HasCompletedFirstRun { get; set; }

    public string Theme { get; set; } = "Dark";

    // Zero marks settings saved before the personal edition's visual refresh.
    // This lets the old light default move once without overriding later choices.
    public int VisualThemeVersion { get; set; }

    public string Language { get; set; } = "zh-CN";

    public string VoiceName { get; set; } = string.Empty;

    public bool SpeakAnswers { get; set; } = true;

    public bool WakeWordEnabled { get; set; } = true;

    public string WakePhrase { get; set; } = "你好小天";

    public bool IdleSelfEvolutionEnabled { get; set; } = true;

    public int SelfEvolutionRunCount { get; set; }

    public DateTimeOffset? LastSelfEvolutionAt { get; set; }

    public string SelfEvolutionSummary { get; set; } =
        "尚未完成第一次本机复盘";

    public bool ArtifactsPaneExpanded { get; set; } = true;

    // Keep the serialized default at zero so installations created before the
    // artifact filter existed are migrated the next time they are opened.
    public int ArtifactDiscoveryVersion { get; set; }

    public string BrainMode { get; set; } = "Automatic";

    public string AutonomyMode { get; set; } = nameof(HuiTian.Core.AutonomyMode.HighAutonomy);

    public string ExternalServiceAddress { get; set; } = "http://127.0.0.1:11434/v1";

    public string ExternalServiceName { get; set; } = string.Empty;

    public List<SavedTask> RecentTasks { get; set; } = [];

    public List<string> UserPreferences { get; set; } = [];

    public List<TaskLearning> TaskLearnings { get; set; } = [];

    // Zero is the serialized legacy value; new stores are initialized to the
    // current schema explicitly in ProductStateStore.Load.
    public int LearningSchemaVersion { get; set; }
}

internal sealed class SavedTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? ThreadId { get; set; }

    public string Title { get; set; } = "新的本机任务";

    public string Summary { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public List<SavedMessage> Messages { get; set; } = [];

    public List<SavedSubtask> Subtasks { get; set; } = [];

    public List<string> Artifacts { get; set; } = [];

    public string IntentJson { get; set; } = string.Empty;

    public string TaskGraphJson { get; set; } = string.Empty;
}

internal sealed class SavedSubtask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Request { get; set; } = string.Empty;

    public string Status { get; set; } = "pending";

    public string ResultSummary { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

internal sealed class SavedMessage
{
    public string Role { get; set; } = "assistant";

    public string Text { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public List<string> Attachments { get; set; } = [];
}

internal sealed class TaskLearning
{
    public string Trigger { get; set; } = string.Empty;

    public string Strategy { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    public string Category { get; set; } = string.Empty;

    public bool VerifiedSuccess { get; set; }

    public string VerificationEvidence { get; set; } = string.Empty;

    public int SuccessCount { get; set; }

    public int FailureCount { get; set; }

    public double AverageDurationSeconds { get; set; }

    public double Confidence { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public void RefreshScore()
    {
        var runs = SuccessCount + FailureCount;
        Succeeded = SuccessCount > 0 && SuccessCount >= FailureCount;
        Confidence = runs == 0
            ? 0
            : Math.Round((SuccessCount + 1d) / (runs + 2d), 4);
    }
}

internal sealed class ProductStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public ProductStateStore(string appStateDirectory)
    {
        Directory.CreateDirectory(appStateDirectory);
        _path = Path.Combine(appStateDirectory, "product-state.json");
    }

    public ProductState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new ProductState
                {
                    ArtifactDiscoveryVersion = 3,
                    LearningSchemaVersion = 2,
                    VisualThemeVersion = 1,
                };
            }

            var state = JsonSerializer.Deserialize<ProductState>(
                File.ReadAllText(_path, Encoding.UTF8),
                _options) ?? new ProductState();
            state.RecentTasks ??= [];
            state.UserPreferences ??= [];
            state.TaskLearnings ??= [];
            if (state.VisualThemeVersion < 1)
            {
                if (string.Equals(state.Theme, "Light", StringComparison.OrdinalIgnoreCase))
                {
                    state.Theme = "Dark";
                }

                state.VisualThemeVersion = 1;
            }
            if (!Enum.TryParse<AutonomyMode>(state.AutonomyMode, true, out _))
            {
                state.AutonomyMode = nameof(AutonomyMode.HighAutonomy);
            }
            foreach (var task in state.RecentTasks)
            {
                task.Messages ??= [];
                task.Subtasks ??= [];
                task.Artifacts ??= [];
                foreach (var message in task.Messages)
                {
                    message.Attachments ??= [];
                }

                task.Summary = ConversationPolicy.BuildConversationSummary(
                    task.Messages.Select(message =>
                        new ConversationMessageSnapshot(message.Role, message.Text)));
                if (task.Subtasks.Count == 0)
                {
                    for (var index = 0; index < task.Messages.Count; index++)
                    {
                        var message = task.Messages[index];
                        if (!message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(message.Text))
                        {
                            continue;
                        }

                        var nextUser = task.Messages.FindIndex(index + 1, item =>
                            item.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
                        var end = nextUser < 0 ? task.Messages.Count : nextUser;
                        var result = task.Messages
                            .Skip(index + 1)
                            .Take(end - index - 1)
                            .LastOrDefault(item => item.Role.Equals(
                                "assistant",
                                StringComparison.OrdinalIgnoreCase));
                        task.Subtasks.Add(new SavedSubtask
                        {
                            Request = message.Text,
                            Status = result is null ? "failed" : "completed",
                            ResultSummary = result is null
                                ? string.Empty
                                : CompactStateText(result.Text, 160),
                            StartedAt = message.Timestamp,
                            UpdatedAt = result?.Timestamp ?? message.Timestamp,
                        });
                    }
                }
            }

            // Versions before schema 2 stored any sentence containing words
            // such as “以后” as a preference and treated failed/unverified
            // tool traces as reusable skills.  Discard that one-time dirty
            // state instead of feeding it back into later tasks.
            if (state.LearningSchemaVersion < 2)
            {
                state.UserPreferences = state.UserPreferences
                    .Where(value => ConversationPolicy.TryExtractExplicitPreference(
                        value,
                        out _))
                    .ToList();
                state.TaskLearnings.Clear();
                state.LearningSchemaVersion = 2;
            }

            if (state.ArtifactDiscoveryVersion < 3)
            {
                foreach (var task in state.RecentTasks)
                {
                    task.Artifacts = task.Artifacts
                        .Where(path => path.StartsWith(
                            @"D:\HuiTian\UserContent\",
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                state.ArtifactDiscoveryVersion = 3;
            }

            if (NormalizeWakePhrase(state.WakePhrase).Length < 2)
            {
                state.WakePhrase = "你好小天";
            }

            if (state.VoiceName.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
            {
                state.VoiceName = string.Empty;
            }
            else if (state.VoiceName.StartsWith("huitian-natural:", StringComparison.OrdinalIgnoreCase) &&
                     !new[] { 0 }
                         .Select(id => $"huitian-natural:{id}")
                         .Contains(state.VoiceName, StringComparer.OrdinalIgnoreCase))
            {
                // Move retired speaker choices to the new local natural voice.
                state.VoiceName = string.Empty;
            }

            state.UserPreferences = state.UserPreferences
                .Where(value => ConversationPolicy.TryExtractExplicitPreference(value, out _))
                .ToList();
            state.TaskLearnings = state.TaskLearnings
                .Where(learning => learning.VerifiedSuccess && learning.SuccessCount > 0)
                .ToList();
            foreach (var learning in state.TaskLearnings)
            {
                learning.Category = string.IsNullOrWhiteSpace(learning.Category)
                    ? SelfEvolutionEngine.Classify(learning.Trigger)
                    : learning.Category;
                if (learning.SuccessCount == 0 && learning.FailureCount == 0)
                {
                    if (learning.Succeeded) learning.SuccessCount = 1;
                    else learning.FailureCount = 1;
                }

                learning.RefreshScore();
            }

            return state;
        }
        catch (JsonException)
        {
            return new ProductState
            {
                ArtifactDiscoveryVersion = 3,
                LearningSchemaVersion = 2,
                VisualThemeVersion = 1,
            };
        }
    }

    public async Task SaveAsync(ProductState state, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            state.RecentTasks = state.RecentTasks
                .OrderByDescending(task => task.UpdatedAt)
                .Take(40)
                .ToList();
            state.UserPreferences = state.UserPreferences
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .TakeLast(60)
                .ToList();
            state.TaskLearnings = state.TaskLearnings
                .OrderByDescending(item => item.UpdatedAt)
                .Take(100)
                .ToList();
            var temporaryPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(state, _options);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static string NormalizeWakePhrase(string? value) => string.Concat(
        (value ?? string.Empty).Where(character =>
            !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));

    private static string CompactStateText(string value, int maximumLength)
    {
        var compact = string.Join(" ", value
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximumLength
            ? compact
            : compact[..Math.Max(1, maximumLength - 1)] + "…";
    }
}
