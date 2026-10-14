using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

string root = Path.Combine(AppContext.BaseDirectory, "launch-profile-check-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
void Fails(Action action, string name, string? message = null)
{
    try { action(); }
    catch (Exception ex) when (message == null || ex.Message.Contains(message, StringComparison.Ordinal)) { passed++; return; }
    throw new Exception("FAIL: " + name);
}
string Folder(string name) => Path.Combine(root, name);
bool AllClients(string _) => true;
try
{
    var profiles = new LaunchProfileService(Folder("basic"), protocolAvailable: AllClients);
    Check(profiles.GetState().Platform == "offline", "default is local standalone profile");
    Check(!profiles.GetState().CanOpenClient, "offline profile never claims platform client");
    Check(profiles.GetState().DisplayName == "离线玩家", "offline display name");
    Check(profiles.GetProfiles().Select(profile => profile.Id).SequenceEqual(new[] { "steam", "xbox", "offline" }), "all three supported platform options");
    Check(profiles.GetProfiles().All(profile => profile.Name.Length > 0 && profile.Description.Length > 0), "options include readable names and descriptions");

    var steam = profiles.SetPlatform(" STEAM ");
    Check(steam.Platform == "steam" && steam.CanOpenClient, "selection normalized and registered client detected");
    Check(steam.DisplayName == "Steam 当前账号" && steam.Description.Contains("客户端"), "uses official client account without claiming app login");
    var file = Path.Combine(Folder("basic"), "launch-profile.json");
    var stored = JObject.Parse(File.ReadAllText(file));
    Check(stored.Properties().Select(property => property.Name).SequenceEqual(new[] { "platform" }), "selection stores only platform, no account tokens or passwords");
    Check(new LaunchProfileService(Folder("basic"), protocolAvailable: AllClients).GetState().Platform == "steam", "selection survives restart");
    Check(new LaunchProfileService(Folder("migrate"), "steam", AllClients).GetState().Platform == "steam", "root may preserve existing installation as initial platform");
    var xbox = profiles.SetPlatform("xbox");
    Check(xbox.DisplayName == "Xbox 当前账号" && xbox.CanOpenClient, "Xbox current account descriptor");
    profiles.SetPlatform("offline");
    Check(new LaunchProfileService(Folder("basic"), protocolAvailable: AllClients).GetState().Platform == "offline",
        "switching back to local play persists across restart");
    Check(!Directory.EnumerateFiles(Folder("basic"), "*.tmp").Any(), "atomic updates leave no temporary settings files");
    var snapshot = JObject.Parse(JsonConvert.SerializeObject(xbox));
    Check(snapshot.Value<string>("platform") == "xbox" && snapshot.Value<bool>("canOpenClient"), "state uses frontend camelcase contract");
    Check(JObject.Parse(JsonConvert.SerializeObject(profiles.GetProfiles()[0])).Value<string>("id") == "steam", "profile options use frontend camelcase contract");

    foreach (var invalid in new[] { "", "gog", "https://example.com", "steam://rungameid/0", "../steam", "steam;cmd.exe" })
    {
        Fails(() => profiles.SetPlatform(invalid), "invalid platform rejected: " + invalid);
        Check(profiles.GetState().Platform == "offline", "invalid selection does not change previous platform");
    }

    var noClients = new LaunchProfileService(Folder("no-clients"), "steam", _ => false);
    Check(!noClients.GetState().CanOpenClient, "missing Steam client detected");
    noClients.SetPlatform("xbox");
    Check(!noClients.GetState().CanOpenClient, "missing Xbox client detected");
    Fails(() => LaunchProfileService.GetClientStartInfo("steam", _ => false), "missing Steam launch refused", "未检测到");
    Fails(() => LaunchProfileService.GetClientStartInfo("xbox", _ => false), "missing Xbox launch refused", "未检测到");
    Fails(() => LaunchProfileService.GetClientStartInfo("offline", AllClients), "offline does not open client", "不需要");
    Fails(() => LaunchProfileService.GetClientStartInfo("file:///windows/notepad.exe", AllClients), "arbitrary shell input rejected");
    var steamPlan = LaunchProfileService.GetClientStartInfo("steam", AllClients);
    Check(steamPlan.FileName == "steam://open/main" && steamPlan.UseShellExecute && steamPlan.Arguments.Length == 0, "Steam client plan fixed safe URI");
    var xboxPlan = LaunchProfileService.GetClientStartInfo("xbox", name => name == "xbox");
    Check(xboxPlan.FileName == "xbox://" && xboxPlan.UseShellExecute && xboxPlan.Arguments.Length == 0, "Xbox client plan verified protocol");
    var xboxFallback = LaunchProfileService.GetClientStartInfo("xbox", name => name == "msxbox");
    Check(xboxFallback.FileName == "msxbox://", "registered Xbox alternate protocol supported");
    Check(LaunchProfileService.GetClientStartInfo("xbox", name => name == "msgamingapp").FileName == "msgamingapp://", "registered GamingApp alternate protocol supported");
    Check(!new LaunchProfileService(Folder("invalid-legacy-protocol"), "xbox", name => name == "ms-xbox").GetState().CanOpenClient,
        "unverified hyphenated Xbox protocol not used");
    var gamePlan = LaunchProfileService.GetSteamGameStartInfo();
    Check(gamePlan.FileName == "steam://rungameid/413150" && gamePlan.UseShellExecute && gamePlan.Arguments.Length == 0,
        "Steam game plan uses official Stardew application id");

    var malformedFolder = Folder("malformed");
    Directory.CreateDirectory(malformedFolder);
    string badFile = Path.Combine(malformedFolder, "launch-profile.json");
    foreach (var content in new[] { "{broken", "{\"platform\":\"invalid\"}", "{\"platform\":null}", new string('x', 8193) })
    {
        File.WriteAllText(badFile, content);
        Check(new LaunchProfileService(malformedFolder, "xbox", AllClients).GetState().Platform == "xbox", "corrupt or excessive selection uses provided migration default");
    }
    File.WriteAllText(badFile, "{\"platform\":\"offline\",\"extra\":{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":{}}}}}}}}}}}");
    Check(new LaunchProfileService(malformedFolder, "steam", AllClients).GetState().Platform == "steam", "settings parsing depth bounded");
    File.WriteAllText(badFile, "{\"platform\":\"steam\",\"unexpected\":\"ignored\"}");
    Check(new LaunchProfileService(malformedFolder, "offline", AllClients).GetState().Platform == "steam", "unknown benign metadata tolerated");

    profiles.SetPlatform("steam");
    File.SetAttributes(file, FileAttributes.ReadOnly);
    try
    {
        Fails(() => profiles.SetPlatform("xbox"), "failed durable update reported");
        Check(profiles.GetState().Platform == "steam", "failed durable update preserves current selection");
    }
    finally { File.SetAttributes(file, FileAttributes.Normal); }
    Check(new LaunchProfileService(Folder("basic"), protocolAvailable: AllClients).GetState().Platform == "steam", "failed update preserves on-disk selection");
    Check(!Directory.EnumerateFiles(Folder("basic"), "*.tmp").Any(), "failed update removes temporary file");

    Console.WriteLine($"LaunchProfileChecks: {passed} checks passed; no clients or games were launched.");
}
finally
{
    // This unique directory is owned by this check and resolves below the compiled test output.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}
