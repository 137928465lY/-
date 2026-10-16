using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NPOI.OpenXmlFormats.Wordprocessing;
using NPOI.XWPF.UserModel;

namespace 叶片缺陷审核.Services;



public class DocumentModifyService 
{
    public async Task AppendAuditResultAsync(string docxPath, string auditResult)
    {
        await Task.Run(() =>
        {
            // 移除只读属性
            if (File.GetAttributes(docxPath).HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(docxPath, File.GetAttributes(docxPath) & ~FileAttributes.ReadOnly);
            }

            // 先读取文档到内存
            byte[] fileBytes = File.ReadAllBytes(docxPath);

            // 从内存中打开文档
            using var ms = new MemoryStream(fileBytes);
            var doc = new XWPFDocument(ms);

            // 添加分页符
            var pageBreakPara = doc.CreateParagraph();
            var pageBreakRun = pageBreakPara.CreateRun();
            pageBreakRun.GetCTR().AddNewBr().type = ST_BrType.page;

            // 添加审核意见标题
            var titlePara = doc.CreateParagraph();
            var titleRun = titlePara.CreateRun();
            titleRun.SetText("AI 审核意见");
            titleRun.IsBold = true;
            titleRun.FontSize = 16;

            // 添加审核意见内容（黄底黑字）
            var lines = auditResult.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.CreateParagraph();
                    continue;
                }

                var para = doc.CreateParagraph();
                var run = para.CreateRun();
                run.SetText(line);
                run.FontSize = 12;
                run.SetColor("000000"); // 黑字

                // 设置黄底
                var rPr = run.GetCTR().IsSetRPr() ? run.GetCTR().rPr : run.GetCTR().AddNewRPr();
                var shd = rPr.shd ?? rPr.AddNewShd();
                shd.val = ST_Shd.clear;
                shd.fill = "FFFF00"; // 黄色背景
            }

            // 先写入临时文件，成功后再替换原文件（防止写入失败导致原文件丢失）
            var tempPath = Path.GetTempFileName();
            try
            {
                using (var writeFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    doc.Write(writeFs);
                }
                File.Copy(tempPath, docxPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        });
    }

