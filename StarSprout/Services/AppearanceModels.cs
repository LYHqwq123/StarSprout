using Newtonsoft.Json;

namespace StarSprout.Services;

public class AppearanceSettingsInput
{
    [JsonProperty("theme")] public string Theme { get; set; } = "light";
    [JsonProperty("accent")] public string Accent { get; set; } = "#5488ee";
    [JsonProperty("blur")] public int Blur { get; set; } = 12;
    [JsonProperty("opacity")] public int Opacity { get; set; } = 85;
    [JsonProperty("fit")] public string Fit { get; set; } = "cover";
    [JsonProperty("overlay")] public bool Overlay { get; set; } = true;
    [JsonProperty("panelOpacity")] public int PanelOpacity { get; set; } = 92;
}

public sealed class AppearanceSettingsView
{
    [JsonProperty("theme")] public string Theme { get; set; } = "light";
    [JsonProperty("accent")] public string Accent { get; set; } = "#5488ee";
    [JsonProperty("backgroundUrl")] public string BackgroundUrl { get; set; } = "";
    [JsonProperty("backgroundName")] public string BackgroundName { get; set; } = "";
    [JsonProperty("hasBackground")] public bool HasBackground { get; set; }
    [JsonProperty("blur")] public int Blur { get; set; } = 12;
    [JsonProperty("opacity")] public int Opacity { get; set; } = 85;
    [JsonProperty("fit")] public string Fit { get; set; } = "cover";
    [JsonProperty("overlay")] public bool Overlay { get; set; } = true;
    [JsonProperty("panelOpacity")] public int PanelOpacity { get; set; } = 92;
}
