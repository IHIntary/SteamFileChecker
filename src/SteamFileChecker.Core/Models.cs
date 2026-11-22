namespace SteamFileChecker.Core;

public record InstalledDepot(uint Id, string ManifestId, uint? DlcAppId = null);
/// <summary>
/// A depot owned by another app (e.g. Steamworks Common Redistributables 228980). Steam installs it into the owning app's
/// folder (steamapps/common/Steamworks Shared), not into this game's folder, so it is not part of this game's comparison.
/// </summary>
public record SharedDepot(uint Id, uint AppId);
public record Game(uint AppId, string Name, string Path, string BuildId, string Branch, string AcfPath, string AcfHash, List<InstalledDepot> Depots, bool Updating, List<SharedDepot>? SharedDepots = null);
public record OfficialFile(string Path, long Size, string Sha1, string Source, bool Supported = true);
public record DepotScope(uint Id, string ManifestId, string Name, uint? DlcId, bool Available, string? Error);
/// <summary>Source "local": manifests the Steam client keeps on this computer (no login). "online": downloaded from Steam.</summary>
public record Snapshot(List<OfficialFile> Files, List<DepotScope> Depots, bool Complete, List<string> Warnings, string Source = "online");
public record FileResult(string Path, string Status, long? LocalSize, long? OfficialSize, string? LocalHash, string? OfficialHash, string Source, string? Detail = null);
public record ScanProgress(string Stage, int Done, int Total, string Current);
/// <summary>Quick: compares file names and sizes only (no file contents read). Full: also compares each file's SHA-1.</summary>
public enum VerifyMode { Quick, Full }
public record ScanReport(uint AppId, string Game, string BuildId, string Branch, DateTimeOffset Started, bool ScopeComplete, List<DepotScope> Depots, List<string> Warnings, List<FileResult> Files, string Mode = "full", string Source = "online");
