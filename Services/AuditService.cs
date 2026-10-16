using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class AuditService 
{
    private readonly LLMService _llm;
    private readonly DocumentExtractService _extractService;
    private readonly DocumentModifyService _modifyService;
    private readonly AppConfigService _config;

    public AuditService(LLMService llm, DocumentExtractService extractService,
        DocumentModifyService modifyService, AppConfigService config)
    {
        _llm = llm;
        _extractService = extractService;
        _modifyService = modifyService;
        _config = config;
    }

    public async Task<AuditOutcome> AuditAsync(string docxPath, StandardDocument? standard, string customPrompt,
        IProgress<string>? log = null, CancellationToken ct = default, IProgress<double>? percent = null)
    {
        void Log(string msg) => log?.Report(msg);

        Log($"开始 AI 文档审核：{Path.GetFileName(docxPath)}");

        // 1) 提取待审文档内容（文字+图片）
        var contentItems = await _extractService.ExtractContentAsync(docxPath);
        Log($"共提取 {contentItems.Count} 个内容项");
        percent?.Report(10);

        // 2) 组装审核 prompt（迁移自 AiAuditDemo.ModelService.AuditDocumentAsync）
        var prompt = string.IsNullOrWhiteSpace(customPrompt)
            ? "你是一个专业的文档审核助手，请仔细分析用户提供的文档内容，给出详细的审核意见。"
            : customPrompt;

        var domainFields = _config.DomainFields;
        var auditPrompt = new StringBuilder(prompt).AppendLine();

        if (domainFields.Count > 0)
        {
            auditPrompt.AppendLine();
            auditPrompt.AppendLine($"请重点检查以下领域字段是否完整、准确：{string.Join("、", domainFields)}。");
        }

        if (standard is { HasContent: true })
        {
            auditPrompt.AppendLine("请严格参照下面提供的【标准维修文档】内容，对【待审核文档】进行对比审核。");
        }

        auditPrompt.AppendLine("""

            请以 JSON 格式返回审核结果，格式如下：
            {
              "issues": [
                {
                  "originalText": "原文中有问题的具体文字",
                  "issue": "问题描述",
                  "suggestion": "修改建议"
                }
              ],
              "summary": "总体审核意见"
            }

            注意：
            1. originalText 必须是原文中完全一致的文字，不要修改或省略
            2. 如果原文没有问题，issues 可以为空数组
            3. 请用中文回答
            """);

        var parts = new List<LlmMessagePart> { LlmMessagePart.TextPart(auditPrompt.ToString()) };

        // 标准文档（文字 + 图片，加分隔标记）
        if (standard is { HasContent: true })
        {
            parts.Add(LlmMessagePart.TextPart("===== 以下是【标准维修文档】内容（供参考对比）====="));
            var stdText = standard.ContentItems
                .Where(i => !i.IsImage && !string.IsNullOrEmpty(i.Text))
                .Select(i => i.Text!);
            parts.Add(LlmMessagePart.TextPart(string.Join("\n", stdText)));
            foreach (var item in standard.ContentItems.Where(i => i.IsImage && i.ImageData != null))
            {
                var (data, mime) = ImageCompressor.PrepareForUpload(item.ImageData!, item.ImageMimeType ?? "image/png");
                parts.Add(LlmMessagePart.ImagePart($"data:{mime};base64,{Convert.ToBase64String(data)}"));
            }
            parts.Add(LlmMessagePart.TextPart("===== 标准文档结束，以下是【待审核文档】====="));
        }

        // 待审文档文字 + 图片
        var docText = contentItems
            .Where(i => !i.IsImage && !string.IsNullOrEmpty(i.Text))
            .Select(i => i.Text!);
        parts.Add(LlmMessagePart.TextPart("文档文字内容：\n" + string.Join("\n\n", docText)));
        foreach (var item in contentItems.Where(i => i.IsImage && i.ImageData != null))
        {
            var (data, mime) = ImageCompressor.PrepareForUpload(item.ImageData!, item.ImageMimeType ?? "image/png");
            parts.Add(LlmMessagePart.ImagePart($"data:{mime};base64,{Convert.ToBase64String(data)}"));
        }

        // 3) 调用 LLM 审核（阶段不确定进度：上报 -1，界面切换流动条）
        percent?.Report(-1);
        var rawResult = await _llm.SendAsync([new LlmMessage { Role = "user", Parts = parts }], ct: ct);
        percent?.Report(80);
        Log("AI 审核完成");

        // 4) 格式化显示结果
        var formatted = FormatAuditResult(rawResult);

        // 5) 回写 DOCX（标红 + 书签 + 页尾意见）
        string? auditedPath = null;
        if (docxPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            percent?.Report(85);
            auditedPath = await _modifyService.AppendAuditResultWithBookmarksAsync(docxPath, rawResult ?? "审核结果为空",
                msg => Log(msg));
            percent?.Report(95);
            Log($"审核意见已附加到文档：{auditedPath}");
        }

        return new AuditOutcome { RawResult = rawResult, Formatted = formatted, AuditedFilePath = auditedPath };
    }

    /// <summary>格式化 AI 返回的 JSON 审核结果（迁移自 AiAuditDemo）。</summary>
    private static string FormatAuditResult(string rawResult)
    {
        if (string.IsNullOrWhiteSpace(rawResult)) return rawResult;

        var jsonMatch = Regex.Match(rawResult, @"```(?:json)?\s*\n?(.*?)\n?```", RegexOptions.Singleline);
        var jsonText = jsonMatch.Success ? jsonMatch.Groups[1].Value.Trim() : rawResult.Trim();

        if (!jsonText.StartsWith("{")) return rawResult;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            var lines = new List<string>();

            if (root.TryGetProperty("summary", out var summary))
            {
                lines.Add("【审核摘要】");
                lines.Add(summary.GetString() ?? "");
                lines.Add("");
            }

            if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var idx = 1;
                foreach (var issue in issues.EnumerateArray())
                {
                    lines.Add($"━━━ 问题 {idx} ━━━");
                    if (issue.TryGetProperty("originalText", out var ot)) lines.Add($"【原文】{ot.GetString()}");
                    if (issue.TryGetProperty("issue", out var iss)) lines.Add($"【问题】{iss.GetString()}");
                    if (issue.TryGetProperty("suggestion", out var sug)) lines.Add($"【建议】{sug.GetString()}");
                    lines.Add("");
                    idx++;
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return rawResult;
        }
    }
}
/// <summary>审核结果。</summary>
public class AuditOutcome
{
    public string RawResult { get; set; } = "";
    public string Formatted { get; set; } = "";
    public string AuditedFilePath { get; set; } = "";
}