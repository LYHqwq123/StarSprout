namespace StarSprout.Services;

public sealed class AnalysisSettingsInput
{
    // Null updates the active connection; an empty string creates a separate connection.
    public string? ProfileId { get; set; }
    public string? Name { get; set; }
    public string? Provider { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }
    public bool SendLog { get; set; } = true;
    public bool SendModList { get; set; } = true;
    public bool AllowLocalHttp { get; set; }
    public bool CompactLog { get; set; } = true;
    public int MaxInputChars { get; set; } = 12000;
    public string LogKeywords { get; set; } = AnalysisSettingsView.DefaultLogKeywords;
}

public sealed class AnalysisSettingsView
{
    public const string DefaultLogKeywords = "ERROR,WARN,missing,requires,incompatible,outdated,duplicate,couldn't load,exception,Content Patcher,patch conflict";
    public string ActiveProfileId { get; set; } = "";
    public string Provider { get; set; } = "custom";
    public List<AnalysisProfileView> Profiles { get; set; } = new();
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public bool HasApiKey { get; set; }
    public bool SendLog { get; set; }
    public bool SendModList { get; set; }
    public bool AllowLocalHttp { get; set; }
    public bool CompactLog { get; set; } = true;
    public int MaxInputChars { get; set; } = 12000;
    public string LogKeywords { get; set; } = DefaultLogKeywords;
    public List<string> Warnings { get; set; } = new();
}

public sealed class AnalysisProfileView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "custom";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public bool HasApiKey { get; set; }
    public bool AllowLocalHttp { get; set; }
}

public sealed class AnalysisModelView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AnalysisPreview
{
    public string RedactedText { get; set; } = "";
    public int CharCount { get; set; }
    public int ModsCount { get; set; }
    public int OriginalCharCount { get; set; }
    public int SelectedCharCount { get; set; }
    // Approximation only; model tokenizers differ and no token-count API is called.
    public int EstimatedTokens { get; set; }
    public List<string> MatchedKeywords { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> LocalFindings { get; set; } = new();
}

public sealed class AnalysisResult
{
    public string Text { get; set; } = "";
    public List<string> LocalFindings { get; set; } = new();
    public DateTimeOffset GeneratedAt { get; set; }
    public List<string> Warnings { get; set; } = new();
}
