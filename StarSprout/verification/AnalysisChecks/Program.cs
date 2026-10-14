using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using StarSprout.Services;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}
async Task ExpectFailure(Func<Task> action, string message, string label)
{
    try { await action(); throw new Exception("FAIL: " + label + " did not fail"); }
    catch (Exception ex) when (ex.Message.Contains(message, StringComparison.Ordinal)) { Check(true, label); }
}

// A dedicated fixture within this verification directory, with no environment keys or live network.
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
var fixture = Path.Combine(workspace, "fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var game = Path.Combine(fixture, "OfflineGame");
var saves = Path.Combine(fixture, "PlayerData", "Saves");
var logs = Path.Combine(fixture, "PlayerData", "ErrorLogs");
Directory.CreateDirectory(game);
Directory.CreateDirectory(saves);
Directory.CreateDirectory(logs);
File.WriteAllText(Path.Combine(game, "Stardew Valley.exe"), "not executable: fixture only");
var modDirectory = Path.Combine(game, "Mods", "ExamplePack");
Directory.CreateDirectory(modDirectory);
File.WriteAllText(Path.Combine(modDirectory, "manifest.json"), """
{"Name":"Example Pack","UniqueID":"Example.AuthorPack","Author":"Example","Description":"fixture","Version":"1.2.3.4","ContentPackFor":{"UniqueID":"Missing.Framework"}}
""");
var invalid = Path.Combine(game, "Mods", "Broken");
Directory.CreateDirectory(invalid);
File.WriteAllText(Path.Combine(invalid, "manifest.json"), "{bad json");
var disabled = Path.Combine(game, "Mods", ".DisabledFramework");
Directory.CreateDirectory(disabled);
File.WriteAllText(Path.Combine(disabled, "fake.dll"), "fixture");
File.WriteAllText(Path.Combine(disabled, "manifest.json"), """
{"Name":"Disabled Framework","UniqueID":"Example.DisabledFramework","Author":"Example","Description":"fixture","Version":"1.0.0","EntryDll":"fake.dll"}
""");
var disabledPack = Path.Combine(game, "Mods", "DisabledPack");
Directory.CreateDirectory(disabledPack);
File.WriteAllText(Path.Combine(disabledPack, "manifest.json"), """
{"Name":"Disabled Pack","UniqueID":"Example.DisabledPack","Author":"Example","Description":"fixture","Version":"1.0.0","ContentPackFor":{"UniqueID":"Example.DisabledFramework"}}
""");
const string fakeKey = "sk-TEST_ONLY_NOT_A_REAL_API_KEY_12345678";
File.WriteAllText(Path.Combine(logs, "SMAPI-latest.txt"), $"""
[12:45:59 INFO SMAPI] Stardew Valley 1.6.15; SMAPI 4.1.0
[12:45:59 WARN Example.AuthorPack] Missing.Framework not installed; game path {game}
[12:45:59 ERROR SMAPI] API key: {fakeKey}; token=private-token-value; email=fixture@example.test; host=192.168.1.10
[12:45:59 CHAT game] Private player conversation
[12:46:00 INFO SMAPI] Version 1.2.3.4; C:\Users\Somebody\Downloads\Game; IP 2001:db8::1
""", Encoding.UTF8);
var stardew = new StardewService(Path.Combine(fixture, "LauncherSettings"), saves);
stardew.SetGameDirectory(game);
var handler = new MockHandler();
using var client = new HttpClient(handler);
using var service = new LogAnalysisService(Path.Combine(fixture, "AI"), stardew, client);
Check(handler.Calls == 0, "constructor never calls network");
var initial = service.GetPreview();
Check(handler.Calls == 0 && initial.ModsCount == 4, "preview is local and includes mods");
Check(initial.LocalFindings.Any(f => f.Contains("Missing.Framework")) && initial.LocalFindings.Any(f => f.Contains("清单无效")), "deterministic missing-dependency and invalid-manifest findings");
Check(initial.LocalFindings.Any(f => f.Contains("依赖已停用：Example.DisabledFramework")), "deterministic disabled-dependency finding");
Check(initial.RedactedText.Contains("Example.AuthorPack") && initial.RedactedText.Contains("1.2.3.4"), "UniqueID and four-part versions preserved");
Check(initial.RedactedText.Contains("12:45:59"), "timestamps preserved");
Check(!initial.RedactedText.Contains(game, StringComparison.OrdinalIgnoreCase) && !initial.RedactedText.Contains("Somebody"), "game path and arbitrary Windows username redacted");
Check(!initial.RedactedText.Contains("fixture@example.test") && !initial.RedactedText.Contains("192.168.1.10") && !initial.RedactedText.Contains("2001:db8::1"), "email IPv4 and IPv6 redacted");
Check(!initial.RedactedText.Contains(fakeKey) && !initial.RedactedText.Contains("private-token-value"), "named credentials redacted");
Check(!initial.RedactedText.Contains("Private player conversation") && initial.RedactedText.Contains("Missing.Framework not installed"), "chat redacted and diagnostic lines retained");
await ExpectFailure(() => service.AnalyzeAsync(), "请先配置", "unconfigured analysis fails locally");
Check(handler.Calls == 0, "failed configuration never calls network");
await ExpectFailure(() => Task.Run(() => service.SaveSettings(new() { Endpoint = "http://api.example.test/v1" })), "HTTPS", "remote HTTP rejected");
await ExpectFailure(() => Task.Run(() => service.SaveSettings(new() { Endpoint = "https://api.example.test/v1?key=secret" })), "查询参数", "endpoint query credentials rejected");
await ExpectFailure(() => Task.Run(() => service.SaveSettings(new() { Endpoint = "https://user:secret@api.example.test/v1" })), "账号", "endpoint user info rejected");
await ExpectFailure(() => Task.Run(() => service.SaveSettings(new() { Endpoint = "http://localhost:1234/v1" })), "勾选", "localhost HTTP requires explicit opt-in");
var saved = service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", ApiKey = fakeKey });
Check(saved.HasApiKey && handler.Calls == 0, "saving settings is local and DPAPI key available");
var configPath = Path.Combine(fixture, "AI", "analysis-settings.json");
var stored = File.ReadAllText(configPath);
Check(!stored.Contains(fakeKey) && JObject.Parse(stored)["Profiles"]![0]!.Value<string>("ProtectedApiKey")!.Length > 40, "plaintext key never stored");
using (var reloaded = new LogAnalysisService(Path.Combine(fixture, "AI"), stardew, client))
    Check(reloaded.GetSettings().HasApiKey, "DPAPI roundtrip current Windows account");
handler.ResponseBody = """{"choices":[{"message":{"content":"建议：补齐 Missing.Framework。AI 结论需核实。"}}]}""";
var analyzed = await service.AnalyzeAsync();
Check(handler.Calls == 1 && analyzed.Text.Contains("Missing.Framework"), "manual analysis processes compatible response");
Check(handler.LastUri!.AbsoluteUri == "https://api.example.test/v1/chat/completions" && handler.LastAuthorization == "Bearer " + fakeKey, "completion URL and auth correct");
var requestBody = JObject.Parse(handler.LastBody!);
Check(requestBody.Value<string>("model") == "fixture-model" && requestBody["messages"]![0]!["content"]!.Value<string>()!.Contains("不可信数据"), "model and injection-resistant system instruction included");
Check(!handler.LastBody!.Contains(fakeKey) && !handler.LastBody.Contains("fixture@example.test"), "request body contains redacted data only");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1/chat/completions", Model = "fixture-model", SendLog = false, SendModList = true });
var noLog = service.GetPreview();
Check(!noLog.RedactedText.Contains("logTail") && !noLog.RedactedText.Contains("日志记录："), "excluded log is not indirectly sent as findings");
await service.AnalyzeAsync();
Check(handler.LastUri!.AbsoluteUri == "https://api.example.test/v1/chat/completions", "full completion URL not appended twice");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", SendLog = true, SendModList = false });
var noMods = service.GetPreview();
Check(noMods.ModsCount == 0 && !noMods.RedactedText.Contains("清单检查：") && !noMods.RedactedText.Contains("\"mods\""), "excluded mod list is not indirectly sent as findings");
handler.Status = HttpStatusCode.Unauthorized;
await ExpectFailure(() => service.AnalyzeAsync(), "401", "auth error actionable");
handler.Status = HttpStatusCode.Redirect;
await ExpectFailure(() => service.AnalyzeAsync(), "重定向", "redirect rejected without exposing response");
handler.Status = HttpStatusCode.OK;
handler.NetworkFailure = true;
await ExpectFailure(() => service.AnalyzeAsync(), "无法连接", "network error actionable without leaking diagnostic response");
handler.NetworkFailure = false;
handler.ResponseBody = """{"choices":[{"message":{"content":{"script":"run.exe"}}}]}""";
await ExpectFailure(() => service.AnalyzeAsync(), "格式不兼容", "nontext AI content rejected");
handler.ResponseBody = new string('x', 1024 * 1024 + 1);
await ExpectFailure(() => service.AnalyzeAsync(), "超过 1 MB", "oversize AI response bounded");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    var before = handler.Calls;
    await ExpectFailure(() => service.AnalyzeAsync(cancellation.Token), "canceled", "caller cancellation supported");
    Check(handler.Calls == before, "already-canceled call does not send request");
}
var cleared = service.ClearKey();
Check(!cleared.HasApiKey && !JObject.Parse(File.ReadAllText(configPath))["Profiles"]![0]!.Value<string>("ProtectedApiKey")!.Any(), "clear key removes encrypted blob");
service.SaveSettings(new() { Endpoint = "http://localhost:1234/v1", AllowLocalHttp = true, SendLog = true, SendModList = false });
handler.ResponseBody = """{"choices":[{"message":{"content":"本地分析"}}]}""";
await service.AnalyzeAsync();
Check(handler.LastAuthorization is null && !JObject.Parse(handler.LastBody!).ContainsKey("model"), "explicit local service accepts omitted key and model");
File.WriteAllText(Path.Combine(logs, "SMAPI-latest.txt"), new string('x', 80 * 1024) + "\n[ERROR SMAPI] tail issue\n");
Check(service.GetPreview().CharCount <= 12000 && service.GetPreview().Warnings.Any(w => w.Contains("按严重度")), "log summary bounded with selection warning");
File.WriteAllText(configPath, "{broken");
using (var broken = new LogAnalysisService(Path.Combine(fixture, "AI"), stardew, client))
{
    Check(broken.GetSettings().Warnings.Any(w => w.Contains("损坏")) && File.ReadAllText(configPath) == "{broken", "corrupt configuration recoverable without changing original");
    broken.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", ApiKey = fakeKey });
    Check(broken.GetSettings().Warnings.Count == 0, "saving repairs corrupt configuration");
}
// Legacy migration keeps the DPAPI blob while the user's original file is untouched until save.
var legacyDirectory = Path.Combine(fixture, "LegacyAI");
Directory.CreateDirectory(legacyDirectory);
var encryptedKey = JObject.Parse(stored)["Profiles"]![0]!.Value<string>("ProtectedApiKey")!;
var legacyText = new JObject { ["Endpoint"] = "https://api.example.test/v1", ["Model"] = "legacy-model", ["ProtectedApiKey"] = encryptedKey, ["SendLog"] = false }.ToString();
File.WriteAllText(Path.Combine(legacyDirectory, "analysis-settings.json"), legacyText);
using (var legacy = new LogAnalysisService(legacyDirectory, stardew, client))
{
    Check(legacy.GetSettings().Profiles.Count == 1 && legacy.GetSettings().HasApiKey && legacy.GetSettings().Model == "legacy-model", "legacy settings migrate into one encrypted connection");
    Check(!legacy.GetSettings().SendLog && File.ReadAllText(Path.Combine(legacyDirectory, "analysis-settings.json")) == legacyText, "migration preserves privacy flag and original file until save");
}

