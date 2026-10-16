using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;

namespace 叶片缺陷审核.Services;

public class DocImporterService 
{
    public string ArchiveDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "叶片缺陷审核", "archive");

    public Task<ImportResult> ImportAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(filePath))
                return new ImportResult { Ok = false, Message = "文件不存在" };

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var title = Path.GetFileNameWithoutExtension(filePath);

            try
            {
                Directory.CreateDirectory(ArchiveDirectory);

                // 图片 / PDF：仅存档，提取原件
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                {
                    var archived = ArchiveFile(filePath);
                    return new ImportResult { Ok = true, Title = title, ArchiveType = "image", FilePath = archived, Message = "图片已存档" };
                }

                if (ext == ".pdf")
                {
                    var archived = ArchiveFile(filePath);
                    return new ImportResult { Ok = true, Title = title, ArchiveType = "pdf", FilePath = archived, Message = "PDF 已存档（检索标题命中后将自动识别正文）" };
                }

                // 文本类：提取内容
                var content = ext switch
                {
                    ".txt" or ".md" or ".log" => File.ReadAllText(filePath, DetectEncoding(filePath)),
                    ".csv" => ReadCsv(filePath),
                    ".xlsx" or ".xls" => ReadExcel(filePath, ct),
                    ".docx" => ReadDocx(filePath),
                    _ => null,
                };

                if (content == null)
                    return new ImportResult { Ok = false, Message = $"暂不支持的文件类型：{ext}" };

                if (string.IsNullOrWhiteSpace(content))
                    return new ImportResult { Ok = false, Message = "未能从文件中提取到文本内容" };

                return new ImportResult { Ok = true, Title = title, Content = content, ArchiveType = "text" };
            }
            catch (Exception ex)
            {
                return new ImportResult { Ok = false, Message = "导入失败：" + ex.Message };
            }
        }, ct);
    }

    public string? FileToDataUrl(string filePath, int maxBytes = 6 * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length > maxBytes) return null;
            var mime = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "application/octet-stream",
            };
            // 压缩后转 dataURI，控制多模态请求体大小
            var (data, compressedMime) = ImageCompressor.PrepareForUpload(File.ReadAllBytes(filePath), mime);
            return $"data:{compressedMime};base64,{Convert.ToBase64String(data)}";
        }
        catch
        {
            return null;
        }
    }

    // ─────────────── 各格式解析 ───────────────

    private string ArchiveFile(string source)
    {
        var dest = Path.Combine(ArchiveDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(source)}");
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    private static Encoding DetectEncoding(string path)
    {
        // 简易 BOM 检测，缺省 UTF-8
        var bom = new byte[4];
        using var fs = File.OpenRead(path);
        if (fs.Read(bom, 0, 4) < 2) return Encoding.UTF8;
        return bom[0] == 0xFF && bom[1] == 0xFE ? Encoding.Unicode
            : bom[0] == 0xFE && bom[1] == 0xFF ? Encoding.BigEndianUnicode
            : bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF ? Encoding.UTF8
            : Encoding.UTF8;
    }

    private static string ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        return string.Join(Environment.NewLine,
            lines.Select(l => string.Join("  ", l.Split(',').Select(c => c.Trim('"').Trim()))));
    }

    private static string ReadExcel(string path, CancellationToken ct)
    {
        using var fs = File.OpenRead(path);
        var workbook = WorkbookFactory.Create(fs);
        var sb = new StringBuilder();

        for (var s = 0; s < workbook.NumberOfSheets; s++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = workbook.GetSheetAt(s);
            sb.AppendLine($"【工作表：{sheet.SheetName}】");
            foreach (var row in sheet)
            {
                var cells = new List<string>();
                foreach (var cell in row.Cells)
                {
                    var text = cell.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(text)) cells.Add(text);
                }
                if (cells.Count > 0) sb.AppendLine(string.Join("  ", cells));
            }
        }
        return sb.ToString();
    }

    private static string ReadDocx(string path)
    {
        using var fs = File.OpenRead(path);
        var doc = new XWPFDocument(fs);
        var sb = new StringBuilder();

        foreach (var element in doc.BodyElements)
        {
            switch (element)
            {
                case XWPFParagraph para when !string.IsNullOrWhiteSpace(para.Text):
                    sb.AppendLine(para.Text.Trim());
                    break;
                case XWPFTable table:
                    foreach (var row in table.Rows)
                    {
                        var cells = row.GetTableCells()
                            .Select(c => c.GetText()?.Trim() ?? "")
                            .Where(t => t.Length > 0);
                        if (cells.Any()) sb.AppendLine(string.Join("  ", cells));
                    }
                    break;
            }
        }
        return sb.ToString();
    }
}
/// <summary>文档导入结果。</summary>
public class ImportResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string ArchiveType { get; set; } = "";
    public string Content { get; set; } = "";
    public string FullText { get; set; } = "";
}