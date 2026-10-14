namespace StarSprout.Services;

public sealed class MultiplayerCoreState
{
    public bool Installed { get; init; }
    public string Version { get; init; } = MultiplayerCoreStore.Version;
    public string CorePath { get; init; } = "";
    public string CliPath { get; init; } = "";
    public string Message { get; init; } = "";
}