// Independent vendor connections, model switching, native protocol payloads, discovery.
var custom = service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", ApiKey = fakeKey, Name = "自定义" });
var customId = custom.ActiveProfileId;
const string vendorKey = "TEST_ONLY_VENDOR_KEY_456789";
var anthropic = service.SaveSettings(new() { ProfileId = "", Provider = "anthropic", Name = "Claude", Model = "fixture-claude", ApiKey = vendorKey });
Check(anthropic.Profiles.Count == 2 && anthropic.Endpoint == "https://api.anthropic.com/v1" && anthropic.HasApiKey, "new vendor connection uses verified preset with independent key");
var anthropicId = anthropic.ActiveProfileId;
handler.ResponseBody = """{"content":[{"type":"thinking","thinking":"private chain"},{"type":"text","text":"Claude诊断结果"}]}""";
var claudeAnswer = await service.AnalyzeAsync();
Check(claudeAnswer.Text == "Claude诊断结果" && handler.LastUri!.AbsoluteUri == "https://api.anthropic.com/v1/messages", "Anthropic native messages response and URL");
Check(handler.LastAuthorization == null && handler.LastHeaders["x-api-key"] == vendorKey && handler.LastHeaders["anthropic-version"] == "2023-06-01", "Anthropic native authentication headers");
var nativeBody = JObject.Parse(handler.LastBody!);
Check(nativeBody["system"]!.Type == Newtonsoft.Json.Linq.JTokenType.String && (int)nativeBody["max_tokens"]! == 1800 && nativeBody["messages"]!.Count() == 1, "Anthropic native request separates system prompt and caps output");
Check(!handler.LastBody!.Contains(vendorKey) && !claudeAnswer.Text.Contains("private chain"), "native request keeps key out of body and ignores reasoning content");
handler.ResponseBody = """{"data":[{"id":"fixture-claude-a","display_name":"Claude A"},{"id":"fixture-claude-b","display_name":"Claude B"}],"has_more":false}""";
var claudeModels = await service.GetModelsAsync();
Check(claudeModels.Count == 2 && claudeModels[0].Name == "Claude A" && handler.LastUri!.AbsolutePath == "/v1/models", "Anthropic model discovery uses live inventory");
var gemini = service.SaveSettings(new() { ProfileId = "", Provider = "gemini", Model = "models/fixture-gemini", ApiKey = "TEST_ONLY_GEMINI_KEY_98765", Name = "Gemini" });
var geminiId = gemini.ActiveProfileId;
handler.ResponseBody = """{"candidates":[{"content":{"parts":[{"text":"hidden reasoning","thought":true},{"text":"Gemini诊断结果"}]}}]}""";
var geminiAnswer = await service.AnalyzeAsync();
Check(geminiAnswer.Text == "Gemini诊断结果" && handler.LastUri!.AbsoluteUri == "https://generativelanguage.googleapis.com/v1beta/models/fixture-gemini:generateContent", "Gemini native generateContent and model prefix normalization");
Check(handler.LastHeaders.ContainsKey("x-goog-api-key") && handler.LastUri!.Query.Length == 0 && handler.LastAuthorization == null, "Gemini key only in header never URL");
var geminiBody = JObject.Parse(handler.LastBody!);
Check(geminiBody.ContainsKey("systemInstruction") && geminiBody["contents"]![0]!["parts"]![0]!["text"]!.Type == Newtonsoft.Json.Linq.JTokenType.String, "Gemini native system and content payload");
handler.ResponseBody = """{"models":[{"name":"models/fixture-text","displayName":"Text model","supportedGenerationMethods":["generateContent"]},{"name":"models/fixture-embedding","supportedGenerationMethods":["embedContent"]}]}""";
var geminiModels = await service.GetModelsAsync();
Check(geminiModels.Count == 1 && geminiModels[0].Id == "fixture-text" && handler.LastUri!.Query == "?pageSize=1000", "Gemini inventory filters generation models and removes models prefix");
service.SelectProfile(customId);
handler.ResponseBody = """{"choices":[{"message":{"content":"再次分析"}}]}""";
await service.AnalyzeAsync();
Check(handler.LastAuthorization == "Bearer " + fakeKey && JObject.Parse(handler.LastBody!).Value<string>("model") == "fixture-model", "switching profile restores original key and model without repasting");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model-2" });
await service.AnalyzeAsync();
Check(handler.LastAuthorization == "Bearer " + fakeKey && JObject.Parse(handler.LastBody!).Value<string>("model") == "fixture-model-2", "changing model keeps connection key");
handler.ResponseBody = """{"data":[{"id":"model-new-a"},{"id":"model-new-b"},{"id":"model-new-a"}]}""";
var compatibleModels = await service.GetModelsAsync();
Check(compatibleModels.Count == 2 && handler.LastUri!.AbsoluteUri == "https://api.example.test/v1/models", "compatible model discovery deduplicates server inventory");
handler.Status = HttpStatusCode.NotFound;
await ExpectFailure(() => service.GetModelsAsync(), "手填", "unsupported model discovery permits actionable manual input");
handler.Status = HttpStatusCode.OK;
var profileJson = File.ReadAllText(configPath);
Check(!profileJson.Contains(fakeKey) && !profileJson.Contains(vendorKey) && JObject.Parse(profileJson)["Profiles"]!.Count() == 3, "all vendor keys independently encrypted on disk");
Check(!JObject.FromObject(service.GetSettings()).ToString().Contains("ProtectedApiKey"), "settings never return encrypted or clear keys to frontend");
using (var roundtrip = new LogAnalysisService(Path.Combine(fixture, "AI"), stardew, client))
{
    Check(roundtrip.GetSettings().Profiles.Count == 3 && roundtrip.SelectProfile(anthropicId).HasApiKey && roundtrip.SelectProfile(customId).Model == "fixture-model-2", "multiple connections survive reload and switch");
}
service.SelectProfile(geminiId);
service.ClearKey();
Check(!service.GetSettings().HasApiKey && service.SelectProfile(customId).HasApiKey && service.SelectProfile(anthropicId).HasApiKey, "clearing one vendor key leaves other vendor keys intact");
service.DeleteProfile(geminiId);
Check(service.GetSettings().Profiles.Count == 2 && !service.GetSettings().Profiles.Any(p => p.Id == geminiId), "deleting connection removes only its configuration");
var noInherited = service.SaveSettings(new() { ProfileId = "", Provider = "deepseek", Model = "fixture-deepseek" });
Check(!noInherited.HasApiKey && noInherited.Endpoint == "https://api.deepseek.com/v1", "new connection does not inherit active vendor credential");
service.DeleteProfile(noInherited.ActiveProfileId);
service.SelectProfile(customId);
var changedVendor = service.SaveSettings(new() { Provider = "siliconflow", Model = "fixture-model" });
Check(!changedVendor.HasApiKey, "changing vendor in existing connection clears old vendor key");
service.SaveSettings(new() { Provider = "custom", Endpoint = "https://api.example.test/v1", Model = "fixture-model", ApiKey = fakeKey });