    public async Task<string> AppendAuditResultWithBookmarksAsync(string docxPath, string auditResult, Action<string>? logCallback = null)
    {
        return await Task.Run(() =>
        {
            void Log(string msg) => logCallback?.Invoke(msg);

            // 移除只读属性
            if (File.GetAttributes(docxPath).HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(docxPath, File.GetAttributes(docxPath) & ~FileAttributes.ReadOnly);
            }

            // 先读取文档到内存
            byte[] fileBytes = File.ReadAllBytes(docxPath);

            // 从内存中打开文档
            using var ms = new MemoryStream(fileBytes);
            var doc = new XWPFDocument(ms);

            // Step 1: 解析审核结果（支持 JSON 和纯文本两种格式）
            var auditData = ParseAuditResult(auditResult);
            var issues = auditData.Issues;

            Log($"[Audit] 解析到 {issues.Count} 个问题");

            // 调试：输出所有段落的文字（限制前50个字符）
            Log($"[Audit] 文档共 {doc.Paragraphs.Count} 个段落");
            for (int p = 0; p < doc.Paragraphs.Count; p++)
            {
                var paraText = doc.Paragraphs[p].Text;
                if (!string.IsNullOrWhiteSpace(paraText))
                {
                    var display = paraText.Length > 60 ? paraText.Substring(0, 60) + "..." : paraText;
                    display = display.Replace("\n", "\\n").Replace("\r", "\\r");
                    Log($"[Audit] 段落{p}: [{display}]");
                }
            }

            foreach (var issue in issues)
            {
                Log($"[Audit] 原文: {issue.OriginalText.Replace("\n", "\\n")}");
            }

            // Step 2: 在原文档中查找有问题的文字，标记为红底黑字，并创建书签
            var bookmarkNames = new List<string>();
            var bookmarkIds = new List<string>();
            int bookmarkCounter = 1;

            // 收集所有段落（顶级段落 + 表格段落），现在都可以修改了
            var allParagraphs = new List<XWPFParagraph>();
            allParagraphs.AddRange(doc.Paragraphs);

            // 收集表格段落
            int tblIdx = 0;
            foreach (var table in doc.Tables)
            {
                int rowIdx = 0;
                foreach (var row in table.Rows)
                {
                    int cellIdx = 0;
                    foreach (var cell in row.GetTableCells())
                    {
                        foreach (var para in cell.Paragraphs)
                        {
                            allParagraphs.Add(para);
                        }
                        cellIdx++;
                    }
                    rowIdx++;
                }
                tblIdx++;
            }

            Log($"[Audit] 总段落数: {allParagraphs.Count}");

            foreach (var issue in issues)
            {
                if (string.IsNullOrWhiteSpace(issue.OriginalText)) continue;

                // 清理原文：去除多余空格和换行
                var cleanOriginal = Regex.Replace(issue.OriginalText, @"\s+", " ").Trim();
                bool found = false;

                // 如果原文包含换行，说明可能跨多个单元格，尝试分段匹配
                var parts = cleanOriginal.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                // 在所有段落中搜索（包括顶级段落和表格段落）
                foreach (var para in allParagraphs)
                {
                    var paraText = para.Text;
                    if (string.IsNullOrEmpty(paraText)) continue;

                    var cleanParaText = Regex.Replace(paraText, @"\s+", " ").Trim();

                    try
                    {
                        var matchResult = TryMatchAndMark(para, cleanParaText, cleanOriginal, parts, bookmarkCounter, Log);
                        if (matchResult != null)
                        {
                            bookmarkNames.Add(matchResult.Value.BookmarkName);
                            bookmarkIds.Add(matchResult.Value.BookmarkId);
                            bookmarkCounter++;
                            found = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Audit] 标记时出错: {ex.Message}");
                    }
                }

                // 如果整段没找到，按关键词搜索分段
                if (!found)
                {
                    // 提取关键词：去除编号前缀，取有意义的部分
                    var searchTerms = new List<string>();
                    
                    // 如果有冒号，取冒号后的内容
                    if (cleanOriginal.Contains("："))
                    {
                        var afterColon = cleanOriginal.Split(new[] { '：' }, 2)[1].Trim();
                        if (afterColon.Length >= 3) searchTerms.Add(afterColon);
                    }
                    
                    // 去除"维修过程描述X："、"质量控制X："等前缀
                    var stripped = Regex.Replace(cleanOriginal, @"^(维修过程描述|质量控制|施工内容|拍摄时间|叶片重量|施工人员|施工日期|机位号|叶片套号|生产日期)\d*[：:\s]*", "").Trim();
                    if (stripped.Length >= 3 && !searchTerms.Contains(stripped))
                    {
                        searchTerms.Add(stripped);
                    }
                    
                    // 也添加原文本身
                    if (cleanOriginal.Length >= 3) searchTerms.Add(cleanOriginal);
                    
                    // 取原文中的分段
                    foreach (var part in parts)
                    {
                        if (part.Length >= 3 && !searchTerms.Contains(part))
                        {
                            searchTerms.Add(part);
                        }
                    }

                    // 在所有段落中按关键词搜索
                    foreach (var para in allParagraphs)
                    {
                        var paraText = para.Text?.Trim();
                        if (string.IsNullOrEmpty(paraText)) continue;

                        var cleanParaText = Regex.Replace(paraText, @"\s+", " ").Trim();

                        foreach (var term in searchTerms)
                        {
                            if (cleanParaText.Contains(term))
                            {
                                try
                                {
                                    // 使用 TryMatchAndMark 标记红底并创建书签
                                    var matchResult = TryMatchAndMark(para, cleanParaText, term, new[] { term }, bookmarkCounter, Log);
                                    if (matchResult != null)
                                    {
                                        bookmarkNames.Add(matchResult.Value.BookmarkName);
                                        bookmarkIds.Add(matchResult.Value.BookmarkId);
                                        bookmarkCounter++;
                                        found = true;
                                        Log($"[Audit] 关键词匹配并标记: '{term}'");
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log($"[Audit] 关键词标记出错: {ex.Message}");
                                }
                            }
                        }
                        if (found) break;
                    }
                }

                if (!found)
                {
                    Log($"[Audit] 未找到匹配: '{cleanOriginal}'");
                }
            }

            Log($"[Audit] 共标记 {bookmarkNames.Count} 个书签");

            // Step 3: 添加分页符
            try
            {
                var pageBreakPara = doc.CreateParagraph();
                var pageBreakRun = pageBreakPara.CreateRun();
                pageBreakRun.GetCTR().AddNewBr().type = ST_BrType.page;
                Log($"[Audit] Step 3 完成: 分页符");
            }
            catch (Exception ex)
            {
                Log($"[Audit] Step 3 出错: {ex.Message}");
            }

            // Step 4: 添加审核意见标题
            try
            {
                var titlePara = doc.CreateParagraph();
                var titleRun = titlePara.CreateRun();
                titleRun.SetText("AI 审核意见");
                titleRun.IsBold = true;
                titleRun.FontSize = 16;
                Log($"[Audit] Step 4 完成: 标题");
            }
            catch (Exception ex)
            {
                Log($"[Audit] Step 4 出错: {ex.Message}");
            }

            // Step 5: 添加审核意见摘要
            try
            {
                if (!string.IsNullOrWhiteSpace(auditData.Summary))
                {
                    var summaryPara = doc.CreateParagraph();
                    var summaryRun = summaryPara.CreateRun();
                    summaryRun.SetText(auditData.Summary);
                    summaryRun.FontSize = 12;
                    Log($"[Audit] Step 5 完成: 摘要");
                }
            }
            catch (Exception ex)
            {
                Log($"[Audit] Step 5 出错: {ex.Message}");
            }

            // Step 6: 添加详细审核意见（带超链接指向原文标记处）
            try
            {
                if (issues.Count > 0)
                {
                    for (int i = 0; i < issues.Count; i++)
                    {
                        var issue = issues[i];

                        // 空行分隔
                        doc.CreateParagraph();

                        // 原文引用（带超链接跳转到原文书签处）
                        var originalPara = doc.CreateParagraph();
                        if (i < bookmarkNames.Count)
                        {
                            // 创建可点击的超链接跳转到书签
                            var hyperlink = BuildHyperlink(bookmarkNames[i], $"【原文】{issue.OriginalText}");
                            originalPara.GetCTP().Items.Add(hyperlink);
                            originalPara.GetCTP().ItemsElementName.Add(ParagraphItemsChoiceType.hyperlink);
                        }
                        else
                        {
                            var originalRun = originalPara.CreateRun();
                            originalRun.SetText($"【原文】{issue.OriginalText}");
                            originalRun.FontSize = 12;
                        }

                        // 问题描述
                        var issuePara = doc.CreateParagraph();
                        var issueRun = issuePara.CreateRun();
                        issueRun.SetText($"【问题】{issue.Issue}");
                        issueRun.FontSize = 12;
                        issueRun.SetColor("FF0000"); // 红字

                        // 修改建议（黄底）
                        if (!string.IsNullOrWhiteSpace(issue.Suggestion))
                        {
                            var suggestionPara = doc.CreateParagraph();
                            var suggestionRun = suggestionPara.CreateRun();
                            suggestionRun.SetText($"【建议】{issue.Suggestion}");
                            suggestionRun.FontSize = 12;
                            suggestionRun.SetColor("000000");

                            var rPr = suggestionRun.GetCTR().IsSetRPr() ? suggestionRun.GetCTR().rPr : suggestionRun.GetCTR().AddNewRPr();
                            var shd = rPr.shd ?? rPr.AddNewShd();
                            shd.val = ST_Shd.clear;
                            shd.fill = "FFFF00";
                        }
                    }
                    Log($"[Audit] Step 6 完成: 详细审核意见 {issues.Count} 条");
                }
            }
            catch (Exception ex)
            {
                Log($"[Audit] Step 6 出错: {ex.Message}");
            }

            // 先写入临时文件，成功后再替换原文件（防止写入失败导致原文件丢失）
            var tempPath = Path.GetTempFileName();
            var savedPath = docxPath; // 默认保存路径
            try
            {
                Log($"[Audit] 开始写入临时文件...");
                using (var writeFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    doc.Write(writeFs);
                }
                Log($"[Audit] 写入临时文件完成");

                // 尝试替换原文件
                try
                {
                    File.Copy(tempPath, docxPath, overwrite: true);
                    savedPath = docxPath;
                    Log($"[Audit] 已保存到原文件: {docxPath}");
                }
                catch (UnauthorizedAccessException)
                {
                    // 原文件被锁定，保存到同目录下带后缀的新文件
                    var dir = Path.GetDirectoryName(docxPath) ?? ".";
                    var baseName = Path.GetFileNameWithoutExtension(docxPath);
                    var ext = Path.GetExtension(docxPath);
                    savedPath = Path.Combine(dir, $"{baseName}_已审核{ext}");
                    File.Copy(tempPath, savedPath, overwrite: true);
                    Log($"[Audit] 原文件被占用，已保存到: {savedPath}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Audit] 写入文件出错: {ex.Message}");
                Log($"[Audit] 堆栈: {ex.StackTrace}");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            return savedPath;
        });
    }

    /// <summary>
    /// 提取关键词：从原文中取前 N 个有意义的字符
    /// </summary>
    private string ExtractKeywords(string text, int maxLen)
    {
        // 去除标点符号和空格，只保留文字部分
        var cleaned = Regex.Replace(text, @"[\s\p{P}]+", "").Trim();
        if (cleaned.Length <= maxLen) return cleaned;
        return cleaned.Substring(0, maxLen);
    }

    /// <summary>
    /// 模糊匹配：在段落文字中查找与原文最长的公共子串
    /// </summary>
    private string FindBestMatch(string paraText, string originalText, int minLen)
    {
        // 先尝试找包含关系
        if (paraText.Contains(originalText)) return originalText;

        // 尝试从原文中取不同长度的子串去匹配
        for (int len = Math.Min(originalText.Length, 20); len >= minLen; len--)
        {
            for (int start = 0; start <= originalText.Length - len; start++)
            {
                var sub = originalText.Substring(start, len);
                var cleanSub = Regex.Replace(sub, @"\s+", " ").Trim();
                var idx = paraText.IndexOf(cleanSub, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    // 找到了，尝试扩展匹配
                    return ExpandMatch(paraText, cleanSub, idx, originalText, start, len);
                }
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// 扩展匹配：在找到子串后，尝试向两边扩展
    /// </summary>
    private string ExpandMatch(string paraText, string matchedSub, int paraIdx, string originalText, int origStart, int origLen)
    {
        // 尝试向右扩展
        var result = matchedSub;
        int paraPos = paraIdx + matchedSub.Length;
        int origPos = origStart + origLen;

        while (origPos < originalText.Length && paraPos < paraText.Length)
        {
            var nextChar = originalText[origPos].ToString();
            if (paraText.Substring(paraPos).StartsWith(nextChar))
            {
                result += nextChar;
                paraPos++;
                origPos++;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 在段落中查找文字的实际位置（考虑多个 run）
    /// </summary>
    private int FindTextInParagraph(XWPFParagraph para, string searchText)
    {
        var ctPara = para.GetCTP();
        var items = ctPara.Items;
        int currentPos = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is CT_R run)
            {
                var runText = GetRunText(run);
                if (!string.IsNullOrEmpty(runText))
                {
                    var index = runText.IndexOf(searchText, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        return currentPos + index;
                    }
                    currentPos += runText.Length;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// 构建一个可点击的内部超链接，跳转到指定书签
    /// </summary>
    private CT_Hyperlink1 BuildHyperlink(string bookmarkName, string displayText)
    {
        var hyperlink = new CT_Hyperlink1();
        hyperlink.anchor = bookmarkName;
        
        // 创建 run 元素
        var run = new CT_R();
        run.Items = new System.Collections.ArrayList();
        run.ItemsElementName = new List<RunItemsChoiceType>();
        
        // 添加文本
        run.Items.Add(new CT_Text { Value = displayText });
        run.ItemsElementName.Add(RunItemsChoiceType.t);
        
        // 设置蓝色下划线样式
        var rPr = new CT_RPr();
        rPr.AddNewColor().val = "0000FF";
        rPr.AddNewU().val = ST_Underline.single;
        rPr.AddNewSz().val = 24; // 12pt * 2
        run.rPr = rPr;
        
        hyperlink.Items = new System.Collections.ArrayList();
        hyperlink.ItemsElementName = new List<ItemsChoiceType12>();
        hyperlink.Items.Add(run);
        hyperlink.ItemsElementName.Add(ItemsChoiceType12.r);
        
        return hyperlink;
    }

    private void AddBookmarkToParagraph(XWPFParagraph para, string bookmarkName, string bookmarkId)
    {
        var p = para.GetCTP();
        
        // 创建 bookmarkStart
        var bookmarkStart = new CT_Bookmark();
        bookmarkStart.id = bookmarkId;
        bookmarkStart.name = bookmarkName;
        
        // 创建 bookmarkEnd
        var bookmarkEnd = new CT_MarkupRange();
        bookmarkEnd.id = bookmarkId;
        
        // 将书签添加到段落开头
        if (p.Items == null)
        {
            p.Items = new System.Collections.ArrayList();
            p.ItemsElementName = new List<ParagraphItemsChoiceType>();
        }
        
        // 在开头插入 bookmarkStart
        p.Items.Insert(0, bookmarkStart);
        p.ItemsElementName.Insert(0, ParagraphItemsChoiceType.bookmarkStart);
        
        // 在末尾插入 bookmarkEnd
        p.Items.Add(bookmarkEnd);
        p.ItemsElementName.Add(ParagraphItemsChoiceType.bookmarkEnd);
    }

    /// <summary>
    /// 尝试在段落中匹配并标记文字，返回书签信息或 null
    /// </summary>
    private (string BookmarkName, string BookmarkId)? TryMatchAndMark(XWPFParagraph para, string cleanParaText, string cleanOriginal, string[] parts, int bookmarkCounter, Action<string> log)
    {
        // 策略1：精确匹配
        var index = cleanParaText.IndexOf(cleanOriginal, StringComparison.Ordinal);
        if (index >= 0)
        {
            var actualIndex = FindTextInParagraph(para, cleanOriginal);
            if (actualIndex >= 0)
            {
                var bookmarkName = $"AuditIssue_{bookmarkCounter}";
                var bookmarkId = bookmarkCounter.ToString();
                log($"[Audit] 精确匹配成功: '{cleanOriginal}'");
                MarkTextInParagraph(para, cleanOriginal, actualIndex, bookmarkName, bookmarkId);
                return (bookmarkName, bookmarkId);
            }
        }

        // 策略2：分段匹配
        if (parts.Length > 1)
        {
            var longestPart = parts.OrderByDescending(p => p.Length).First();
            if (longestPart.Length >= 3)
            {
                var partIndex = cleanParaText.IndexOf(longestPart, StringComparison.Ordinal);
                if (partIndex >= 0)
                {
                    var actualIndex = FindTextInParagraph(para, longestPart);
                    if (actualIndex >= 0)
                    {
                        var bookmarkName = $"AuditIssue_{bookmarkCounter}";
                        var bookmarkId = bookmarkCounter.ToString();
                        log($"[Audit] 分段匹配成功: '{longestPart}'");
                        MarkTextInParagraph(para, longestPart, actualIndex, bookmarkName, bookmarkId);
                        return (bookmarkName, bookmarkId);
                    }
                }
            }
        }

        // 策略3：关键词匹配
        if (cleanOriginal.Length > 6)
        {
            var keywords = ExtractKeywords(cleanOriginal, 10);
            if (keywords.Length >= 4)
            {
                var keyIndex = cleanParaText.IndexOf(keywords, StringComparison.Ordinal);
                if (keyIndex >= 0)
                {
                    var actualIndex = FindTextInParagraph(para, keywords);
                    if (actualIndex >= 0)
                    {
                        var bookmarkName = $"AuditIssue_{bookmarkCounter}";
                        var bookmarkId = bookmarkCounter.ToString();
                        log($"[Audit] 关键词匹配成功: '{keywords}'");
                        MarkTextInParagraph(para, keywords, actualIndex, bookmarkName, bookmarkId);
                        return (bookmarkName, bookmarkId);
                    }
                }
            }
        }

        // 策略4：模糊匹配
        if (cleanOriginal.Length > 4)
        {
            var bestMatch = FindBestMatch(cleanParaText, cleanOriginal, 6);
            if (!string.IsNullOrEmpty(bestMatch) && bestMatch.Length >= 4)
            {
                var actualIndex = FindTextInParagraph(para, bestMatch);
                if (actualIndex >= 0)
                {
                    var bookmarkName = $"AuditIssue_{bookmarkCounter}";
                    var bookmarkId = bookmarkCounter.ToString();
                    log($"[Audit] 模糊匹配成功: '{bestMatch}'");
                    MarkTextInParagraph(para, bestMatch, actualIndex, bookmarkName, bookmarkId);
                    return (bookmarkName, bookmarkId);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 在段落中查找并标记指定文字为红底黑字，并添加书签
    /// </summary>
    private void MarkTextInParagraph(XWPFParagraph para, string targetText, int startIndex, string bookmarkName, string bookmarkId)
    {
        var ctPara = para.GetCTP();
        var items = ctPara.Items;
        var itemsElementName = ctPara.ItemsElementName;

        // 收集段落中所有 run 的文字，构建位置映射
        var runPositions = new List<(int Start, int End, CT_R Run)>();
        int currentPos = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is CT_R run)
            {
                var runText = GetRunText(run);
                if (!string.IsNullOrEmpty(runText))
                {
                    runPositions.Add((currentPos, currentPos + runText.Length, run));
                    currentPos += runText.Length;
                }
            }
        }

        // 找到目标文字覆盖的 run 范围
        var targetEnd = startIndex + targetText.Length;
        var affectedRuns = new List<(CT_R Run, int TextStartInRun, int TextLength)>();

        foreach (var rp in runPositions)
        {
            var overlapStart = Math.Max(rp.Start, startIndex);
            var overlapEnd = Math.Min(rp.End, targetEnd);

            if (overlapStart < overlapEnd)
            {
                var textInRunStart = overlapStart - rp.Start;
                var textLength = overlapEnd - overlapStart;
                affectedRuns.Add((rp.Run, textInRunStart, textLength));
            }
        }

        // 对每个受影响的 run，拆分并标记目标文字
        bool isFirst = true;
        foreach (var (run, textStart, textLength) in affectedRuns)
        {
            SplitAndMarkRun(para, run, textStart, textLength, isFirst ? bookmarkName : null, isFirst ? bookmarkId : null);
            isFirst = false;
        }
    }

    /// <summary>
    /// 拆分 run 并标记指定部分为红底黑字，可选添加书签
    /// </summary>
    private void SplitAndMarkRun(XWPFParagraph para, CT_R run, int textStart, int textLength, string? bookmarkName, string? bookmarkId)
    {
        var fullText = GetRunText(run);
        if (string.IsNullOrEmpty(fullText) || textStart + textLength > fullText.Length) return;

        var ctPara = para.GetCTP();
        var items = ctPara.Items;
        var itemsElementName = ctPara.ItemsElementName;

        int runIndex = items.IndexOf(run);
        if (runIndex < 0) return;

        var beforeText = fullText.Substring(0, textStart);
        var targetText = fullText.Substring(textStart, textLength);
        var afterText = fullText.Substring(textStart + textLength);

        // 移除原来的 run
        items.RemoveAt(runIndex);
        itemsElementName.RemoveAt(runIndex);

        // 插入 before run（保持原样式）
        if (!string.IsNullOrEmpty(beforeText))
        {
            var beforeRun = CloneRun(run);
            SetRunText(beforeRun, beforeText);
            items.Insert(runIndex, beforeRun);
            itemsElementName.Insert(runIndex, ParagraphItemsChoiceType.r);
            runIndex++;
        }

        // 插入 target run（红底黑字）
        var targetRun = CloneRun(run);
        SetRunText(targetRun, targetText);
        var rPr = targetRun.IsSetRPr() ? targetRun.rPr : targetRun.AddNewRPr();
        rPr.color = new CT_Color { val = "000000" }; // 黑字
        var shd = rPr.shd ?? rPr.AddNewShd();
        shd.val = ST_Shd.clear;
        shd.fill = "FF0000"; // 红底
        items.Insert(runIndex, targetRun);
        itemsElementName.Insert(runIndex, ParagraphItemsChoiceType.r);

        // 如果有书签名，在 target run 前后添加书签
        if (!string.IsNullOrEmpty(bookmarkName) && !string.IsNullOrEmpty(bookmarkId))
        {
            var bookmarkStart = new CT_Bookmark { name = bookmarkName, id = bookmarkId };
            items.Insert(runIndex, bookmarkStart);
            itemsElementName.Insert(runIndex, ParagraphItemsChoiceType.bookmarkStart);

            var bookmarkEnd = new CT_Bookmark { id = bookmarkId };
            items.Insert(runIndex + 1, bookmarkEnd);
            itemsElementName.Insert(runIndex + 1, ParagraphItemsChoiceType.bookmarkEnd);
        }

        runIndex++;

        // 插入 after run（保持原样式）
        if (!string.IsNullOrEmpty(afterText))
        {
            var afterRun = CloneRun(run);
            SetRunText(afterRun, afterText);
            items.Insert(runIndex, afterRun);
            itemsElementName.Insert(runIndex, ParagraphItemsChoiceType.r);
        }
    }

    private string GetRunText(CT_R run)
    {
        var texts = new List<string>();
        foreach (var item in run.Items)
        {
            if (item is CT_Text text)
            {
                texts.Add(text.Value ?? string.Empty);
            }
        }
        return string.Join("", texts);
    }

    private void SetRunText(CT_R run, string text)
    {
        // 确保 Items 初始化
        if (run.Items == null)
        {
            run.Items = new System.Collections.ArrayList();
        }
        
        var toRemoveIndices = new List<int>();
        for (int i = 0; i < run.Items.Count; i++)
        {
            if (run.Items[i] is CT_Text)
            {
                toRemoveIndices.Add(i);
            }
        }
        // 从后往前删除，避免索引变化
        toRemoveIndices.Reverse();
        foreach (var idx in toRemoveIndices)
        {
            run.Items.RemoveAt(idx);
            if (run.ItemsElementName != null && idx < run.ItemsElementName.Count)
            {
                run.ItemsElementName.RemoveAt(idx);
            }
        }
        run.Items.Add(new CT_Text { Value = text });
        if (run.ItemsElementName != null)
        {
            run.ItemsElementName.Add(RunItemsChoiceType.t);
        }
    }

    private CT_R CloneRun(CT_R run)
    {
        var newRun = new CT_R();
        
        // 复制 Items 和 ItemsElementName（用于正确序列化）
        if (run.Items != null)
        {
            newRun.Items = new System.Collections.ArrayList(run.Items);
        }
        if (run.ItemsElementName != null)
        {
            newRun.ItemsElementName = new List<RunItemsChoiceType>(run.ItemsElementName);
        }
        
        if (run.IsSetRPr())
        {
            var oldRPr = run.rPr;
            var newRPr = new CT_RPr();

            if (oldRPr.b != null) newRPr.b = new CT_OnOff { val = oldRPr.b.val };
            if (oldRPr.i != null) newRPr.i = new CT_OnOff { val = oldRPr.i.val };
            if (oldRPr.color != null) newRPr.color = new CT_Color { val = oldRPr.color.val };
            if (oldRPr.sz != null) newRPr.sz = new CT_HpsMeasure { val = oldRPr.sz.val };
            if (oldRPr.rFonts != null) newRPr.rFonts = new CT_Fonts { ascii = oldRPr.rFonts.ascii, hAnsi = oldRPr.rFonts.hAnsi, eastAsia = oldRPr.rFonts.eastAsia };
            if (oldRPr.highlight != null) newRPr.highlight = new CT_Highlight { val = oldRPr.highlight.val };
            if (oldRPr.u != null) newRPr.u = new CT_Underline { val = oldRPr.u.val };
            if (oldRPr.shd != null) newRPr.shd = new CT_Shd { val = oldRPr.shd.val, fill = oldRPr.shd.fill };
            if (oldRPr.vertAlign != null) newRPr.vertAlign = new CT_VerticalAlignRun { val = oldRPr.vertAlign.val };

            newRun.rPr = newRPr;
        }
        return newRun;
    }

    /// <summary>
    /// 解析审核结果（支持 JSON 和纯文本两种格式）
    /// </summary>
    private AuditResultData ParseAuditResult(string auditResult)
    {
        var result = new AuditResultData();

        // 先尝试 JSON 解析
        var jsonText = ExtractJson(auditResult);
        if (!string.IsNullOrEmpty(jsonText))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                if (root.TryGetProperty("summary", out var summaryProp))
                {
                    result.Summary = summaryProp.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("issues", out var issuesProp) && issuesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var issueElem in issuesProp.EnumerateArray())
                    {
                        var issue = new AuditIssue();
                        if (issueElem.TryGetProperty("originalText", out var ot))
                            issue.OriginalText = ot.GetString() ?? string.Empty;
                        if (issueElem.TryGetProperty("issue", out var iss))
                            issue.Issue = iss.GetString() ?? string.Empty;
                        if (issueElem.TryGetProperty("suggestion", out var sug))
                            issue.Suggestion = sug.GetString() ?? string.Empty;

                        if (!string.IsNullOrWhiteSpace(issue.OriginalText))
                        {
                            result.Issues.Add(issue);
                        }
                    }
                }

                if (result.Issues.Count > 0) return result;
            }
            catch
            {
                // JSON 解析失败，继续尝试纯文本解析
            }
        }

        // 纯文本格式解析：支持 【原文】...【问题】...【建议】... 结构
        ParsePlainText(auditResult, result);
        return result;
    }

    /// <summary>
    /// 解析纯文本格式的审核结果
    /// </summary>
    private void ParsePlainText(string text, AuditResultData result)
    {
        // 匹配模式：【原文】xxx \n 【问题】xxx \n 【建议】xxx
        var pattern = @"【原文】\s*(.+?)\s*【问题】\s*(.+?)\s*【建议】\s*(.+?)(?=【原文】|$)";
        var matches = Regex.Matches(text, pattern, RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var issue = new AuditIssue
            {
                OriginalText = match.Groups[1].Value.Trim(),
                Issue = match.Groups[2].Value.Trim(),
                Suggestion = match.Groups[3].Value.Trim()
            };

            if (!string.IsNullOrWhiteSpace(issue.OriginalText))
            {
                result.Issues.Add(issue);
            }
        }

        // 提取摘要（第一个【原文】之前的内容）
        var firstIssueIndex = text.IndexOf("【原文】");
        if (firstIssueIndex > 0)
        {
            result.Summary = text.Substring(0, firstIssueIndex).Trim();
            // 移除可能的标题
            if (result.Summary.StartsWith("AI审核意见") || result.Summary.StartsWith("AI 审核意见"))
            {
                result.Summary = result.Summary.Substring(result.Summary.IndexOf('\n') > 0 ? result.Summary.IndexOf('\n') + 1 : 0).Trim();
            }
        }
        else
        {
            result.Summary = text.Trim();
        }
    }

    /// <summary>
    /// 从文本中提取 JSON 内容
    /// </summary>
    private string ExtractJson(string text)
    {
        var startMarker = "```json";
        var endMarker = "```";

        var startIndex = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        if (startIndex >= 0)
        {
            startIndex += startMarker.Length;
            var endIndex = text.IndexOf(endMarker, startIndex);
            if (endIndex > startIndex)
            {
                return text.Substring(startIndex, endIndex - startIndex).Trim();
            }
        }

        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            return text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        return string.Empty;
    }
}

/// <summary>
/// 审核结果数据模型
/// </summary>
public class AuditResultData
{
    public string Summary { get; set; } = string.Empty;
    public List<AuditIssue> Issues { get; set; } = new();
}

/// <summary>
/// 审核问题项
/// </summary>
public class AuditIssue
{
    public string OriginalText { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
}

