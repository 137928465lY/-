using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class DocGenAgentService 
{
    /// <summary>内嵌默认格式（规划失败时的回退模板结构，迁自 Qt 版）。</summary>
    private const string DefaultFormat =
        "1. 名称\n2. 等级\n3. 总结\n4. 维修方案（描述、步骤、材料工具测算）";

    private readonly LLMService _llm;
    private readonly KnowledgeBaseService _knowledgeBase;
    private readonly DocImporterService _importer;

    public DocGenAgentService(LLMService llm, KnowledgeBaseService knowledgeBase, DocImporterService importer)
    {
        _llm = llm;
        _knowledgeBase = knowledgeBase;
        _importer = importer;
    }

    public async Task<DocGenDocument> GenerateAsync(DocGenInput input, IProgress<AgentStep>? progress = null,
        IProgress<string>? chunk = null, CancellationToken ct = default)
    {
        progress?.Report(new AgentStep { Index = 1, Name = "规划文档大纲" });

        // ─── 知识库参考内容（PDF 命中时自动触发按需识别；可见库图片可插入文档） ───
        var (hitContext, insertImages, referenceImageUrls, bestCase) = await BuildKnowledgeContextAsync(input, progress, ct);

        // ─── 步骤 1：规划（固定标准章节树，只规划标题；大纲不再自由发挥） ───
        var planSystem =
            "你是风电叶片缺陷维修文档的规划专家。文档章节结构已由标准模板固定（不需要你规划章节），" +
            "你只需根据缺陷信息与知识库参考内容判定并输出一个 JSON 对象（不要输出任何其他文字）：\n" +
            "{\"title\": \"文档标题（格式：缺陷名称维修方案-版本号，如：壳体玻璃钢损伤-分层维修方案-A1.0）\", " +
            "\"defects\": [\"识别出的缺陷1\", \"缺陷2\"], " +
            "\"grade\": \"等级（A/B/C/D/E）\", " +
            "\"workface\": \"internal 或 external（内部作业/外部作业）\", " +
            "\"scheme\": \"匹配的修复方式（如 RS-STR-04 夹芯真空灌注修复）\"}\n" +
            "defects 必须列全（多缺陷不得遗漏）；workface 判定：导线/电缆/端子/雷电记录卡/内蒙皮/人孔/挡板相关为 internal，" +
            "外蒙皮/涂层/型面/接闪器/合模缝相关为 external。";
        // 规划只需缺陷信息与方案匹配要点，上下文截断以提速（完整参考内容供撰写步骤使用）
        var planContext = hitContext.Length > 3000 ? hitContext[..3000] + "…" : hitContext;
        var planUser = $"缺陷信息：\n{input.DefectInfo}\n\n知识库参考内容：\n{planContext}";

        var planRaw = await _llm.SendAsync([LlmMessage.User(planUser)], planSystem,
            ResolveFastModel(input), ct);
        ct.ThrowIfCancellationRequested();

        var (title, sections) = ParsePlan(planRaw);
        var planJson = planRaw;

        // ─── 步骤 2：正文生成 ───
        var style = input.Style ?? new DocxStyle();
        var styleDesc = $"正文字体 {style.FontName} 字号 {style.BodyPt}pt；" +
                        $"章节标题加粗、字号 {style.HeadingPt}pt；大标题居中加粗、字号 {style.TitlePt}pt；" +
                        (input.TableThreeLine ? "表格为三线表（仅顶线、表头下线、底线，无竖线）" : "表格为全框线样式");

        string draftHtml;
        if (bestCase != null)
        {
            // ── 主体内容直接取自知识库案例：2~6 章程序化组装（零 LLM），LLM 仅撰写第 1 章与案例未覆盖的章 ──
            progress?.Report(new AgentStep { Index = 2, Name = "以知识库案例为主体组装正文" });

            var byChapter = bestCase.Sections
                .Select(s => (Section: s, Chapter: CaseStructuringService.ClassifyChapter(s.Heading)))
                .Where(x => x.Chapter >= 2)
                .GroupBy(x => x.Chapter)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Section).ToList());

            var chapterList = SplitSectionTree(DocGenPrompts.SectionTree);
            var fragments = new List<string>();
            var usedImages = new HashSet<DocGenImage>();
            var imgCountByChapter = new Dictionary<int, int>();
            foreach (var ch in new[] { 2, 3, 4, 5, 6 })
            {
                if (byChapter.TryGetValue(ch, out var secs) && secs.Count > 0)
                {
                    // 就近插图：按图题关键词匹配小节文本，插入对应小节之后
                    fragments.Add(BuildChapterWithImages(ch, secs, insertImages, usedImages, imgCountByChapter));
                }
                else
                {
                    var (num, tree) = chapterList.First(c => c.Number == ch);
                    progress?.Report(new AgentStep { Index = 2, Name = $"案例未覆盖第 {ch} 章，LLM 补写" });
                    fragments.Add(await GenerateChapterAsync(num, tree,
                        ChapterBudget.GetValueOrDefault(ch, 500),
                        input.DefectInfo, planJson, hitContext, styleDesc,
                        false, insertImages, [], ct));
                }
            }

            // 未匹配到小节的图：集中附加到第 2 章末尾兜底（图号续接该章已插数量）
            var leftover = insertImages.Where(i => !usedImages.Contains(i)).ToList();
            if (leftover.Count > 0 && fragments.Count > 0)
                fragments[0] += "\n" + RenderImages(leftover, 2, imgCountByChapter.GetValueOrDefault(2));

            // 第 1 章：缺陷分析随缺陷信息变化，由 LLM 撰写（主体之外唯一的生成部分）
            progress?.Report(new AgentStep { Index = 2, Name = "撰写第 1 章（缺陷分析）" });
            var (num1, tree1) = chapterList.First(c => c.Number == 1);
            var ch1 = await GenerateChapterAsync(num1, tree1, ChapterBudget[1],
                input.DefectInfo, planJson, hitContext, styleDesc,
                false, insertImages, referenceImageUrls.Take(3).ToList(), ct);

            draftHtml = string.Join("\n", ch1, string.Join("\n", fragments));
        }
        else
        {
            // ── 无结构化案例可用：退回分章并行撰写（每章独立 LLM 调用并行执行） ──
            progress?.Report(new AgentStep { Index = 2, Name = "分章并行撰写正文" });

            var chapters = SplitSectionTree(DocGenPrompts.SectionTree);
            var doneCount = 0;
            using var gate = new SemaphoreSlim(3); // 并发上限，避免触发 API 限流
            var chapterTasks = chapters.Select(async chapter =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // 插图与多模态参考图只随相关章节发送：IMG_N 插图池仅限第 2 章（维修工序），参考图限第 1/2 章
                    var withImages = chapter.Number == 2 && insertImages.Count > 0;
                    var urls = withImages
                        ? insertImages.Select(i => i.DataUrl)
                            .Concat(chapter.Number <= 2 ? referenceImageUrls : [])
                            .Take(10).ToList()
                        : chapter.Number <= 2 ? referenceImageUrls.Take(3).ToList() : [];

                    var html = await GenerateChapterAsync(chapter.Number, chapter.Tree,
                        ChapterBudget.GetValueOrDefault(chapter.Number, 500),
                        input.DefectInfo, planJson, hitContext, styleDesc,
                        withImages, insertImages, urls, ct);

                    var done = Interlocked.Increment(ref doneCount);
                    progress?.Report(new AgentStep { Index = 2, Name = $"撰写完成 {done}/{chapters.Count} 章" });
                    return (chapter.Number, html);
                }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(chapterTasks);
            ct.ThrowIfCancellationRequested();

            draftHtml = string.Join("\n",
                chapterTasks.Select(t => t.Result)
                    .OrderBy(r => r.Number)
                    .Select(r => r.html));
        }

        if (string.IsNullOrEmpty(draftHtml))
            throw new InvalidOperationException("撰写步骤未产出有效内容");

        // ─── 步骤 3：审校（快速模式下跳过，草稿即终稿） ───
        string finalHtml;
        if (input.FastMode)
        {
            progress?.Report(new AgentStep { Index = 3, Name = "快速模式：跳过审校" });
            finalHtml = draftHtml;
        }
        else
        {
            progress?.Report(new AgentStep { Index = 3, Name = "审校润色" });
            var reviewSystem =
                "你是文档审校专家。请对以下 HTML 维修文档进行审校并输出修正后的完整 HTML：\n" +
                "1. 章节完整性：对照标准章节树核对（1 叶片损伤情况分析 1.1~1.8 / 2 维修方案 2.1~2.4 / 3 物料和工具 3.1~3.4 / " +
                "4 验收与作业条件 4.1~4.3 / 5 风险管控 / 6 修订记录），缺失的小节须补写，多出的删除。\n" +
                "2. 检查并修正表格序号的连续性与表格上方居中的表格题注。\n" +
                "3. 图片检查：仅允许保留 src 为 IMG_ 占位符（IMG_1、IMG_2…）的图片，删除其他来源的图片引用；" +
                "每张图片下方须有居中图题，图号按章连续（图<章>-<序号>）。\n" +
                "4. 业务规则核对：多缺陷是否列全、班组/用量口径是否正确（玻纤布面积×层数×1.30、薄膜≥1.0㎡、" +
                "外部4人/内部3人班组）、内部作业不得出现高空/吊篮/涂层恢复字样、关键指标是否红色字体。\n" +
                "5. 保持未被修改的内容原样，不新增不删减章节。\n" +
                "6. 只输出修正后的完整 HTML 源码，不要任何解释文字。";

            try
            {
                var reviewRaw = await StreamCollectAsync(reviewSystem, draftHtml, null,
                    ResolveFastModel(input), chunk, ct);
                finalHtml = ExtractBodyHtml(reviewRaw);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                finalHtml = ""; // 审校失败回退到草稿
            }

            if (string.IsNullOrEmpty(finalHtml))
                finalHtml = draftHtml;
        }

        return new DocGenDocument
        {
            Title = title,
            PlanJson = planJson,
            Sections = sections,
            DraftHtml = draftHtml,
            FinalHtml = finalHtml,
            Images = insertImages,
        };
    }

    public void Cancel() => _llm.Cancel();

    /// <summary>
    /// 按用户修改意见修订已生成的文档：严格落实删减/补充要求；
    /// 若意见涉及标题变更，同步更新 doc.Title（封面标题随之刷新）。
    /// </summary>
    public async Task ApplyRevisionAsync(DocGenDocument doc, string revisionComments,
        CancellationToken ct = default)
    {
        var system =
            "你是维修文档修订助手。请根据用户的修改意见对以下 HTML 维修文档进行修订，并输出修正后的完整 HTML：\n" +
            "1. 严格落实每一条修改意见：明确要求删除的内容必须删除，要求补充/调整的部分按意见修改。\n" +
            "2. 未被意见涉及的章节保持原样，不新增不删减章节结构。\n" +
            "3. 图片仅允许保留 src 为 IMG_ 占位符（IMG_1、IMG_2…）的图片，不得新增或删除图片。\n" +
            "4. 修正表格序号/题注的连续性。\n" +
            "5. 若修改意见涉及文档标题变更（如版本号从 A1.0 升级为 A2.0），" +
            "请在 <head> 的 <title> 标签中输出新的完整标题文字。\n" +
            "6. 只输出修正后的完整 HTML 源码，不要任何解释文字。";
        var user = $"修改意见：\n{revisionComments}\n\n原文档 HTML：\n{doc.FinalHtml}";

        var raw = await StreamCollectAsync(system, user, null, null, null, ct);

        // 标题变更提取：<title> 中给出新标题时同步更新（封面标题来自 doc.Title）
        var titleMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"<title[^>]*>([\s\S]*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            var newTitle = titleMatch.Groups[1].Value.Trim();
            if (newTitle.Length > 0 && newTitle != doc.Title)
                doc.Title = newTitle;
        }

        var html = ExtractBodyHtml(raw);
        if (string.IsNullOrEmpty(html))
            throw new InvalidOperationException("修订未产出有效内容");
        doc.FinalHtml = html;
    }

    // ─────────────── 内部实现 ───────────────

    /// <summary>各章篇幅预算（字）：合计约 6000+，第 2 章为文档主体。</summary>
    private static readonly Dictionary<int, int> ChapterBudget = new()
    {
        [1] = 1300, [2] = 2600, [3] = 1000, [4] = 800, [5] = 600, [6] = 300,
    };

    /// <summary>按顶层章节行（"N 标题"）把章节树拆为独立章节（子节缩进归入所属章）。</summary>
    private static List<(int Number, string Tree)> SplitSectionTree(string sectionTree)
    {
        var chapters = new List<(int Number, string Tree)>();
        var sb = new StringBuilder();
        var current = 0;
        foreach (var rawLine in sectionTree.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0) continue;

            var match = Regex.Match(line.Trim(), @"^(\d+)\s");
            var isTopLevel = !rawLine.StartsWith(' ') && match.Success;
            if (isTopLevel && sb.Length > 0)
            {
                chapters.Add((current, sb.ToString().TrimEnd()));
                sb.Clear();
            }
            if (isTopLevel) current = int.Parse(match.Groups[1].Value);
            sb.AppendLine(line);
        }
        if (sb.Length > 0) chapters.Add((current, sb.ToString().TrimEnd()));
        return chapters;
    }

    /// <summary>
    /// 组装某章 HTML 并就近插图：图题关键词（缺陷类型词，如"夹芯剔除"）与
    /// 小节文本精确匹配插入对应小节之后；无精确命中时退回匹配 2 字词根；
    /// 本章完全无命中的图不插入（由调用方兜底集中放置）。
    /// </summary>
    private static string BuildChapterWithImages(int chapter, List<CaseSection> sections,
        IReadOnlyList<DocGenImage> candidates, HashSet<DocGenImage> used, Dictionary<int, int> imgCountByChapter)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>{chapter} {CaseStructuringService.GetChapterTitle(chapter)}</h1>");

        var remaining = candidates.Where(i => !used.Contains(i)).ToList();
        var sub = 0;
        foreach (var section in sections)
        {
            sb.Append(CaseStructuringService.RenderSection(section, chapter, ++sub));

            var text = CaseStructuringService.GetSectionText(section);
            foreach (var img in remaining.Where(i => !used.Contains(i)))
            {
                var word = DefectWord(img);
                if (word.Length == 0) continue;
                if (!text.Contains(word) && !(word.Length >= 2 && text.Contains(word[..2]))) continue;

                used.Add(img);
                var n = imgCountByChapter.GetValueOrDefault(chapter) + 1;
                imgCountByChapter[chapter] = n;
                sb.Append(RenderImage(img, $"图{chapter}-{n} {CaptionOf(img)}"));
            }
        }
        return sb.ToString();
    }

    /// <summary>渲染居中图片 + 图题。</summary>
    private static string RenderImage(DocGenImage img, string caption) =>
        $"<p style=\"text-align:center\"><img src=\"{img.Token}\" width=\"450\" /></p>" +
        $"<p style=\"text-align:center\">{caption}</p>";

    /// <summary>图题：AI 补充图用缺陷类型词 + （示意）标注；案例照片用原始题注。</summary>
    private static string CaptionOf(DocGenImage img) =>
        img.IsSupplement ? $"{DefectWord(img)}（示意）" : img.Title;

    /// <summary>取图片题名中的缺陷类型词（命名约定第 2 段，如"CS-02 夹芯剔除 通用 00" → "夹芯剔除"）。</summary>
    private static string DefectWord(DocGenImage img)
    {
        var tokens = img.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 ? tokens[1] : "";
    }

    /// <summary>渲染一组图片（兜底集中放置，图号从 start 起连续）。</summary>
    private static string RenderImages(IEnumerable<DocGenImage> images, int chapter, int start = 0)
    {
        var sb = new StringBuilder();
        var n = start;
        foreach (var img in images)
        {
            n++;
            sb.Append("\n").Append(RenderImage(img, $"图{chapter}-{n} {CaptionOf(img)}"));
        }
        return sb.ToString();
    }

    /// <summary>撰写单个章节的 HTML 片段（由分章并行调用）。</summary>
    private async Task<string> GenerateChapterAsync(int number, string chapterTree, int budget,
        string defectInfo, string planJson, string hitContext, string styleDesc,
        bool withImages, List<DocGenImage> insertImages, List<string> imageUrls,
        CancellationToken ct)
    {
        var imageRule = withImages
            ? "4. 图片使用：本消息按顺序附有 " + insertImages.Count + " 张待插图，第 1 张对应 IMG_1、第 2 张对应 IMG_2……请先仔细看图再决定用图与写图题。" +
              "待插图清单（占位符 = 知识库原始题注/来源）：" +
              string.Join("；", insertImages.Select(i => $"{i.Token} =「{i.Title}」")) +
              "。插入写法：<img src=\"IMG_1\" width=\"450\" />，并在图片下方输出居中图题（如：图" + number + "-1 夹芯剔除示意），" +
              "图题描述必须与图片实际内容一致（图中是什么工艺/缺陷就写什么）；若清单给出了原始题注则沿用其描述；" +
              $"图号按本章连续（图{number}-1、图{number}-2…）；一道工序最多一张图，同图不得重复使用；无合适位置的图可不用；" +
              "严禁在图片下方写出与图片内容无关的图题。" +
              "警告：img 的 src 只能精确填写 IMG_1、IMG_2 等给定占位符字符串，严禁编造任何文件名、路径或 URL。\n"
            : "4. 本次无可用图片，本章不插入任何 <img> 标签，也不得出现\"如图X所示\"等图片引用文字与图题。\n";

        var system =
            "你是风电叶片缺陷维修文档撰写助手。本次只撰写文档的第 " + number + " 章，直接输出该章 HTML 片段。要求：\n" +
            "1. 只输出本章 HTML 片段：以 <h1> 章标题开始，小节用 <h2>/<h3>、正文段落与表格构成；" +
            "禁止 <!DOCTYPE、<html>、<head>、<body> 标签，禁止封面/目录/页眉，禁止任何解释文字或代码块标记。\n" +
            $"2. 排版样式：{styleDesc}（用内联 CSS 实现）。\n" +
            $"3. 表格使用 <table border=\"1\">，每个表格上方输出居中的表格序号与名称（如：表{number}-1 维修材料），" +
            $"表号按本章编号（表{number}-1、表{number}-2…）。\n" +
            imageRule +
            $"5. 严格按照本章章节树逐节输出（章节编号与名称必须与树一致，内容按规划结果填写）：\n{chapterTree}\n" +
            (number == 2
                ? "6. 维修步骤（2.2）必须完整详细：按知识库案例中的工步逐条展开（步骤一、步骤二…），" +
                  "每道写明：使用材料/工具及用量、具体工艺参数（配比、温度、时间、压力、打磨错层尺寸等）、" +
                  "控制要点与质量检验标准（如巴氏硬度≥60HBa、电阻≤50mΩ、保压测试要求）。\n"
                : "") +
            "7. 知识库参考内容中的具体数值（物料、用量、温度、时间、配比、硬度/电阻要求）必须原样采用，" +
            "禁止编造、禁止概括省略；与缺陷类型最相关的案例是本章内容的主要依据。\n" +
            "8. 内容边界：知识库参考内容与缺陷信息是唯一事实来源，你只负责组织结构、成文与语言润色，" +
            "参考内容未覆盖的工艺方法/参数不得自行发明（留空或写\"按现场技术交底执行\"）；" +
            "禁止输出任何面向用户的说明性文字（如\"这是根据…生成的HTML\"）。\n" +
            $"9. 业务规则（必须逐条遵守）：\n{DocGenPrompts.BusinessRules}\n" +
            $"10. 本章篇幅约 {budget} 字，须完整成文成表。";
        var user = $"缺陷信息：\n{defectInfo}\n\n规划结果（JSON）：\n{planJson}\n\n知识库参考内容：\n{hitContext}";

        // 撰写步骤始终使用当前启用模型（质量优先，不换快速模型）
        var raw = await StreamCollectAsync(system, user, imageUrls, null, null, ct);
        var html = ExtractBodyHtml(raw);
        if (string.IsNullOrEmpty(html))
            throw new InvalidOperationException($"第 {number} 章撰写未产出有效内容");
        return html;
    }

    private async Task<(string Context, List<DocGenImage> InsertImages, List<string> ReferenceImageUrls, CaseDoc? BestCase)> BuildKnowledgeContextAsync(DocGenInput input,
        IProgress<AgentStep>? progress, CancellationToken ct)
    {
        var ctx = new StringBuilder();
        var referenceImageUrls = new List<string>();
        var usedTitles = new List<string>();
        CaseDoc? bestCase = null; // 检索命中的首个结构化案例（生成时作为正文主体）

        // 按标题去重：同一案例可能同时存在文本条目与 PDF 存档条目（如内置案例库与案例知识库），
        // 保留得分最高的文本条目，避免重复引用与无谓的 PDF 识别调用
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBudget = 40000; // 参考内容总字符预算（控制 prompt 体量）

        foreach (var hit in _knowledgeBase.Search(input.DefectInfo,
                     input.KnowledgeBaseIds.Count > 0 ? input.KnowledgeBaseIds : null, limit: 12,
                     includeHidden: true))
        {
            ct.ThrowIfCancellationRequested();
            if (totalBudget <= 0) break;
            var entry = hit.Entry;

            if (!seenTitles.Add(entry.Title)) continue;

            // 首个带结构化结果的命中案例（正文主体的候选，按检索得分取最优）
            if (bestCase == null && entry.ArchiveType != "image")
                bestCase = _knowledgeBase.TryGetStructuredCase(entry);

            if (entry.ArchiveType == "image" && !string.IsNullOrEmpty(entry.FilePath))
            {
                // 隐藏/可见库图片仅作多模态参考附图（最多 3 张），不直接插入文档
                if (referenceImageUrls.Count < 3)
                {
                    var dataUrl = _importer.FileToDataUrl(entry.FilePath);
                    if (dataUrl != null)
                    {
                        referenceImageUrls.Add(dataUrl);
                        ctx.AppendLine($"【图片参考：{entry.Title}】（该图片已随消息附上，可结合图片内容回答）\n");
                    }
                }
                continue;
            }

            // PDF 条目：按需识别后并入上下文（每条最多 12000 字，总量 40000 字预算）
            var content = await _knowledgeBase.GetContentAsync(entry,
                new Progress<string>(s => progress?.Report(new AgentStep { Index = 1, Name = s })), ct);

            var take = Math.Min(content.Length, Math.Min(12000, totalBudget));
            ctx.AppendLine($"【知识：{entry.Title}】");
            ctx.AppendLine(take < content.Length ? content[..take] + "…" : content);
            ctx.AppendLine();
            totalBudget -= take;
            usedTitles.Add(entry.Title);
        }

        // 可插入图片：仅来自非隐藏知识库（图片知识库等），与缺陷信息相关的图片条目
        // 图片检索不受文本库勾选限制（始终检索全部可见库的图片条目，图片库必然参与）
        // 检索词 = 缺陷信息 + 工序关键词，提高图片命中率；最多 8 张供模型选择插入
        var insertImages = new List<DocGenImage>();
        var imageQuery = input.DefectInfo + " 打磨 夹芯 布层 灌注 真空 加热固化 涂层 电阻";
        // limit 必须显著大于图片数：Search 的 limit 作用于全部条目（案例全文得分远高于图片），
        // 取小了图片候选会被文本条目挤出，导致无图可插
        var imageHits = _knowledgeBase.Search(imageQuery, baseIds: null, limit: 120,
                includeHidden: false)
            .Where(h => h.Entry.ArchiveType == "image" && !string.IsNullOrEmpty(h.Entry.FilePath))
            .ToList();

        // AI 补充图（标准化工序示意，标题与工序名精确对应）优先占位，案例照片补足剩余名额：
        // 否则老照片题注中的高频词（打磨/布层等）总能占据全部席位，补充图永远无法被引用
        var orderedHits = imageHits
            .Where(h => h.Entry.Tags.Contains("补充图片"))
            .Concat(imageHits.Where(h => !h.Entry.Tags.Contains("补充图片")));

        foreach (var hit in orderedHits)
        {
            if (insertImages.Count >= 8) break;
            var dataUrl = _importer.FileToDataUrl(hit.Entry.FilePath);
            if (dataUrl == null) continue;
            insertImages.Add(new DocGenImage
            {
                Token = $"IMG_{insertImages.Count + 1}",
                DataUrl = dataUrl,
                Title = hit.Entry.Title,
                IsSupplement = hit.Entry.Tags.Contains("补充图片"),
            });
        }

        // 命中情况可视化：展示实际参考了哪些条目，便于排查生成质量
        progress?.Report(new AgentStep
        {
            Index = 1,
            Name = usedTitles.Count > 0
                ? $"检索命中 {usedTitles.Count} 条：{string.Join("、", usedTitles.Take(3))}{(usedTitles.Count > 3 ? " 等" : "")}"
                : "检索未命中任何知识条目",
        });

        return (ctx.ToString(), insertImages, referenceImageUrls, bestCase);
    }

    /// <summary>按名称解析快速模型；未配置或找不到时返回 null（= 当前启用模型）。</summary>
    private LlmModelConfig? ResolveFastModel(DocGenInput input) =>
        input.FastModelName is { Length: > 0 } name
            ? _llm.Models.FirstOrDefault(m => m.Name == name)
            : null;

    private async Task<string> StreamCollectAsync(string systemPrompt, string userText,
        List<string>? imageUrls, LlmModelConfig? model, IProgress<string>? chunk, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var message = (imageUrls is { Count: > 0 })
            ? new LlmMessage
            {
                Role = "user",
                Parts =
                [
                    LlmMessagePart.TextPart(userText),
                    .. imageUrls.Select(LlmMessagePart.ImagePart),
                ],
            }
            : LlmMessage.User(userText);

        await foreach (var delta in _llm.SendStreamAsync([message], systemPrompt, model: model, ct: ct))
        {
            sb.Append(delta);
            chunk?.Report(delta);
        }
        return sb.ToString();
    }

    private static (string Title, List<string> Sections) ParsePlan(string raw)
    {
        try
        {
            var match = Regex.Match(raw, @"\{[\s\S]*\}");
            var json = match.Success ? match.Value : raw.Trim();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

            var sections = new List<string>();
            if (doc.RootElement.TryGetProperty("sections", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                sections.AddRange(arr.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => s.Length > 0));
            }

            return (string.IsNullOrWhiteSpace(title) ? "叶片缺陷维修文档" : title, sections);
        }
        catch
        {
            // 解析失败回退内嵌模板结构
            return ("叶片缺陷维修文档", []);
        }
    }

    /// <summary>提取 HTML 正文（迁移自 Qt 版 extractBodyHtml）。</summary>
    private static string ExtractBodyHtml(string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return "";

        // 模型常把 HTML 包在 Markdown 代码围栏里、并在围栏外输出说明文字
        //（如"这是根据您提供的维修方案文档生成的HTML代码…```html"），先剥离围栏取内容
        var fence = Regex.Match(text, @"```(?:html)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fence.Success && fence.Groups[1].Value.Contains('<'))
            text = fence.Groups[1].Value.Trim();

        var bodyMatch = Regex.Match(text, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
        if (bodyMatch.Success) return bodyMatch.Groups[1].Value.Trim();

        // 无 body 标签时：截掉首个 HTML 标签之前的所有说明文字（前言），并去掉尾部围栏残留
        if (!text.StartsWith("<"))
        {
            var tagMatch = Regex.Match(text, @"<\s*!?[a-zA-Z]");
            if (tagMatch.Success) text = text[tagMatch.Index..].Trim();
        }
        text = Regex.Replace(text, @"^\s*```(?:html)?\s*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s*```\s*$", "");

        text = Regex.Replace(text, @"<html[^>]*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</html>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<head[\s\S]*?</head>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?body[^>]*>", "", RegexOptions.IgnoreCase);
        return text.Trim();
    }
}
