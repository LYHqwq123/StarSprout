using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarSprout.Services;

/// <summary>Provider protocols, without hard-coded model inventories or credentials in URLs.</summary>
internal static class AnalysisProviderProtocol
{
    internal static readonly IReadOnlyDictionary<string, string> Presets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["openai"] = "https://api.openai.com/v1", ["deepseek"] = "https://api.deepseek.com/v1",
        ["qwen"] = "https://dashscope.aliyuncs.com/compatible-mode/v1", ["moonshot"] = "https://api.moonshot.cn/v1",
        ["zhipu"] = "https://open.bigmodel.cn/api/paas/v4", ["doubao"] = "https://ark.cn-beijing.volces.com/api/v3",
        ["siliconflow"] = "https://api.siliconflow.cn/v1", ["ollama"] = "http://localhost:11434/v1",
        ["anthropic"] = "https://api.anthropic.com/v1", ["gemini"] = "https://generativelanguage.googleapis.com/v1beta",
        ["custom"] = ""
    };

    internal static string NormalizeProvider(string? provider)
    {
        var result = (provider ?? "custom").Trim().ToLowerInvariant();
        if (!Presets.ContainsKey(result)) throw new ArgumentException("未知 AI 厂商，请选择自定义兼容接口。");
        return result;
    }

    internal static Uri BaseEndpoint(Uri endpoint, string provider)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/messages" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) path = path[..^suffix.Length];
        var geminiModel = path.IndexOf("/models/", StringComparison.Ordinal);
        if (provider == "gemini" && geminiModel >= 0) path = path[..geminiModel];
        if (path.Length == 0) path = provider == "gemini" ? "/v1beta" : "/v1";
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/'));
    }

    internal static HttpRequestMessage CreateAnalysis(Uri endpoint, string provider, string model, string key, string instruction, string snapshot)
    {
        var baseUrl = BaseEndpoint(endpoint, provider).AbsoluteUri.TrimEnd('/');
        JObject body;
        string url;
        if (provider == "anthropic")
        {
            url = baseUrl + "/messages";
            body = new JObject
            {
                ["model"] = model, ["system"] = instruction, ["max_tokens"] = 1800,
                ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = snapshot })
            };
        }
        else if (provider == "gemini")
        {
            if (model.StartsWith("models/", StringComparison.Ordinal)) model = model[7..];
            // Model ids are a path segment, never a query or a replacement host.
            if (model.Length == 0 || model.Contains('/') || model.Contains(':')) throw new ArgumentException("Gemini 模型名称无效，请从模型列表选择或填写模型 ID。");
            url = baseUrl + "/models/" + Uri.EscapeDataString(model) + ":generateContent";
            body = new JObject
            {
                ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = instruction }) },
                ["contents"] = new JArray(new JObject { ["role"] = "user", ["parts"] = new JArray(new JObject { ["text"] = snapshot }) }),
                ["generationConfig"] = new JObject { ["maxOutputTokens"] = 1800 }
            };
        }
        else
        {
            url = baseUrl + "/chat/completions";
            body = new JObject
            {
                ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = instruction },
                    new JObject { ["role"] = "user", ["content"] = snapshot }), ["stream"] = false
            };
            if (model.Length > 0) body["model"] = model;
            // Leave unsupported sampling/output parameters out of the common protocol.
        }
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json") };
        AddAuthentication(request, provider, key);
        return request;
    }

    internal static HttpRequestMessage CreateModels(Uri endpoint, string provider, string key, string? cursor = null)
    {
        var url = BaseEndpoint(endpoint, provider).AbsoluteUri.TrimEnd('/') + "/models";
        if (provider == "gemini") url += "?pageSize=1000" + (string.IsNullOrEmpty(cursor) ? "" : "&pageToken=" + Uri.EscapeDataString(cursor));
        else if (provider == "anthropic") url += "?limit=1000" + (string.IsNullOrEmpty(cursor) ? "" : "&after_id=" + Uri.EscapeDataString(cursor));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuthentication(request, provider, key);
        return request;
    }

    private static void AddAuthentication(HttpRequestMessage request, string provider, string key)
    {
        if (provider == "anthropic")
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            if (key.Length > 0) request.Headers.Add("x-api-key", key);
        }
        else if (provider == "gemini")
        { if (key.Length > 0) request.Headers.Add("x-goog-api-key", key); }
        else if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    internal static string ReadAnswer(JObject response, string provider)
    {
        string answer;
        if (provider == "anthropic")
            answer = string.Join("\n", (response["content"] as JArray ?? new JArray())
                .Where(x => (string?)x["type"] == "text" && x["text"]?.Type == JTokenType.String).Select(x => (string?)x["text"]));
        else if (provider == "gemini")
            answer = string.Join("\n", (response["candidates"]?[0]?["content"]?["parts"] as JArray ?? new JArray())
                .Where(x => x["text"]?.Type == JTokenType.String && (bool?)x["thought"] != true).Select(x => (string?)x["text"]));
        else
        {
            var content = response["choices"]?[0]?["message"]?["content"];
            if (content?.Type != JTokenType.String) throw new JsonException();
            answer = (string)content!;
        }
        if (string.IsNullOrWhiteSpace(answer)) throw new JsonException();
        return answer.Trim();
    }

    internal static (List<AnalysisModelView> Models, string? Cursor) ReadModels(JObject response, string provider)
    {
        var list = provider == "gemini" ? response["models"] as JArray : response["data"] as JArray;
        if (list == null) throw new JsonException();
        var models = new List<AnalysisModelView>();
        foreach (var item in list.Take(1000))
        {
            if (provider == "gemini" && !(item["supportedGenerationMethods"] as JArray ?? new JArray()).Any(x => (string?)x == "generateContent")) continue;
            var id = provider == "gemini" ? (string?)item["name"] : (string?)item["id"];
            if (id?.StartsWith("models/", StringComparison.Ordinal) == true && provider == "gemini") id = id[7..];
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) continue;
            models.Add(new AnalysisModelView { Id = id, Name = (string?)item["displayName"] ?? (string?)item["display_name"] ?? id });
        }
        var cursor = provider == "gemini" ? (string?)response["nextPageToken"] :
            provider == "anthropic" && (bool?)response["has_more"] == true ? (string?)response["last_id"] : null;
        if (cursor?.Length > 2048) throw new JsonException();
        return (models, cursor);
    }
}
