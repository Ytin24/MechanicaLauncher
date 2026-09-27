using MechanicaLauncher.Core.Instances;

namespace MechanicaLauncher.Core.Servers;

public enum ServerSyncChangeKind { Add, Replace, Remove }
public sealed record ServerSyncChange(ServerSyncChangeKind Kind, string ArtifactId, string FileName, long SizeBytes);
public sealed record ServerSyncConflict(string FileName, string Reason);
public sealed record ServerSyncTarget(string TargetId, string Minecraft, string? Loader, string? LoaderVersion);

public sealed class ServerSyncPlan
{
    public Guid PlanId { get; } = Guid.NewGuid();
    public Guid ServerId { get; internal init; }
    public ServerSyncTarget Target { get; internal init; } = null!;
    public long Revision { get; internal init; }
    public string ManifestSha512 { get; internal init; } = "";
    public string GameDir { get; internal init; } = "";
    public DateTimeOffset ExpiresUtc { get; internal init; }
    public IReadOnlyList<ServerSyncChange> Changes { get; internal init; } = [];
    public IReadOnlyList<ServerSyncConflict> Conflicts { get; internal init; } = [];
    public bool HasChanges => Changes.Count > 0;
    public long DownloadBytes => Changes.Where(c => c.Kind != ServerSyncChangeKind.Remove).Sum(c => c.SizeBytes);
    public string Summary => $"Добавить: {Changes.Count(c => c.Kind == ServerSyncChangeKind.Add)}, заменить: {Changes.Count(c => c.Kind == ServerSyncChangeKind.Replace)}, убрать: {Changes.Count(c => c.Kind == ServerSyncChangeKind.Remove)}";
    internal ServerSyncPlan() { }
    internal Uri DescriptorUrl { get; init; } = null!;
    internal bool AllowLocalSource { get; init; }
    internal GameInstance Instance { get; init; } = null!;
    internal IReadOnlyList<ServerSyncArtifact> Artifacts { get; init; } = [];
    internal IReadOnlyList<ServerSyncLocalFile> OriginalFiles { get; init; } = [];
    internal string? StateHash { get; init; }
    internal ServerSyncState NextState { get; init; } = new();
    internal IReadOnlyList<string> RemoveFiles { get; init; } = [];
}

public sealed class ServerSyncStage
{
    public ServerSyncPlan Plan { get; }
    public string DirectoryPath { get; }
    public IReadOnlyDictionary<string, string> Files { get; }
    internal ServerSyncStage(ServerSyncPlan plan, string directory, IReadOnlyDictionary<string, string> files)
        => (Plan, DirectoryPath, Files) = (plan, directory, files);
}

public sealed class ServerModSyncException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

internal sealed record ServerSyncArtifact(string ArtifactId, string[] ModIds, string FileName, long Size,
    string Sha512, Uri Url, bool External, string? ProjectId, string? VersionId);
internal sealed record ServerSyncLocalFile(string FileName, long Size, string Sha512, string[] ModIds, bool Enabled);
internal sealed record ServerSyncManagedFile(string ArtifactId, string FileName, long Size, string Sha512);
internal sealed record ServerSyncManagedServer(Guid ServerId, string Origin, ServerSyncTarget Target, long Revision,
    string ManifestSha512, Guid LastPlanId, List<ServerSyncManagedFile> Files);
internal sealed class ServerSyncState
{
    public int Version { get; init; } = 1;
    public List<ServerSyncManagedServer> Servers { get; init; } = [];
}

internal sealed record ServerSyncSeen(Guid ServerId, string Origin, ServerSyncTarget Target, long Revision, string ManifestSha512);
internal sealed record ServerSyncJournalEntry(string FileName, string? BeforeHash, long BeforeSize, string? AfterHash, long AfterSize);
internal sealed class ServerSyncJournal
{
    public int Version { get; init; } = 1;
    public Guid PlanId { get; init; }
    public bool Committed { get; set; }
    public string? PreviousState { get; init; }
    public string NextStateHash { get; init; } = "";
    public List<ServerSyncJournalEntry> Entries { get; init; } = [];
}
