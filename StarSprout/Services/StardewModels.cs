using System;
using System.Collections.Generic;

namespace StarSprout.Services;

public sealed class StardewSettings
{
    public string GameDirectory { get; set; } = "";
    public bool UseSmapi { get; set; } = true;
}

public sealed class GameInstallation
{
    public string DirectoryPath { get; set; } = "";
    public bool HasGame { get; set; }
    public bool HasSmapi { get; set; }
    public string GameVersion { get; set; } = "未识别";
    public string SmapiVersion { get; set; } = "未安装";
}

public sealed class StardewMod
{
    public string Name { get; set; } = "";
    public string UniqueId { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public bool Enabled { get; set; }
    public string Status { get; set; } = "";
    internal bool Valid { get; set; }
    internal List<ModDependency> Dependencies { get; } = new();
}

internal sealed class ModDependency
{
    public string UniqueId { get; set; } = "";
    public string MinimumVersion { get; set; } = "";
    public bool IsRequired { get; set; } = true;
}

public sealed class StardewSave
{
    public string Name { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public DateTime LastModified { get; set; }
}

public sealed class ModInstallResult
{
    public List<StardewMod> Mods { get; set; } = new();
    public int InstalledCount { get; set; }
    public int UpdatedCount { get; set; }
    public int PatchedCount { get; set; }
    public int SkippedCount { get; set; }
    public string BackupDirectory { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
}

public sealed class ModRemovalResult
{
    public string Name { get; set; } = "";
    public string UniqueId { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public bool CleanupPending { get; set; }
}
