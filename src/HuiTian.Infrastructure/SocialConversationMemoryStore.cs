using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public enum SocialConversationTurnRole
{
    UserSent,
    ContactReceived,
}

public sealed record SocialConversationTurn(
    SocialConversationTurnRole Role,
    string Text,
    DateTimeOffset OccurredAt);

public sealed record SocialContactMemory(
    SocialContactKey Contact,
    string DisplayName,
    string UserProvidedNotes,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SocialConversationTurn> RecentTurns);

/// <summary>
/// A screened candidate, not a person the assistant has contacted. No profile biography,
/// private attributes, screenshots, or messages are stored in candidate records.
/// </summary>
public sealed record SocialCandidateMemory(
    SocialContactKey Contact,
    string DisplayName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastScreenedAt,
    bool HasVerifiedTurn,
    bool HasVerifiedOutgoing);

/// <summary>
/// Small, user-local memory for separately verified contacts. It is not a profile finder,
/// a message sender, or a license to infer facts about the user from generated messages.
/// Contents are encrypted for the current Windows user and never written to telemetry.
/// </summary>
public sealed class SocialConversationMemoryStore
{
    private const int MaximumContacts = 128;
    private const int MaximumCandidates = 128;
    private const int MaximumTurnsPerContact = 24;
    private const int MaximumStyleSamples = 12;
    private const int MaximumStyleSampleLength = 400;
    private const int MaximumTurnLength = 1_000;
    private const int MaximumNotesLength = 600;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan ContactRetention = TimeSpan.FromDays(90);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SocialConversationMemoryStore(HuiTianPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _filePath = Path.Combine(paths.UserContent, "Private", "social-conversations.bin");
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gate = FileGates.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Persist only a candidate that already passed SocialCandidatePolicy. This does not
    /// create a contact conversation or mark any outgoing message as sent.
    /// </summary>
    public async Task RememberEligibleCandidateAsync(
        SocialContactKey contact,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200)
            throw new ArgumentException("候选人的显示名称无效。", nameof(displayName));
        await MutateAsync(state =>
        {
            var now = _timeProvider.GetUtcNow();
            var candidate = FindCandidate(state, contact);
            if (candidate is null)
            {
                candidate = new SocialCandidateEntry
                {
                    Platform = contact.Platform,
                    ContactId = contact.StableContactId,
                    FirstSeenAt = now,
                };
                state.Candidates.Add(candidate);
            }
            candidate.DisplayName = displayName.Trim();
            candidate.LastScreenedAt = now;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SocialCandidateMemory?> ReadCandidateAsync(
        SocialContactKey contact,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        return await ReadAsync(state =>
        {
            var candidate = FindCandidate(state, contact);
            return candidate is null ? null : CandidateSnapshot(candidate, state);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SocialCandidateMemory>> ListCandidatesAsync(
        string? platform = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedPlatform = string.IsNullOrWhiteSpace(platform)
            ? null
            : SocialContactKey.NormalizePlatform(platform);
        return await ReadAsync(state => (IReadOnlyList<SocialCandidateMemory>)state.Candidates
            .Where(candidate => normalizedPlatform is null ||
                string.Equals(candidate.Platform, normalizedPlatform, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.LastScreenedAt)
            .Take(Math.Clamp(limit, 1, 20))
            .Select(candidate => CandidateSnapshot(candidate, state))
            .ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCandidateAsync(
        SocialContactKey contact,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        await MutateAsync(state => state.Candidates.RemoveAll(candidate => SameContact(candidate, contact)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Store only examples explicitly supplied by the user as their own writing.</summary>
    public async Task SetExplicitUserStyleSamplesAsync(
        IEnumerable<string> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var bounded = samples
            .Where(sample => !string.IsNullOrWhiteSpace(sample))
            .Select(sample => sample.Trim())
            .Where(sample => sample.Length <= MaximumStyleSampleLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumStyleSamples)
            .ToList();
        await MutateAsync(state => state.ExplicitUserStyleSamples = bounded, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadExplicitUserStyleSamplesAsync(
        CancellationToken cancellationToken = default)
    {
        return await ReadAsync(
            state => (IReadOnlyList<string>)state.ExplicitUserStyleSamples.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Call after the actual application has verified a stable platform contact ID.
    /// Display name and notes are for context only and must never be used as the routing key.
    /// </summary>
    public async Task RecordVerifiedContactAsync(
        SocialContactKey contact,
        string displayName,
        string? userProvidedNotes = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200)
            throw new ArgumentException("联系人显示名称无效。", nameof(displayName));
        if (userProvidedNotes?.Length > MaximumNotesLength)
            throw new ArgumentException("用户提供的备注过长。", nameof(userProvidedNotes));

        await MutateAsync(state =>
        {
            var entry = Find(state, contact);
            if (entry is null)
            {
                entry = new SocialContactEntry { Platform = contact.Platform, ContactId = contact.StableContactId };
                state.Contacts.Add(entry);
            }
            entry.DisplayName = displayName.Trim();
            if (userProvidedNotes is not null)
                entry.UserProvidedNotes = userProvidedNotes.Trim();
            entry.UpdatedAt = _timeProvider.GetUtcNow();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Call only after the visible conversation has been bound to this exact contact ID.
    /// UserSent requires a verified delivery outcome, not merely a successful key press.
    /// </summary>
    public async Task AppendVerifiedTurnAsync(
        SocialContactKey contact,
        SocialConversationTurnRole role,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTurnLength)
            throw new ArgumentException("聊天内容为空或过长。", nameof(text));

        await MutateAsync(state =>
        {
            var entry = Find(state, contact) ??
                        throw new InvalidOperationException("应先核验联系人稳定 ID，再记录会话。");
            var now = _timeProvider.GetUtcNow();
            entry.RecentTurns.Add(new SocialConversationTurn(role, text.Trim(), now));
            if (entry.RecentTurns.Count > MaximumTurnsPerContact)
                entry.RecentTurns.RemoveRange(0, entry.RecentTurns.Count - MaximumTurnsPerContact);
            entry.UpdatedAt = now;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SocialContactMemory?> ReadContactAsync(
        SocialContactKey contact,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        return await ReadAsync(state =>
        {
            var entry = Find(state, contact);
            return entry is null ? null : Snapshot(entry);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SocialContactMemory>> ListContactsAsync(
        CancellationToken cancellationToken = default)
    {
        return await ReadAsync(state => (IReadOnlyList<SocialContactMemory>)state.Contacts
            .OrderByDescending(entry => entry.UpdatedAt)
            .Select(Snapshot)
            .ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteContactAsync(
        SocialContactKey contact,
        CancellationToken cancellationToken = default)
    {
        ValidateContact(contact);
        await MutateAsync(state =>
        {
            state.Contacts.RemoveAll(entry => SameContact(entry, contact));
            state.Candidates.RemoveAll(entry => SameContact(entry, contact));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> ReadAsync<T>(Func<SocialConversationState, T> reader, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return reader(await LoadAsync(ct).ConfigureAwait(false)); }
        finally { _gate.Release(); }
    }

    private async Task MutateAsync(Action<SocialConversationState> mutation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            mutation(state);
            Prune(state);
            await SaveAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SocialConversationState> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return new SocialConversationState();
        var file = new FileInfo(_filePath);
        if (file.Length > MaximumFileBytes + 2_048)
            throw new InvalidDataException("社交会话记忆文件超过大小上限。");
        var encrypted = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
        var json = WindowsUserDataProtection.Unprotect(encrypted);
        var state = JsonSerializer.Deserialize<SocialConversationState>(json, _jsonOptions) ??
                    throw new InvalidDataException("社交会话记忆格式无效。");
        if (state.Version != 1 || state.Contacts is null || state.Candidates is null ||
            state.ExplicitUserStyleSamples is null)
            throw new InvalidDataException("社交会话记忆版本不兼容；原文件已保留。");
        if (Prune(state))
            await SaveAsync(state, ct).ConfigureAwait(false);
        return state;
    }

    private async Task SaveAsync(SocialConversationState state, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("社交会话记忆超过大小上限。");
        var protectedBytes = WindowsUserDataProtection.Protect(bytes);
        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, ct)
                .ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private bool Prune(SocialConversationState state)
    {
        var previousContacts = state.Contacts.Count;
        var previousCandidates = state.Candidates.Count;
        var previousTurns = state.Contacts.Sum(entry => entry.RecentTurns?.Count ?? 0);
        var previousStyleSamples = state.ExplicitUserStyleSamples.Count;
        var cutoff = _timeProvider.GetUtcNow() - ContactRetention;
        state.Contacts = state.Contacts
            .Where(entry => entry.UpdatedAt >= cutoff &&
                            !string.IsNullOrWhiteSpace(entry.Platform) &&
                            !string.IsNullOrWhiteSpace(entry.ContactId))
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(MaximumContacts)
            .ToList();
        state.Candidates = state.Candidates
            .Where(entry => entry.LastScreenedAt >= cutoff &&
                            !string.IsNullOrWhiteSpace(entry.Platform) &&
                            !string.IsNullOrWhiteSpace(entry.ContactId))
            .OrderByDescending(entry => entry.LastScreenedAt)
            .Take(MaximumCandidates)
            .ToList();
        foreach (var entry in state.Contacts)
        {
            entry.RecentTurns ??= [];
            if (entry.RecentTurns.Count > MaximumTurnsPerContact)
                entry.RecentTurns = entry.RecentTurns.TakeLast(MaximumTurnsPerContact).ToList();
        }
        state.ExplicitUserStyleSamples = state.ExplicitUserStyleSamples
            .Where(sample => !string.IsNullOrWhiteSpace(sample) && sample.Length <= MaximumStyleSampleLength)
            .Take(MaximumStyleSamples)
            .ToList();
        return state.Contacts.Count != previousContacts ||
               state.Candidates.Count != previousCandidates ||
               state.Contacts.Sum(entry => entry.RecentTurns.Count) != previousTurns ||
               state.ExplicitUserStyleSamples.Count != previousStyleSamples;
    }

    private static void ValidateContact(SocialContactKey contact)
    {
        if (!contact.IsValid)
            throw new ArgumentException("需要已核验的稳定联系人 ID。", nameof(contact));
    }

    private static SocialContactEntry? Find(SocialConversationState state, SocialContactKey contact) =>
        state.Contacts.FirstOrDefault(entry => SameContact(entry, contact));

    private static SocialCandidateEntry? FindCandidate(
        SocialConversationState state, SocialContactKey contact) =>
        state.Candidates.FirstOrDefault(entry => SameContact(entry, contact));

    private static bool SameContact(SocialContactEntry entry, SocialContactKey contact) =>
        string.Equals(entry.Platform, contact.Platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.ContactId, contact.StableContactId, StringComparison.Ordinal);

    private static bool SameContact(SocialCandidateEntry entry, SocialContactKey contact) =>
        string.Equals(entry.Platform, contact.Platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.ContactId, contact.StableContactId, StringComparison.Ordinal);

    private static SocialCandidateMemory CandidateSnapshot(
        SocialCandidateEntry candidate,
        SocialConversationState state)
    {
        var conversation = Find(state,
            new SocialContactKey(candidate.Platform, candidate.ContactId));
        return new SocialCandidateMemory(
            new SocialContactKey(candidate.Platform, candidate.ContactId),
            candidate.DisplayName,
            candidate.FirstSeenAt,
            candidate.LastScreenedAt,
            conversation is { RecentTurns.Count: > 0 },
            conversation?.RecentTurns.Any(turn => turn.Role == SocialConversationTurnRole.UserSent) == true);
    }

    private static SocialContactMemory Snapshot(SocialContactEntry entry) => new(
        new SocialContactKey(entry.Platform, entry.ContactId),
        entry.DisplayName,
        entry.UserProvidedNotes,
        entry.UpdatedAt,
        entry.RecentTurns.ToArray());
}

internal sealed class SocialConversationState
{
    public int Version { get; set; } = 1;
    public List<string> ExplicitUserStyleSamples { get; set; } = [];
    public List<SocialContactEntry> Contacts { get; set; } = [];
    public List<SocialCandidateEntry> Candidates { get; set; } = [];
}

internal sealed class SocialCandidateEntry
{
    public string Platform { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastScreenedAt { get; set; }
}

internal sealed class SocialContactEntry
{
    public string Platform { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UserProvidedNotes { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<SocialConversationTurn> RecentTurns { get; set; } = [];
}

/// <summary>Windows DPAPI, current user scope. No recoverable key is stored in the project.</summary>
internal static class WindowsUserDataProtection
{
    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data) => Transform(data, encrypt: true);
    public static byte[] Unprotect(byte[] data) => Transform(data, encrypt: false);

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("社交会话记忆仅支持 Windows 用户加密存储。");
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            DataBlob output;
            IntPtr description = IntPtr.Zero;
            var success = encrypt
                ? CryptProtectData(ref input, "HuiTian social conversation memory", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, out IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
