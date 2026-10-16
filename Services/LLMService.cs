using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class LLMService 
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "叶片缺陷审核", "llm_config.json");

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private CancellationTokenSource? _cts;


    public ObservableCollection<LlmModelConfig> Models { get; } = [];

    public event Action? ModelsChanged;

    public LLMService()
    {
        LoadConfig();
    }

    public LlmModelConfig? Current =>
        Models.FirstOrDefault(m => ReferenceEquals(m, current)) ?? Models.FirstOrDefault();

    private LlmModelConfig? current;

    public void AddModel(LlmModelConfig config)
    {
        Models.Add(config);
        current ??= config;
        SaveConfig();
        ModelsChanged?.Invoke();
    }

    public void RemoveModel(LlmModelConfig config)
    {
        Models.Remove(config);
        if (ReferenceEquals(current, config)) current = Models.FirstOrDefault();
        SaveConfig();
        ModelsChanged?.Invoke();
    }

    public void SelectModel(LlmModelConfig config)
    {
        current = Models.Contains(config) ? config : throw new InvalidOperationException("模型不在列表中");
        SaveConfig();
        ModelsChanged?.Invoke();
    }

    public void Persist() => SaveConfig();

    public async Task<(bool Ok, string Message)> TestConnectionAsync(LlmModelConfig config, CancellationToken ct = default)
    {
        try
        {
            var content = await PostChatAsync(config,
                [LlmMessage.User("回复\"连接成功\"四个字")], systemPrompt: null, stream: false, ct);
            return (true, $"连接成功（模型返回：{Trim(content, 50)}）");
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }

    public async Task<string> SendAsync(IReadOnlyList<LlmMessage> messages, string? systemPrompt = null,
        LlmModelConfig? model = null, CancellationToken ct = default)
    {
        var cfg = model ?? Current ?? throw new InvalidOperationException("未配置可用模型，请先在【模型配置】中添加");
        return await PostChatAsync(cfg, messages, systemPrompt, stream: false, ct);
    }

    public async IAsyncEnumerable<string> SendStreamAsync(IReadOnlyList<LlmMessage> messages, string? systemPrompt = null,
        LlmModelConfig? model = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cfg = model ?? Current ?? throw new InvalidOperationException("未配置可用模型，请先在【模型配置】中添加");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var request = BuildRequest(cfg, messages, systemPrompt, stream: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(_cts.Token) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (string.IsNullOrEmpty(data)) continue;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                delta = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("delta")
                    .TryGetProperty("content", out var c) ? c.GetString() : null;
            }
            catch
            {
                // 忽略无法解析的心跳/注释块
            }

            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    // ─────────────── HTTP 底层 ───────────────

    private async Task<string> PostChatAsync(LlmModelConfig cfg, IReadOnlyList<LlmMessage> messages,
        string? systemPrompt, bool stream, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var request = BuildRequest(cfg, messages, systemPrompt, stream);

        // 大请求体（多模态图片 base64）+ 大响应时，缓冲式读取会出现
        // "Error while copying content to a stream"，改为响应头返回后流式读取全文
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token);

        await using var responseStream = await response.Content.ReadAsStreamAsync(_cts.Token);
        using var reader = new StreamReader(responseStream);
        var body = await reader.ReadToEndAsync(_cts.Token);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}：{Trim(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private HttpRequestMessage BuildRequest(LlmModelConfig cfg, IReadOnlyList<LlmMessage> messages,
        string? systemPrompt, bool stream)
    {
        var msgList = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgList.Add(new() { ["role"] = "system", ["content"] = systemPrompt });

        foreach (var m in messages)
        {
            // 单文本消息序列化为字符串，多模态消息序列化为 part 数组
            if (m.Parts.Count == 1 && m.Parts[0].Type == "text")
            {
                msgList.Add(new() { ["role"] = m.Role, ["content"] = m.Parts[0].Text ?? "" });
            }
            else
            {
                var parts = m.Parts.Select(p => p.Type == "text"
                    ? (object)new Dictionary<string, object?> { ["type"] = "text", ["text"] = p.Text ?? "" }
                    : new Dictionary<string, object?>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new Dictionary<string, object?> { ["url"] = p.ImageUrl },
                    }).ToList();
                msgList.Add(new() { ["role"] = m.Role, ["content"] = parts });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = cfg.ModelId,
            ["messages"] = msgList,
            ["temperature"] = cfg.Temperature,
            ["stream"] = stream,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, cfg.ApiUrl.TrimEnd('/') + "/chat/completions");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        return request;
    }

    // ─────────────── 配置持久化 ───────────────

    private class LlmConfigFile
    {
        public List<LlmModelConfig> Models { get; set; } = [];
        public string? CurrentName { get; set; }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var file = JsonSerializer.Deserialize<LlmConfigFile>(File.ReadAllText(ConfigPath));
            if (file == null) return;
            foreach (var m in file.Models) Models.Add(m);
            current = Models.FirstOrDefault(m => m.Name == file.CurrentName) ?? Models.FirstOrDefault();
        }
        catch
        {
            // 配置损坏时忽略，保持空列表
        }
    }

    private void SaveConfig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var file = new LlmConfigFile
        {
            Models = [.. Models],
            CurrentName = Current?.Name,
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Trim(string s, int len) => s.Length <= len ? s : s[..len] + "…";
}
