using Newtonsoft.Json;

namespace StarSprout.Services;

public sealed class LaunchProfileState
{
    [JsonProperty("platform")] public string Platform { get; init; } = "offline";
    [JsonProperty("displayName")] public string DisplayName { get; init; } = "离线玩家";
    [JsonProperty("description")] public string Description { get; init; } = "";
    [JsonProperty("canOpenClient")] public bool CanOpenClient { get; init; }
}

public sealed class LaunchProfileDescriptor
{
    [JsonProperty("id")] public string Id { get; init; } = "";
    [JsonProperty("name")] public string Name { get; init; } = "";
    [JsonProperty("description")] public string Description { get; init; } = "";
}
