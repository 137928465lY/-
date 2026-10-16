using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

/// <summary>案例结构化结果：文档生成时以它为主体程序化组装正文（LLM 仅少量补写）。</summary>
public class CaseDoc
{
    public string Title { get; set; } = "";
    public List<CaseSection> Sections { get; set; } = [];
}

public class CaseSection
{
    /// <summary>1 = 章（一、/1），2 = 节（1.1），3 = 小节（1.1.1）。</summary>
    public int Level { get; set; } = 1;
    public string Heading { get; set; } = "";
    public List<CaseBlock> Blocks { get; set; } = [];
}

public class CaseBlock
{
    /// <summary>p = 段落；table = 表格。</summary>
    public string Type { get; set; } = "p";
    public string Text { get; set; } = "";
    /// <summary>表格题注（type = table 时）。</summary>
    public string Caption { get; set; } = "";
    /// <summary>表格行（type = table，首行为表头）。</summary>
    public List<List<string>> Rows { get; set; } = [];
}

/// <summary>
/// 案例结构化：把 PDF 提取的原始文本（页眉重复、标题粘连、表格打散）清洗为
/// 结构化 JSON。每个案例只需结构化一次（结果缓存），生成文档时零 LLM 复用。
/// </summary>
public class CaseStructuringService
{
    private const string SystemPrompt =
        "你是文档结构化助手。把维修案例原文（PDF 提取，存在页眉重复、标题与正文粘连、表格被打散等问题）" +
        "清洗为结构化 JSON。只输出 JSON，格式：\n" +
        "{\"title\":\"案例标题\",\"sections\":[{\"level\":1,\"heading\":\"章节标题\",\"blocks\":[" +
        "{\"type\":\"p\",\"text\":\"段落文本\"},{\"type\":\"table\",\"caption\":\"表1 主材配备\"," +
        "\"rows\":[[\"序号\",\"物料描述\",\"单位\",\"用量\"],[\"1\",\"三轴向布\",\"Kg\",\"3\"]]}]}]}\n" +
        "要求：\n" +
        "1. 删除每页重复的页眉（如\"XXX叶片维修方案1\"\"XXX叶片维修方案2…\"）与封面署名行（编制/审核/批准/文件编号/版权声明）。\n" +
        "2. level：\"一、二、\"顶层章 = 1；\"2.1\"节 = 2；\"2.1.1\"小节 = 3。\n" +
        "3. 表格还原 rows（首行为表头）：依据列语义切分单元格，物料编码/描述/单位/用量逐行对应；无法确定列边界时宁可按段落输出，不要编造。\n" +
        "4. 正文文字原样保留，不改写不删减（可去掉多余空白）；标题只保留标题文字。\n" +
        "5. 只输出 JSON，不要解释文字或代码块标记。";

    private readonly LLMService _llm;

    public CaseStructuringService(LLMService llm)
    {
        _llm = llm;
    }

