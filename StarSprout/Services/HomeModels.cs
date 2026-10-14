using Newtonsoft.Json;

namespace StarSprout.Services;

public sealed class HomePlayDay
{
    [JsonProperty("date")] public string Date { get; set; } = "";
    [JsonProperty("accent")] public string Accent { get; set; } = "#5488ee";
    [JsonProperty("count")] public int Count { get; set; }
}

public sealed class HomeState
{
    [JsonProperty("year")] public int Year { get; set; }
    [JsonProperty("month")] public int Month { get; set; }
    [JsonProperty("today")] public string Today { get; set; } = "";
    [JsonProperty("days")] public List<HomePlayDay> Days { get; set; } = new();
    [JsonProperty("hasImage")] public bool HasImage { get; set; }
    [JsonProperty("imageName")] public string ImageName { get; set; } = "";
    [JsonProperty("imageUrl")] public string ImageUrl { get; set; } = "";
}
