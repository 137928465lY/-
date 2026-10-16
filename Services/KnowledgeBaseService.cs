using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class KnowledgeBaseService 
{
    private static readonly string DataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "叶片缺陷审核", "knowledge_base.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static readonly Dictionary<string, string> BuiltinBaseNames = new()
    {
        ["builtin_leaf_defects"] = "叶片缺陷知识库",
        ["builtin_summary"] = "维修总结知识库",
    };

    private readonly List<KnowledgeBaseInfo> _bases = [];
    private readonly DocImporterService _importer;
    private readonly PdfRecognizeService _pdfRecognizer;
    private readonly CaseStructuringService _caseStructurer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public KnowledgeBaseService(DocImporterService importer, PdfRecognizeService pdfRecognizer,
        PdfImageExtractor pdfImageExtractor, CaseStructuringService caseStructurer)
    {
        _importer = importer;
        _pdfRecognizer = pdfRecognizer;
        _pdfImageExtractor = pdfImageExtractor;
        _caseStructurer = caseStructurer;
    }

    private readonly PdfImageExtractor _pdfImageExtractor;

    public IReadOnlyList<KnowledgeBaseInfo> Bases => _bases;

    private bool _initialized;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (File.Exists(DataPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<KnowledgeBaseInfo>>(File.ReadAllText(DataPath));
                if (saved != null) _bases.AddRange(saved);
            }
            catch
            {
                // 配置损坏则重建
            }
        }

        // 内置库自愈：无论数据文件处于何种状态，四个内置库必须存在且不可删除
        // 叶片缺陷知识库含客户敏感案例全文，与案例库同样隐藏（用户不可直接访问，仅由知识库调用内部引用）
        GetOrCreateBase("builtin_leaf_defects", "叶片缺陷知识库", isBuiltin: true, hidden: true);
        GetOrCreateBase("builtin_summary", "维修总结知识库", isBuiltin: true);
        GetOrCreateBase("builtin_leaf_repair", "叶片维修知识库", isBuiltin: true);
        GetOrCreateBase("builtin_hidden_cases", "叶片维修案例库（内置隐藏）", isBuiltin: true, hidden: true);

        // 首次运行：合并内置种子（按条目 id 去重，避免覆盖用户数据）
        try
        {
            // 种子文件随程序输出（SeedKB/builtin_seed.json），文件系统加载
            var seedPath = Path.Combine(AppContext.BaseDirectory, "SeedKB", "builtin_seed.json");
            if (!File.Exists(seedPath))
                throw new FileNotFoundException($"未找到种子文件：{seedPath}");
            var seed = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(seedPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (seed?.KnowledgeBases != null)
            {
                foreach (var seedBase in seed.KnowledgeBases)
                {
                    var name = BuiltinBaseNames.GetValueOrDefault(seedBase.Id, seedBase.Id);
                    var existing = _bases.FirstOrDefault(b => b.Id == seedBase.Id);
                    if (existing == null)
                    {
                        existing = new KnowledgeBaseInfo { Id = seedBase.Id, Name = name, IsBuiltin = true };
                        _bases.Add(existing);
                    }

                    var knownIds = existing.Entries.Select(e => e.Id).ToHashSet();
                    foreach (var doc in seedBase.Docs ?? [])
                    {
                        if (knownIds.Contains(doc.Id)) continue;
                        existing.Entries.Add(new KnowledgeEntry
                        {
                            Id = doc.Id,
                            Title = doc.Title,
                            Tags = (doc.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
                            Content = doc.Content ?? "",
                            ArchiveType = "text",
                            IsBuiltin = true,
                        });
                    }
                }
                Save();
            }
        }
        catch (Exception ex)
        {
            // 种子失败不阻塞启动，但记录日志便于排查
            LogSeed("builtin_seed.json 合并失败：" + ex.Message);
        }

        // 内置库二：叶片维修知识库（可见，结构化 JSON 分节入库 + 主文档 docx）
        try
        {
            await SeedLeafRepairBaseAsync();
        }
        catch (Exception ex)
        {
            LogSeed("叶片维修知识库 种子失败：" + ex.Message);
        }

        // 内置库三：叶片维修案例库（隐藏，PDF 案例存档，仅文档生成参考时可见）
        try
        {
            SeedHiddenCaseBase();
            Save();
        }
        catch (Exception ex)
        {
            LogSeed("叶片维修案例库 种子失败：" + ex.Message);
        }

        // 内置库四：图片知识库（可见）——从案例 PDF 中提取施工参考图，供文档生成插图
        try
        {
            await SeedImageBaseAsync();
            Save();
        }
        catch (Exception ex)
        {
            LogSeed("图片知识库 种子失败：" + ex.Message);
        }

        // 后台预热：隐藏案例库 PDF 文本层直读（零 LLM 成本，见 PrewarmHiddenPdfTextAsync）
        _ = PrewarmHiddenPdfTextAsync();
    }

    /// <summary>
    /// 后台预热隐藏案例库 PDF：优先读随程序分发的离线文本缓存（text_index.json → txt/），
    /// 未覆盖的条目再现场 PdfPig 文本层直读（不调用 LLM，免费秒级）；
    /// 识别结果写回 entry.Content 并持久化——文档生成与检索摘要首次即命中缓存；
    /// 含扫描页（无有效文本层）的条目跳过，保留生成时按需视觉识别。
    /// </summary>
    private async Task PrewarmHiddenPdfTextAsync()
    {
        try
        {
            var kb = _bases.FirstOrDefault(b => b.Id == "builtin_hidden_cases");
            if (kb == null) return;

            var pending = kb.Entries
                .Where(e => e.ArchiveType == "pdf" && string.IsNullOrEmpty(e.Content)
                            && !string.IsNullOrEmpty(e.FilePath) && File.Exists(e.FilePath))
                .ToList();
            if (pending.Count == 0) return;

            var done = 0;
            foreach (var entry in pending)
            {
                try
                {
                    // 1) 离线文本缓存直读（零成本）
                    var text = TryLoadIndexedPdfText(Path.GetFileName(entry.FilePath!));
                    // 2) 兜底：现场 PdfPig 文本层抽取（含扫描页则放弃，留给按需识别）
                    if (string.IsNullOrEmpty(text))
                    {
                        var result = await _pdfRecognizer.RecognizeAsync(entry.FilePath!, log: null,
                            CancellationToken.None, allowOcrFallback: false);
                        if (!result.NeedsOcr) text = result.FullText;
                    }
                    if (string.IsNullOrEmpty(text) || text.Length < 100) continue;
                    entry.Content = text;
                    entry.Updated = DateTime.Now;
                    done++;
                    if (done % 5 == 0) Save();
                }
                catch { /* 单文件失败跳过，不影响其余 */ }
            }
            if (done > 0)
            {
                Save();
                LogSeed($"隐藏案例库预热完成：{done}/{pending.Count} 份 PDF 文本已缓存" +
                        $"（其余为扫描件，生成时按需识别）");
            }

            // ── 阶段 2：案例结构化（LLM 一次性清洗，结果落盘终身复用） ──
            // 文档生成以结构化案例为主体程序化组装正文（LLM 仅撰写第 1 章），
            // 未结构化的案例退回 LLM 全文撰写路径
            await PrewarmStructuredCasesAsync(kb);
        }
        catch (Exception ex)
        {
            LogSeed("隐藏案例库预热失败：" + ex.Message);
        }
    }

    /// <summary>结构化缓存目录（归档目录下，随用户数据持久化）。</summary>
    private string StructuredDir =>
        Path.Combine(_importer.ArchiveDirectory, "kb_structured");

    /// <summary>读取案例的结构化结果；未结构化返回 null。</summary>
    public CaseDoc? TryGetStructuredCase(KnowledgeEntry entry)
    {
        try
        {
            var path = Path.Combine(StructuredDir, entry.Id + ".json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CaseDoc>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后台逐个结构化案例（LLM 清洗，一次性成本）；未配置模型时静默跳过。</summary>
    private async Task PrewarmStructuredCasesAsync(KnowledgeBaseInfo kb)
    {
        try
        {
            var pending = kb.Entries
                .Where(e => !string.IsNullOrEmpty(e.Content))
                .Where(e => !File.Exists(Path.Combine(StructuredDir, e.Id + ".json")))
                .ToList();
            if (pending.Count == 0) return;

            Directory.CreateDirectory(StructuredDir);
            var done = 0;
            foreach (var entry in pending)
            {
                try
                {
                    var doc = await _caseStructurer.StructureAsync(entry.Title, entry.Content);
                    if (doc == null) continue;
                    doc.Title = entry.Title;
                    File.WriteAllText(Path.Combine(StructuredDir, entry.Id + ".json"),
                        JsonSerializer.Serialize(doc));
                    done++;
                }
                catch (InvalidOperationException)
                {
                    // 未配置可用模型：结构化需要 LLM，整体跳过（生成时退回全文撰写路径）
                    LogSeed("案例结构化跳过：未配置可用模型");
                    return;
                }
                catch { /* 单案例失败跳过 */ }
            }
            if (done > 0)
                LogSeed($"案例结构化完成：{done}/{pending.Count} 份（正文可程序化组装）");
        }
        catch (Exception ex)
        {
            LogSeed("案例结构化预热失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按文件名查找随程序分发的 PDF 离线文本缓存
    /// （SeedKB/HiddenKB/text_index.json → txt/*.txt，发布前用 PdfPig 预生成）。
    /// </summary>
    private static string? TryLoadIndexedPdfText(string pdfFileName)
    {
        try
        {
            var indexPath = Path.Combine(AppContext.BaseDirectory, "SeedKB", "HiddenKB", "text_index.json");
            if (!File.Exists(indexPath)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (!doc.RootElement.TryGetProperty("files", out var files)) return null;

            foreach (var item in files.EnumerateArray())
            {
                if (!string.Equals(item.GetProperty("pdf").GetString(), pdfFileName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = item.GetProperty("txt").GetString();
                if (string.IsNullOrEmpty(rel)) return null;
                var txtPath = Path.Combine(Path.GetDirectoryName(indexPath)!,
                    rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(txtPath) ? File.ReadAllText(txtPath) : null;
            }
        }
        catch
        {
            // 索引缺失/损坏不影响主流程（回退到现场文本层抽取）
        }
        return null;
    }

    /// <summary>
    /// 图片知识库（可见内置库）：优先采用用户人工整理的图片目录（标题=文件名，检索最语义化）；
    /// 无人工整理标记时才从案例 PDF 自动提取（题注标题 + 公章过滤）。供文档生成插图。
    /// </summary>
    private async Task SeedImageBaseAsync()
    {
        var kb = GetOrCreateBase("builtin_images", "图片知识库", isBuiltin: true);
        var imageDir = Path.Combine(_importer.ArchiveDirectory, "pdf_images");

        // 随包分发图片（SeedKB/images：案例提取图 + AI 补充图）增量拷入本地图片目录
        //（新机器首启即有完整图片库；按文件名幂等，不覆盖本地已有）
        var seedImageDir = Path.Combine(AppContext.BaseDirectory, "SeedKB", "images");
        if (Directory.Exists(seedImageDir))
        {
            Directory.CreateDirectory(imageDir);
            var copied = 0;
            foreach (var f in Directory.GetFiles(seedImageDir))
            {
                var dest = Path.Combine(imageDir, Path.GetFileName(f));
                if (File.Exists(dest)) continue;
                File.Copy(f, dest);
                copied++;
            }
            if (copied > 0) LogSeed($"图片知识库：拷入随包分发的图片 {copied} 张");
        }

        // 增量补登记：目录中新增图片（如 AI 生成的补充图，见 docs/图片库补充建议清单.csv）
        // 尚未登记时追加条目——幂等、只追加、不重建不删除；
        // 注意必须在"人工整理"早退之前执行，否则人工模式下新增图片永远无法入库
        await SyncNewImagesAsync(kb, imageDir);

        // 用户人工整理过 → 永远尊重人工成果，程序不再自动重建/覆盖
        if (kb.Entries.Any(e => e.Tags.Contains("人工整理")))
            return;

        // v11 迁移：目录已被用户手动整理（重命名/精选），以目录现状为准重建条目
        if (kb.Entries.Count > 0 && !kb.Entries.Any(e => e.Tags.Contains("施工参考图v11")))
        {
            LogSeed("图片知识库 v11：以人工整理的图片目录为准重建条目…");
            kb.Entries.Clear();
            Save();

            await Task.Run(() =>
            {
                var rebuilt = 0;
                if (Directory.Exists(imageDir))
                {
                    foreach (var file in Directory.GetFiles(imageDir)
                                 .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            var stem = Path.GetFileNameWithoutExtension(file);
                            var parts = stem.Split('_');
                            var defect = parts.Length > 1 ? parts[1] : "";   // 约定：第 2 段 = 缺陷类型
                            var site = parts.Length > 2 ? parts[2] : "";     // 第 3 段 = 风场/来源

                            kb.Entries.Add(new KnowledgeEntry
                            {
                                Id = "img_" + stem,
                                // 标题 = 文件名（下划线转空格便于展示）；子串检索不受影响
                                Title = string.Join(" ", parts),
                                Tags = new List<string> { "人工整理", "施工参考图" }
                                    .Concat(new[] { defect, site }.Where(t => t.Length > 0))
                                    .Distinct().ToList(),
                                Content = $"{defect} {site}".Trim(),
                                ArchiveType = "image",
                                FilePath = file,
                                IsBuiltin = true,
                            });
                            rebuilt++;
                        }
                        catch { /* 单文件失败跳过 */ }
                    }
                }
                LogSeed($"图片知识库重建完成：{rebuilt} 张（人工整理版，此后不再自动重建）");
            });
            Save();
            return;
        }

        if (kb.Entries.Count > 0) return; // 已提取过，幂等

        const string imageTag = "施工参考图v11";
        var hiddenPdfBase = _bases.FirstOrDefault(b => b.Id == "builtin_hidden_cases");
        if (hiddenPdfBase == null) return;

        var extracted = 0;

        await Task.Run(() =>
        {
            var pdfCount = 0;
            foreach (var entry in hiddenPdfBase.Entries.Where(e => e.ArchiveType == "pdf" && !string.IsNullOrEmpty(e.FilePath)))
            {
                pdfCount++;
                List<PdfImage> images;
                try
                {
                    images = _pdfImageExtractor.Extract(entry.FilePath!);
                    LogSeed($"  [{pdfCount}] {Path.GetFileName(entry.FilePath)} → {images.Count} 张");
                }
                catch (Exception ex)
                {
                    LogSeed($"  [{pdfCount}] {Path.GetFileName(entry.FilePath)} → 异常: {ex.Message}");
                    continue;
                }
                foreach (var img in images)
                {
                    try
                    {
                        var archived = Path.Combine(imageDir, img.FileName);
                        if (!File.Exists(archived))
                        {
                            Directory.CreateDirectory(imageDir);
                            File.WriteAllBytes(archived, img.Data);
                        }

                        // 标题优先用题注（图2-1 打磨开窗与错层示意），保证图文对应；无题注回退为"来源·附图NN"
                        var caption = img.Caption;
                        var title = !string.IsNullOrWhiteSpace(caption)
                            ? caption
                            : $"{img.SourceTitle}·附图{Path.GetFileNameWithoutExtension(img.FileName)[^2..]}";

                        kb.Entries.Add(new KnowledgeEntry
                        {
                            Id = "img_" + Path.GetFileNameWithoutExtension(img.FileName),
                            Title = title,
                            Tags = [imageTag],
                            // 题注写入 content，参与关键词检索（如"打磨""开窗"）
                            Content = caption ?? "",
                            ArchiveType = "image",
                            FilePath = archived,
                            IsBuiltin = true,
                        });
                        extracted++;
                    }
                    catch
                    {
                        // 单张图失败跳过
                    }
                }
            }
        });

        LogSeed($"图片知识库提取完成：{extracted} 张（{imageTag}：题注标题 + 公章过滤）");
        foreach (var err in PdfImageExtractor.LastErrors.Take(15))
            LogSeed("  [提取诊断] " + err);
    }

    /// <summary>
    /// 增量登记图片目录中的新增图片（幂等，只追加不重建不删除）。
    /// 命名约定：编号_缺陷类型_来源_序号.png/jpg（下划线分隔，缺陷类型参与检索），
    /// 用于接入 AI 生成的补充图（docs/图片库补充建议清单.csv）。
    /// </summary>
    private async Task SyncNewImagesAsync(KnowledgeBaseInfo kb, string imageDir)
    {
        if (!Directory.Exists(imageDir)) return;

        var knownFiles = kb.Entries
            .Where(e => e.ArchiveType == "image" && !string.IsNullOrEmpty(e.FilePath))
            .Select(e => Path.GetFileName(e.FilePath!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownIds = kb.Entries.Select(e => e.Id).ToHashSet();

        var added = 0;
        foreach (var file in Directory.GetFiles(imageDir)
                     .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var entryId = "img_" + stem;
            if (knownFiles.Contains(fileName) || knownIds.Contains(entryId)) continue;

            var parts = stem.Split('_');
            var defect = parts.Length > 1 ? parts[1] : "";
            var site = parts.Length > 2 ? parts[2] : "";

            kb.Entries.Add(new KnowledgeEntry
            {
                Id = entryId,
                Title = stem.Replace('_', ' '),
                Tags = new List<string> { "施工参考图v11", "补充图片" }
                    .Concat(new[] { defect, site }.Where(t => t.Length > 0))
                    .Distinct().ToList(),
                Content = $"{defect} {site}".Trim(),
                ArchiveType = "image",
                FilePath = file,
                IsBuiltin = true,
            });
            knownIds.Add(entryId);
            added++;
        }

        if (added > 0)
        {
            Save();
            LogSeed($"图片知识库增量登记完成：新增 {added} 张（目录新增图片，含 AI 补充图）");
        }
    }

    /// <summary>种子日志：%AppData%/叶片缺陷审核/kb_seed.log。</summary>
    private static void LogSeed(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(DataPath)!, "kb_seed.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响启动
        }
    }

    /// <summary>
    /// 叶片维修知识库（可见内置库）：SeedKB/叶片维修知识库.json 的每个顶层节转为一条知识条目，
    /// 主文档 docx 提取文本后作为单独条目。
    /// </summary>
    private async Task SeedLeafRepairBaseAsync()
    {
        var seedDir = Path.Combine(AppContext.BaseDirectory, "SeedKB");
        var jsonPath = Path.Combine(seedDir, "叶片维修知识库.json");
        if (!File.Exists(jsonPath)) return;

        var kb = GetOrCreateBase("builtin_leaf_repair", "叶片维修知识库", isBuiltin: true);
        var knownIds = kb.Entries.Select(e => e.Id).ToHashSet();

        // 1) JSON 分节入库
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        foreach (var section in doc.RootElement.EnumerateObject())
        {
            var entryId = $"leaf_repair_{section.Name}";
            if (knownIds.Contains(entryId)) continue;

            kb.Entries.Add(new KnowledgeEntry
            {
                Id = entryId,
                Title = $"叶片维修知识库·{section.Name}",
                Tags = ["叶片维修", "分级标准", section.Name],
                Content = section.Value.GetRawText(),
                ArchiveType = "text",
                IsBuiltin = true,
            });
        }

        // 2) 主文档 docx
        var docxPath = Path.Combine(seedDir, "叶片维修知识库_主文档(1).docx");
        if (File.Exists(docxPath) && !knownIds.Contains("leaf_repair_master_doc"))
        {
            var imported = await _importer.ImportAsync(docxPath);
            if (imported.Ok)
            {
                kb.Entries.Add(new KnowledgeEntry
                {
                    Id = "leaf_repair_master_doc",
                    Title = "叶片维修知识库·主文档",
                    Tags = ["叶片维修", "主文档"],
                    Content = imported.Content,
                    ArchiveType = "text",
                    IsBuiltin = true,
                });
            }
        }

        Save();
    }

    /// <summary>
    /// 叶片维修案例库（隐藏内置库）：SeedKB/HiddenKB 下的 PDF 案例逐个存档，
    /// 仅登记标题（正文检索命中后按需识别）；隐藏库不参与界面展示与知识库页检索。
    /// </summary>
    private void SeedHiddenCaseBase()
    {
        var seedDir = Path.Combine(AppContext.BaseDirectory, "SeedKB", "HiddenKB");
        if (!Directory.Exists(seedDir)) return;

        var kb = GetOrCreateBase("builtin_hidden_cases", "叶片维修案例库（内置隐藏）", isBuiltin: true, hidden: true);
        var knownIds = kb.Entries.Select(e => e.Id).ToHashSet();

        // 优先按离线文本缓存索引建条目（text_index.json → txt/）：PDF 原件已随包去除，
        // 文本直读即全文，无需识别；FilePath 为空表示无原件（内容已预填，不会再触发识别）
        var addedFromIndex = 0;
        var indexPath = Path.Combine(seedDir, "text_index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
                foreach (var item in index.RootElement.GetProperty("files").EnumerateArray())
                {
                    var pdfName = item.GetProperty("pdf").GetString() ?? "";
                    var rel = item.GetProperty("txt").GetString() ?? "";
                    if (pdfName.Length == 0 || rel.Length == 0) continue;

                    var entryId = "hidden_" + Path.GetFileNameWithoutExtension(pdfName).Replace(" ", "_");
                    if (knownIds.Contains(entryId)) continue;

                    var txtPath = Path.Combine(seedDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(txtPath)) continue;
                    var content = File.ReadAllText(txtPath);
                    if (content.Length < 200) continue;

                    kb.Entries.Add(new KnowledgeEntry
                    {
                        Id = entryId,
                        Title = Path.GetFileNameWithoutExtension(pdfName),
                        Tags = ["维修案例"],
                        Content = content,
                        ArchiveType = "pdf",
                        FilePath = "", // 无原件：文本已预填，检索/生成直接使用
                        IsBuiltin = true,
                    });
                    knownIds.Add(entryId);
                    addedFromIndex++;
                }
                if (addedFromIndex > 0) LogSeed($"隐藏案例库：{addedFromIndex} 份按离线文本缓存登记（无 PDF 原件）");
            }
            catch (Exception ex)
            {
                LogSeed("隐藏案例库文本索引登记失败：" + ex.Message);
            }
        }

        foreach (var file in Directory.EnumerateFiles(seedDir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".pdf" or ".docx" or ".doc")) continue;

            var entryId = "hidden_" + Path.GetFileNameWithoutExtension(file).Replace(" ", "_");
            if (knownIds.Contains(entryId)) continue;

            // 拷贝到归档目录，脱离程序目录引用
            var archived = Path.Combine(_importer.ArchiveDirectory, "hidden_seed",
                Path.GetFileName(file));
            if (!File.Exists(archived))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
                File.Copy(file, archived);
            }

            string content = "";
            var archiveType = "pdf";
            if (ext == ".docx")
            {
                // docx 可直接提取文本
                try
                {
                    var imported = _importer.ImportAsync(archived).GetAwaiter().GetResult();
                    content = imported.Ok ? imported.Content : "";
                }
                catch
                {
                    content = "";
                }
            }
            else if (ext == ".doc")
            {
                // 旧版 .doc 无法程序化提取，仅存档、按标题检索
                archiveType = "pdf";
            }
            else if (ext == ".pdf")
            {
                // 离线文本缓存直读（text_index.json → txt/，随程序分发）：
                // 启动即载入全文，零识别成本、零 LLM 调用
                content = TryLoadIndexedPdfText(Path.GetFileName(file)) ?? "";
            }

            kb.Entries.Add(new KnowledgeEntry
            {
                Id = entryId,
                Title = Path.GetFileNameWithoutExtension(file),
                Tags = ["维修案例"],
                Content = content,
                ArchiveType = archiveType,
                FilePath = archived,
                IsBuiltin = true,
            });
        }
    }

    private KnowledgeBaseInfo GetOrCreateBase(string id, string name, bool isBuiltin, bool hidden = false)
    {
        var existing = _bases.FirstOrDefault(b => b.Id == id);
        if (existing != null)
        {
            // 隐藏标记以代码为准（自愈：历史数据中标记缺失时强制纠正）
            if (existing.Hidden != hidden)
            {
                existing.Hidden = hidden;
            }
            return existing;
        }

        var kb = new KnowledgeBaseInfo { Id = id, Name = name, IsBuiltin = isBuiltin, Hidden = hidden };
        _bases.Add(kb);
        return kb;
    }

    public KnowledgeBaseInfo CreateBase(string name)
    {
        var kb = new KnowledgeBaseInfo { Name = string.IsNullOrWhiteSpace(name) ? $"知识库{_bases.Count + 1}" : name.Trim() };
        _bases.Add(kb);
        Save();
        return kb;
    }

    public bool DeleteBase(string baseId)
    {
        var kb = _bases.FirstOrDefault(b => b.Id == baseId);
        if (kb == null || kb.IsBuiltin) return false;
        _bases.Remove(kb);
        Save();
        return true;
    }

    public async Task<KnowledgeEntry?> ImportAsync(string baseId, string filePath, CancellationToken ct = default)
    {
        var kb = _bases.FirstOrDefault(b => b.Id == baseId);
        if (kb == null) return null;

        var result = await _importer.ImportAsync(filePath, ct);
        if (!result.Ok) throw new InvalidOperationException(result.Message);

        var entry = new KnowledgeEntry
        {
            Title = result.Title,
            Tags = [],
            Content = result.Content,
            ArchiveType = result.ArchiveType,
            FilePath = result.FilePath,
        };
        kb.Entries.Add(entry);
        Save();
        return entry;
    }

    public bool DeleteEntry(string baseId, string entryId)
    {
        var kb = _bases.FirstOrDefault(b => b.Id == baseId);
        var entry = kb?.Entries.FirstOrDefault(e => e.Id == entryId);
        if (kb == null || entry == null || entry.IsBuiltin) return false;
        kb.Entries.Remove(entry);
        Save();
        return true;
    }

    public List<SearchResultItem> Search(string keyword, IEnumerable<string>? baseIds = null, int limit = 6,
        bool includeHidden = false)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return [];

        var idSet = baseIds?.ToHashSet();
        var terms = ExtractTerms(keyword);
        if (terms.Count == 0) return [];

        var best = new Dictionary<string, (KnowledgeEntry Entry, int Score, string Snippet)>();

        foreach (var kb in _bases.Where(b =>
                     (idSet == null || idSet.Count == 0 || idSet.Contains(b.Id))
                     && (includeHidden || !b.Hidden)))
        {
            foreach (var entry in kb.Entries)
            {
                int total = 0;
                string snippet = "";

                foreach (var term in terms)
                {
                    var weight = Math.Min(3, term.Length); // 短词权重低，避免常用二字词淹没
                    var titleHit = entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
                    var tagHit = entry.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));

                    var score = 0;
                    if (titleHit) score += 50 * weight;
                    if (tagHit) score += 20 * weight;

                    if (entry.Content.Length > 0)
                    {
                        var hits = CountOccurrences(entry.Content, term);
                        score += Math.Min(hits, 10) * 5 * weight;
                        if (hits > 0 && snippet.Length == 0)
                        {
                            var idx = entry.Content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                            var start = Math.Max(0, idx - 40);
                            var len = Math.Min(220, entry.Content.Length - start);
                            snippet = entry.Content.Substring(start, len).ReplaceLineEndings(" ");
                        }
                    }
                    total += score;
                }

                if (total > 0 && !best.ContainsKey(entry.Id))
                {
                    if (snippet.Length == 0 && entry.ArchiveType == "pdf")
                        snippet = "（PDF 存档：内容将在使用时自动识别）";
                    best[entry.Id] = (entry, total, snippet);
                }
            }
        }

        return best.Values.OrderByDescending(r => r.Score).Take(limit)
            .Select(r => new SearchResultItem { Entry = r.Entry, Snippet = r.Snippet, Score = r.Score })
            .ToList();
    }

    /// <summary>
    /// 查询词切分：整句直接匹配几乎不可能命中知识条目，
    /// 按标点/空白切分为短语，并对长中文短语补 2 字滑窗词根兜底。
    /// </summary>
    private static List<string> ExtractTerms(string query)
    {
        var phrases = System.Text.RegularExpressions.Regex
            .Split(query, @"[，。；：！？、\n\r\t ,.;:!?()\[\]【】<>""'…\-—/\\|]+")
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();

        var terms = new List<string>(phrases);
        foreach (var phrase in phrases.Where(p => p.Length > 4))
        {
            for (var i = 0; i + 2 <= phrase.Length; i++)
            {
                var gram = phrase.Substring(i, 2);
                if (gram.Any(char.IsWhiteSpace)) continue;
                terms.Add(gram);
            }
        }
        return terms.Distinct().ToList();
    }

    public async Task<string> GetContentAsync(KnowledgeEntry entry, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        if (entry.ArchiveType != "pdf" || !string.IsNullOrEmpty(entry.Content))
            return entry.Content;
        return await EnsureContentAsync(entry, log, ct);
    }

    public async Task<string> EnsureContentAsync(KnowledgeEntry entry, IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        if (entry.ArchiveType != "pdf") return entry.Content;
        if (!string.IsNullOrEmpty(entry.Content)) return entry.Content;
        if (string.IsNullOrEmpty(entry.FilePath) || !File.Exists(entry.FilePath))
            throw new InvalidOperationException("PDF 原件缺失，无法识别");

        var result = await _pdfRecognizer.RecognizeAsync(entry.FilePath, log, ct);
        entry.Content = result.FullText;
        entry.Updated = DateTime.Now;
        Save();
        return entry.Content;
    }

    public void Save()
    {
        _saveLock.Wait();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DataPath)!);
            File.WriteAllText(DataPath, JsonSerializer.Serialize(_bases, JsonOpts));
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static int CountOccurrences(string text, string keyword)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += keyword.Length;
        }
        return count;
    }

    // ─────────────── 种子反序列化 DTO ───────────────

    private class SeedFile
    {
        public List<SeedBase>? KnowledgeBases { get; set; }
    }

    private class SeedBase
    {
        public string Id { get; set; } = "";
        public List<SeedDoc>? Docs { get; set; }
    }

    private class SeedDoc
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Content { get; set; }
        public string? Tags { get; set; }
    }
}