// An endpoint change cannot reuse a secret for a different origin, while the same origin can change path.
service.SaveSettings(new() { Endpoint = "https://api.example.test/v2", Model = "fixture-model" });
Check(service.GetSettings().HasApiKey, "same-origin API base path update preserves key");
service.SaveSettings(new() { Endpoint = "https://different.example.test/v1", Model = "fixture-model" });
Check(!service.GetSettings().HasApiKey, "different API origin clears saved key when key field empty");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", ApiKey = fakeKey });
var localProfile = service.SaveSettings(new() { ProfileId = "", Provider = "ollama", Model = "fixture-local", AllowLocalHttp = true });
var localId = localProfile.ActiveProfileId;
service.SelectProfile(customId);
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", AllowLocalHttp = false });
using (var localRoundtrip = new LogAnalysisService(Path.Combine(fixture, "AI"), stardew, client))
{
    Check(localRoundtrip.GetSettings().Profiles.Count == 3 && !localRoundtrip.GetSettings().AllowLocalHttp, "remote save does not invalidate stored local connection on reload");
    Check(localRoundtrip.SelectProfile(localId).AllowLocalHttp && !localRoundtrip.SelectProfile(customId).AllowLocalHttp, "local HTTP permission belongs to individual connection");
}
service.DeleteProfile(localId);

