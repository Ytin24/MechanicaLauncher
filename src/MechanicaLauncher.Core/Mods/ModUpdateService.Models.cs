using MechanicaLauncher.Core.Instances;
using MechanicaLauncher.Core.Models;

namespace MechanicaLauncher.Core.Mods;

public enum ModUpdateState { Available, Current, Unknown, Unavailable, Managed, Incompatible }
public sealed record ModUpdateItem(string FilePath, string FileName, bool Enabled, string Name,
    string CurrentVersion, string? TargetVersion, ModUpdateState State, string? Reason);
public sealed record ModUpdateChange(string FileName, string Name, string CurrentVersion, string TargetVersion,
    bool Dependency, bool Enabled, long DownloadBytes);
public sealed record ModUpdateConflict(string FileName, string Reason);
public sealed record ModUpdateResult(int UpdatedCount, string BackupDirectory);

public sealed class ModUpdateScan
{
    public string InstanceId { get; internal init; } = "";
    public string GameDir { get; internal init; } = "";
    public IReadOnlyList<ModUpdateItem> Items { get; internal init; } = [];
    public IReadOnlyList<string> Errors { get; internal init; } = [];
    internal GameInstance Instance { get; init; } = null!;
    internal string Minecraft { get; init; } = "";
    internal string Loader { get; init; } = "";
    internal string? LoaderVersion { get; init; }
    internal IReadOnlyList<ModUpdateLocal> Files { get; init; } = [];
    internal ModUpdateScan() { }
}

public sealed class ModUpdatePlan
{
    public Guid PlanId { get; } = Guid.NewGuid();
    public string InstanceId => Scan.InstanceId;
    public string GameDir => Scan.GameDir;
    public IReadOnlyList<ModUpdateChange> Changes { get; internal init; } = [];
    public IReadOnlyList<ModUpdateConflict> Conflicts { get; internal init; } = [];
    public long DownloadBytes => Changes.Sum(c => c.DownloadBytes);
    public bool HasChanges => Changes.Count > 0;
    internal ModUpdateScan Scan { get; init; } = null!;
    internal IReadOnlyList<ModUpdateFileChange> Files { get; init; } = [];
    internal ModUpdatePlan() { }
}

public sealed class ModUpdateException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

internal sealed record ModUpdateLocal(string FilePath, string FileName, bool Enabled, long Size, string Sha1, string Sha512,
    bool Managed = false, ModrinthVersion? Current = null, ModrinthVersion? Target = null, ModUpdateState State = ModUpdateState.Unknown,
    string? Reason = null);
internal sealed record ModUpdateFileChange(string FileName, ModUpdateLocal? Before, ModrinthFile File,
    ModrinthVersion Version, bool Dependency, bool Enabled);
