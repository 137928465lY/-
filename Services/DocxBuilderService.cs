using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlToOpenXml;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class DocxBuilderService 
{
    public async Task<string> BuildAsync(DocGenDocument doc, DocGenInput options, string outputDirectory,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(outputDirectory);
            var safeTitle = string.Concat(doc.Title.Split(Path.GetInvalidFileNameChars()));
            if (safeTitle.Length == 0) safeTitle = "叶片缺陷维修文档";
            var outputPath = Path.Combine(outputDirectory, $"{safeTitle}.docx");
            var unique = 1;
            while (File.Exists(outputPath))
                outputPath = Path.Combine(outputDirectory, $"{safeTitle}({unique++}).docx");

            using var generated = new MemoryStream();
            using (var package = WordprocessingDocument.Create(generated, WordprocessingDocumentType.Document))
            {
                var mainPart = package.AddMainDocumentPart();

                // 打开文档时自动更新域（TOC 目录页码）
                var settingsPart = mainPart.AddNewPart<DocumentSettingsPart>();
                settingsPart.Settings = new Settings(new UpdateFieldsOnOpen { Val = OnOffValue.FromBoolean(true) });

                mainPart.Document = new Document(new Body());
                var body = mainPart.Document.Body!;

                AppendCover(body, doc, options);
                AppendToc(body);

                // 占位符替换：IMG_N → 真实图片 dataURI（HtmlToOpenXml 支持 <img src="data:...">）
                var html = doc.FinalHtml ?? "";
                foreach (var image in doc.Images)
                {
                    html = html.Replace(image.Token, image.DataUrl);
                }

                // 兜底：模型幻觉出的自编 src（文件名/路径）按出现顺序映射为真实图片；
                // 可用图片用尽后删除剩余 img 标签——确保文档中绝不出现"找不到图片"的损坏引用
                var usedImages = new HashSet<DocGenImage>(doc.Images
                    .Where(i => html.Contains(i.DataUrl)));
                var imageCursor = 0;
                html = System.Text.RegularExpressions.Regex.Replace(html, @"<img[^>]*>",
                    m =>
                    {
                        if (m.Value.Contains("src=\"data:", StringComparison.OrdinalIgnoreCase))
                            return m.Value; // 已是嵌入图片

                        // 跳过已被占位符使用的图片，按顺序取下一张可用图
                        while (imageCursor < doc.Images.Count && usedImages.Contains(doc.Images[imageCursor]))
                            imageCursor++;
                        if (imageCursor < doc.Images.Count)
                        {
                            var img = doc.Images[imageCursor++];
                            usedImages.Add(img);
                            return $"<img src=\"{img.DataUrl}\" width=\"450\"/>";
                        }
                        return ""; // 无可用图：删除损坏引用
                    }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                AppendBodyHtml(mainPart, body, html);
                ApplyTableStyles(body, options.TableThreeLine);

                mainPart.Document.Save();
            }

            File.WriteAllBytes(outputPath, generated.ToArray());
            FixImagePartLocations(outputPath);
            return outputPath;
        }, ct);
    }

    // ─────────────── 封面页（复刻 Qt 版 buildCoverHtml 版式，字体/字号取自模板样式） ───────────────

    private static void AppendCover(Body body, DocGenDocument doc, DocGenInput options)
    {
        var style = options.Style ?? new DocxStyle();
        var font = style.FontName;
        var titleHalfPoints = ((int)(style.TitlePt * 2)).ToString();
        var companyHalfPoints = ((int)Math.Max(12, style.HeadingPt) * 2).ToString();

        // 文档标题
        body.Append(new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "1200" }),
            new Run(
                new RunProperties(new Bold(), new RunFonts { EastAsia = font },
                    new FontSize { Val = titleHalfPoints }),
                new Text(doc.Title))));

        // 垂直留白
        for (var i = 0; i < 8; i++)
            body.Append(new Paragraph());

        // 签名表：编制/审核/批准/版本
        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "4400", Type = TableWidthUnitValues.Dxa },
                new TableJustification { Val = TableRowAlignmentValues.Center },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 8 },
                    new BottomBorder { Val = BorderValues.Single, Size = 8 },
                    new LeftBorder { Val = BorderValues.Single, Size = 8 },
                    new RightBorder { Val = BorderValues.Single, Size = 8 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 8 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 8 })));

        string[] labels = ["编  制", "审  核", "批  准", "版  本"];
        for (var i = 0; i < labels.Length; i++)
        {
            table.Append(new TableRow(
                new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = "1540", Type = TableWidthUnitValues.Dxa }),
                    new Paragraph(
                        new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                        new Run(new RunProperties(new Bold(), new RunFonts { EastAsia = font }),
                            new Text(labels[i])))),
                new TableCell(
                    new TableCellProperties(new TableCellWidth { Width = "2860", Type = TableWidthUnitValues.Dxa }),
                    new Paragraph(
                        new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                        new Run(new Text(i == 3 ? options.Version : ""))))));
        }
        body.Append(table);

        for (var i = 0; i < 5; i++)
            body.Append(new Paragraph());

        // 公司名
        body.Append(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new RunProperties(new Bold(), new RunFonts { EastAsia = font },
                    new FontSize { Val = companyHalfPoints }),
                new Text(options.Company))));

        // 封面分页
        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    // ─────────────── 目录页（TOC 域代码，打开文档时更新页码） ───────────────

    private static void AppendToc(Body body)
    {
        body.Append(new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new RunProperties(new Bold(), new RunFonts { EastAsia = "宋体" },
                    new FontSize { Val = "32" }),
                new Text("目  录"))));

        // TOC 域 + 缓存条目：MiniPdf 等预览转换器不执行 Word 域，
        // 域内必须写入静态目录条目（章节树）预览才可见；Word 打开时更新域会替换缓存内容。
        // 采用 Word 原生的跨段落域结构：begin+指令+separate 在首段，缓存条目各自成段，
        // end 追加在最后一个条目段落末尾。MiniPdf 0.30.0 按段落解析域，
        // 同段 separate..end 之间的内容会被丢弃（跨段则不受影响）；Word 仍识别为标准 TOC 域。
        var tocBegin = new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { After = "40" }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" TOC \\o \"1-2\" \\z ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }));

        var entries = new List<Paragraph>();
        foreach (var rawLine in DocGenPrompts.SectionTree.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var isSub = rawLine.StartsWith("  ");
            entries.Add(new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { After = "40" }),
                new Run(new RunProperties(
                    new RunFonts { EastAsia = "宋体" },
                    new FontSize { Val = isSub ? "18" : "21" },
                    new Color { Val = "333333" }),
                    new Text(line.Trim()) { Space = SpaceProcessingModeValues.Preserve })));
        }

        if (entries.Count > 0)
        {
            entries[^1].Append(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
            body.Append(tocBegin);
            foreach (var entry in entries) body.Append(entry);
        }
        else
        {
            // 章节树为空的兜底：域不写缓存条目，仅保留域结构
            tocBegin.Append(new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
            body.Append(tocBegin);
        }

        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    /// <summary>
    /// 修正包结构：HtmlToOpenXml 把图片 part 放在包根 /media/（绝对路径关系），
    /// Word 能容错打开，但 MiniPdf 等预览转换器解析不到。
    /// 移动 media/* → word/media/* 并把关系改为相对路径。
    /// </summary>
    private static void FixImagePartLocations(string path)
    {
        try
        {
            FixImagePartLocationsCore(path);
        }
        catch
        {
            // 包结构修正失败不中断生成（Word 可容错打开，仅预览转换受影响）
        }
    }

    private static void FixImagePartLocationsCore(string path)
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update);

        var mediaEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("media/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var entry in mediaEntries)
        {
            byte[] data;
            using (var src = entry.Open())
            {
                using var ms = new MemoryStream();
                src.CopyTo(ms);
                data = ms.ToArray();
            } // 先关闭流再删除（Delete 要求无打开的写入流）

            entry.Delete();

            var neo = zip.CreateEntry("word/" + entry.FullName);
            using var ws = neo.Open();
            ws.Write(data, 0, data.Length);
        }

        var relsEntry = zip.GetEntry("word/_rels/document.xml.rels");
        if (relsEntry != null)
        {
            string relXml;
            using (var s = relsEntry.Open())
            using (var sr = new StreamReader(s))
                relXml = sr.ReadToEnd();

            var fixedXml = relXml.Replace("Target=\"/media/", "Target=\"media/");
            if (fixedXml != relXml)
            {
                relsEntry.Delete();
                var neo = zip.CreateEntry("word/_rels/document.xml.rels");
                using var ws = neo.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(fixedXml);
                ws.Write(bytes, 0, bytes.Length);
            }
        }

        // 确保 Content_Types 声明了图片扩展名
        var ctEntry = zip.GetEntry("[Content_Types].xml");
        if (ctEntry != null)
        {
            string ctXml;
            using (var s = ctEntry.Open())
            using (var sr = new StreamReader(s))
                ctXml = sr.ReadToEnd();

            var add = "";
            if (!ctXml.Contains("jpg", StringComparison.OrdinalIgnoreCase))
                add += "<Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>";
            if (!ctXml.Contains("png", StringComparison.OrdinalIgnoreCase))
                add += "<Default Extension=\"png\" ContentType=\"image/png\"/>";
            if (add.Length > 0)
            {
                ctXml = ctXml.Replace("</Types>", add + "</Types>");
                ctEntry.Delete();
                var neo = zip.CreateEntry("[Content_Types].xml");
                using var ws = neo.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(ctXml);
                ws.Write(bytes, 0, bytes.Length);
            }
        }
    }

    // ─────────────── HTML 正文转换 ───────────────

    private static void AppendBodyHtml(MainDocumentPart mainPart, Body body, string html)
    {
        var converter = new HtmlConverter(mainPart);
        converter.ParseBody(html);
    }

    // ─────────────── 表格样式后处理（确定性重刷，不信任模型边框细节） ───────────────

    private static void ApplyTableStyles(Body body, bool threeLine)
    {
        foreach (var table in body.Descendants<Table>())
        {
            var props = table.GetFirstChild<TableProperties>();
            if (props == null)
            {
                props = new TableProperties();
                table.PrependChild(props);
            }

            var borders = props.GetFirstChild<TableBorders>();
            borders?.Remove();
            borders = new TableBorders();

            if (threeLine)
            {
                // 三线表：顶线/底线粗、无竖线；表头下线单独加（首行单元格底边框）
                borders.Append(new TopBorder { Val = BorderValues.Single, Size = 12 });
                borders.Append(new BottomBorder { Val = BorderValues.Single, Size = 12 });
                borders.Append(new LeftBorder { Val = BorderValues.None, Size = 0 });
                borders.Append(new RightBorder { Val = BorderValues.None, Size = 0 });
                borders.Append(new InsideHorizontalBorder { Val = BorderValues.None, Size = 0 });
                borders.Append(new InsideVerticalBorder { Val = BorderValues.None, Size = 0 });
                props.Append(borders);

                var firstRow = table.Elements<TableRow>().FirstOrDefault();
                if (firstRow != null)
                {
                    foreach (var cell in firstRow.Elements<TableCell>())
                    {
                        var cellProps = cell.GetFirstChild<TableCellProperties>();
                        if (cellProps == null)
                        {
                            cellProps = new TableCellProperties();
                            cell.PrependChild(cellProps);
                        }
                        cellProps.RemoveAllChildren<TableCellBorders>();
                        cellProps.Append(new TableCellBorders(
                            new BottomBorder { Val = BorderValues.Single, Size = 6 }));
                    }
                }
            }
            else
            {
                // 全框线
                borders.Append(new TopBorder { Val = BorderValues.Single, Size = 4 });
                borders.Append(new BottomBorder { Val = BorderValues.Single, Size = 4 });
                borders.Append(new LeftBorder { Val = BorderValues.Single, Size = 4 });
                borders.Append(new RightBorder { Val = BorderValues.Single, Size = 4 });
                borders.Append(new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 });
                borders.Append(new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 });
                props.Append(borders);
            }
        }
    }
}