// A severe early error survives a long tail, repeated warnings collapse, normal mods are counted only.
var diagnosticLog = new StringBuilder("[00:00:01 INFO SMAPI] Stardew Valley 1.6.15; SMAPI 4.1.0\n[00:00:02 ERROR Example.AuthorPack] EARLY_CRITICAL: missing Missing.Framework\n");
for (var i = 0; i < 9000; i++) diagnosticLog.AppendLine($"[00:01:00 INFO game] normal frame {i}");
for (var i = 0; i < 100; i++) diagnosticLog.AppendLine($"[00:02:{i % 60:00} WARN Example.AuthorPack] DUPLICATE_WARNING: incompatible patch");
diagnosticLog.AppendLine("[00:03:00 INFO custom] MY_DIAGNOSTIC_KEYWORD user supplied hint");
File.WriteAllText(Path.Combine(logs, "SMAPI-latest.txt"), diagnosticLog.ToString());
var normalMod = Path.Combine(game, "Mods", "NormalMod");
Directory.CreateDirectory(normalMod);
File.WriteAllText(Path.Combine(normalMod, "fake.dll"), "fixture");
File.WriteAllText(Path.Combine(normalMod, "manifest.json"), """{"Name":"Normal Mod","UniqueID":"Example.Normal","Author":"Example","Description":"fixture","Version":"1.0.0","EntryDll":"fake.dll"}""");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", MaxInputChars = 4000, LogKeywords = "MY_DIAGNOSTIC_KEYWORD" });
var compactPreview = service.GetPreview();
Check(compactPreview.RedactedText.Contains("EARLY_CRITICAL") && compactPreview.RedactedText.Contains("Missing.Framework"), "severe early error retained beyond UI log tail");
Check(compactPreview.CharCount <= 4000 && compactPreview.OriginalCharCount > 300000 && compactPreview.SelectedCharCount < 2500, "whole snapshot respects configurable input budget");
Check(compactPreview.RedactedText.Contains("重复 100 次") && compactPreview.RedactedText.Split("DUPLICATE_WARNING").Length <= 4, "timestamp-changing repeated warning collapsed with count");
Check(compactPreview.MatchedKeywords.Contains("MY_DIAGNOSTIC_KEYWORD") && compactPreview.EstimatedTokens > 0 && compactPreview.Warnings.Any(w => w.Contains("估计")), "custom keyword and explicit local token estimate exposed");
var compactJson = JObject.Parse(compactPreview.RedactedText);
Check(compactJson["modCounts"]!.Value<int>("total") == 5 && !compactPreview.RedactedText.Contains("Example.Normal"), "unrelated normal mods omitted and total count retained");
Check(compactPreview.RedactedText.Contains("Example.DisabledFramework"), "relevant disabled dependency included in compact snapshot");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", SendLog = false, SendModList = true });
Check(service.GetPreview().OriginalCharCount == 0 && !service.GetPreview().RedactedText.Contains("EARLY_CRITICAL"), "privacy log opt-out prevents keyword data indirect upload");
service.SaveSettings(new() { Endpoint = "https://api.example.test/v1", Model = "fixture-model", SendLog = true, SendModList = false });
Check(service.GetPreview().ModsCount == 0 && !service.GetPreview().RedactedText.Contains("modCounts"), "privacy mod opt-out prevents metadata indirect upload");
await ExpectFailure(() => Task.Run(() => service.SaveSettings(new() { MaxInputChars = 35000 })), "4000", "input budget outside supported range rejected");
await ExpectFailure(() => Task.Run(() => service.SaveSettings(new() { Provider = "unknown" })), "未知", "unknown provider rejected");
Console.WriteLine($"{checks} analysis checks passed; no live AI requests or game launches.");
// Resolve and check the target before deleting only this generated fixture.
if (!Path.GetFullPath(fixture).StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup");
Directory.Delete(fixture, true);

sealed class MockHandler : HttpMessageHandler
{
    public int Calls;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string ResponseBody = "{}";
    public string? LastBody;
    public Uri? LastUri;
    public string? LastAuthorization;
    public bool NetworkFailure;
    public Dictionary<string, string> LastHeaders = new(StringComparer.OrdinalIgnoreCase);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastUri = request.RequestUri;
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        LastHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (NetworkFailure) throw new HttpRequestException("fixture network failure");
        return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json") };
    }
}