    /// <summary>结构化一份案例文本；失败返回 null（该案例退回 LLM 全文撰写路径）。</summary>
    public async Task<CaseDoc?> StructureAsync(string caseTitle, string rawText, CancellationToken ct = default)
    {
        var take = rawText.Length > 24000 ? rawText[..24000] + "…" : rawText;
        var user = $"案例标题：{caseTitle}\n\n案例原文：\n{take}";
        var raw = await _llm.SendAsync([LlmMessage.User(user)], SystemPrompt, ct: ct);

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<CaseDoc>(raw[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return doc is { Sections.Count: > 0 } ? doc : null;
        }
        catch
        {
            return null;
        }
    }

    // ─────────────── 章节归类与 HTML 组装（纯程序化，供生成时调用） ───────────────

    private static readonly Dictionary<int, (string Title, string[] Keywords)> ChapterMap = new()
    {
        [5] = ("风险管控", ["风险", "安全", "应急", "环保", "文明施工"]),
        [4] = ("验收与作业条件", ["验收", "检验", "质量", "周期", "工期", "人员", "作业条件", "引用文件", "编制说明"]),
        [3] = ("物料和工具", ["物料", "材料", "主材", "辅材", "工具", "设备", "劳保", "用量"]),
        [2] = ("维修方案", ["方案", "修复", "修补", "工艺", "步骤", "工序", "施工", "处理"]),
    };

    private static readonly string[] DropKeywords = ["编制", "审核", "批准", "文件编号", "版权", "目录", "修订记录", "附图", "附件"];

    /// <summary>按标题关键词把案例小节归入标准章节；0 = 丢弃（封面行等）。</summary>
    public static int ClassifyChapter(string heading)
    {
        var h = CleanHeading(heading);
        if (h.Length == 0) return 0;
        if (DropKeywords.Any(k => h.Contains(k))) return 0;
        foreach (var (chapter, (_, keywords)) in ChapterMap)
            if (keywords.Any(k => h.Contains(k)))
                return chapter;
        return 0;
    }

    /// <summary>标准章标题（未知章返回空串）。</summary>
    public static string GetChapterTitle(int chapter) =>
        ChapterMap.TryGetValue(chapter, out var v) ? v.Title : "";

    /// <summary>把案例小节组装为标准章 HTML（小节重新按 {章}.{序号} 编号）。</summary>
    public static string BuildChapterHtml(int chapter, IReadOnlyList<CaseSection> sections)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>{chapter} {GetChapterTitle(chapter)}</h1>");
        var sub = 0;
        foreach (var section in sections)
            sb.Append(RenderSection(section, chapter, ++sub));
        return sb.ToString();
    }

    /// <summary>渲染单个小节（标题 + 块内容；标题与章名重复时省略标题行）。</summary>
    public static string RenderSection(CaseSection section, int chapter, int sub)
    {
        var title = GetChapterTitle(chapter);
        var heading = CleanHeading(section.Heading);
        var sb = new StringBuilder();

        var isChapterTitle = heading.Length <= title.Length + 2 && title.Contains(heading) ||
                             ChapterMap[chapter].Keywords.Any(k => heading == k);
        if (!isChapterTitle && heading.Length > 0)
            sb.Append(section.Level >= 3
                ? $"<h3>{heading}</h3>"
                : $"<h2>{chapter}.{sub} {heading}</h2>");

        foreach (var block in section.Blocks)
        {
            if (block.Type == "table" && block.Rows.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(block.Caption))
                    sb.Append($"<p style=\"text-align:center\"><b>{block.Caption}</b></p>");
                sb.Append("<table border=\"1\">");
                foreach (var (row, i) in block.Rows.Select((r, i) => (r, i)))
                {
                    sb.Append("<tr>");
                    foreach (var cell in row)
                        sb.Append(i == 0 ? $"<th>{cell}</th>" : $"<td>{cell}</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
            }
            else if (!string.IsNullOrWhiteSpace(block.Text))
            {
                sb.Append($"<p>{block.Text}</p>");
            }
        }
        return sb.ToString();
    }

    /// <summary>小节纯文本（标题 + 段落 + 表格题注与单元格），供插图关键词匹配。</summary>
    public static string GetSectionText(CaseSection section)
    {
        var sb = new StringBuilder();
        sb.Append(CleanHeading(section.Heading));
        foreach (var block in section.Blocks)
        {
            sb.Append(block.Text);
            if (!string.IsNullOrWhiteSpace(block.Caption)) sb.Append(block.Caption);
            foreach (var row in block.Rows)
                foreach (var cell in row)
                    sb.Append(cell);
        }
        return sb.ToString();
    }

    /// <summary>去掉标题里的原文编号（"3.1 " / "一、"）。</summary>
    public static string CleanHeading(string heading)
    {
        var h = (heading ?? "").Trim();
        h = Regex.Replace(h, @"^(\d+(\.\d+)*|[一二三四五六七八九十]+)\s*[、.．]?\s*", "");
        return h.Trim();
    }
}
