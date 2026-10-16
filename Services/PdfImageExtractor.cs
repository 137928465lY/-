using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace 叶片缺陷审核.Services;

public class PdfImageExtractor 
{
    // 题注："图2-1 xxx" / "图 2-1 xxx" / "图2 xxx"
    private static readonly Regex CaptionRegex = new(@"^图\s*(\d+)\s*[-–—.．]?\s*(\d+)?", RegexOptions.Compiled);

    /// <summary>诊断：最近的内部异常（fileName → message）。</summary>
    public static readonly List<string> LastErrors = new();

    private static void RecordError(string context, Exception ex)
    {
        if (LastErrors.Count < 8)
            LastErrors.Add($"[{context}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?[..Math.Min(900, ex.StackTrace.Length)]}");
    }

    public List<PdfImage> Extract(string pdfPath, int minDimension = 150)
    {
        var result = new List<PdfImage>();
        var title = Path.GetFileNameWithoutExtension(pdfPath);
        var index = 0;

        try
        {
            using var document = PdfDocument.Open(pdfPath);
            foreach (var page in document.GetPages())
            {
                // 预构建本页文字行（按基线 Y 聚类），供题注匹配
                List<(PdfRectangle Bounds, string Text)> lines;
                try { lines = BuildLines(page); }
                catch (Exception ex) { RecordError(Path.GetFileName(pdfPath) + " BuildLines p" + page.Number, ex); continue; }

                foreach (var image in page.GetImages())
                {
                    try
                    {
                        var width = image.WidthInSamples;
                        var height = image.HeightInSamples;
                        if (width < minDimension || height < minDimension) continue;

                        // 极端宽高比：横幅/分隔线/长条装饰
                        var ratio = (double)width / height;
                        if (ratio > 4 || ratio < 0.25) continue;

                        byte[] data;
                        string mime;
                        if (image.TryGetPng(out var png))
                        {
                            data = png;
                            mime = "image/png";
                        }
                        else
                        {
                            data = image.RawBytes.ToArray();
                            mime = "image/jpeg"; // 多数 PDF 内嵌图为 JPEG 流（DCTDecode）
                        }
                        if (data.Length < 15_000) continue; // <15KB 过滤

                        // 像素分析：过滤公章/大面积空白装饰图
                        if (!PassesPixelFilter(data)) continue;

                        // 题注：图片正下方、水平有重叠、匹配"图N-N"的最近文字行
                        var caption = FindCaption(lines, image.Bounds);
                        var captionSuffix = caption != null ? CaptionRegex.Match(caption).Value : "";

                        index++;
                        var safeCap = SanitizeFileName(captionSuffix.Length > 0 ? captionSuffix : $"图{index:D2}");
                        result.Add(new PdfImage
                        {
                            FileName = $"{title}_{safeCap}_{index:D2}.jpg",
                            Data = data,
                            MimeType = mime,
                            SourceTitle = title,
                            Caption = caption,
                        });
                    }
                    catch (Exception ex)
                    {
                        RecordError(Path.GetFileName(pdfPath) + " img#" + index, ex);
                        // 单张图提取失败跳过
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RecordError(Path.GetFileName(pdfPath) + " open", ex);
            // PDF 解析失败返回已提取部分
        }

        return result;
    }

    // ─────────────── 题注识别 ───────────────

    private static List<(PdfRectangle Bounds, string Text)> BuildLines(Page page)
    {
        var lines = new List<(PdfRectangle, string)>();
        foreach (var g in page.GetWords()
                     .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 3))) // 3pt 容差聚类成行
        {
            var ws = g.OrderBy(w => w.BoundingBox.Left).ToList();
            if (ws.Count == 0) continue;
            var left = ws.Min(w => w.BoundingBox.Left);
            var right = ws.Max(w => w.BoundingBox.Right);
            var bottom = ws.Min(w => w.BoundingBox.Bottom);
            var top = ws.Max(w => w.BoundingBox.Top);
            var text = string.Concat(ws.Select(w => w.Text));
            lines.Add((new PdfRectangle(left, bottom, right - left, top - bottom), text));
        }
        return lines;
    }

    private static string? FindCaption(List<(PdfRectangle Bounds, string Text)> lines, PdfRectangle imgBounds)
    {
        // 优先找图片下方（常规图题位置）；找不到再找上方（部分文档图题在图上）
        var below = MatchCaptionLine(lines, imgBounds, belowOnly: true);
        return below ?? MatchCaptionLine(lines, imgBounds, belowOnly: false);
    }

    private static string? MatchCaptionLine(List<(PdfRectangle Bounds, string Text)> lines, PdfRectangle imgBounds, bool belowOnly)
    {
        // 窗口：图外缘 ±60pt 内、水平方向与图片有重叠、匹配"图N"开头的最贴近行
        string? best = null;
        double bestDist = double.MaxValue;
        foreach (var l in lines)
        {
            if (string.IsNullOrEmpty(l.Text)) continue;
            if (!CaptionRegex.IsMatch(l.Text)) continue;

            double dist;
            if (belowOnly)
            {
                if (!(l.Bounds.Bottom < imgBounds.Bottom + 2 && l.Bounds.Bottom > imgBounds.Bottom - 60)) continue;
                dist = imgBounds.Bottom - l.Bounds.Bottom;
            }
            else
            {
                if (!(l.Bounds.Top > imgBounds.Top - 2 && l.Bounds.Top < imgBounds.Top + 60)) continue;
                dist = l.Bounds.Top - imgBounds.Top;
            }

            if (!(l.Bounds.Left < imgBounds.Right && l.Bounds.Right > imgBounds.Left)) continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = l.Text;
            }
        }

        return best == null ? null : Regex.Replace(best.Trim(), @"\s+", " ");
    }

    // ─────────────── 公章/装饰过滤 ───────────────

    /// <summary>
    /// 像素特征过滤：公章（红章+大比例白底）、空白图、纯色块。
    /// 缺陷照片（复杂纹理、低白底比例）不受影响。
    /// </summary>
    private static bool PassesPixelFilter(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var bmp = new Bitmap(ms);
            // 降采样到 ≤200px 加速统计
            var scale = Math.Min(1.0, 200.0 / Math.Max(bmp.Width, bmp.Height));
            var w = Math.Max(1, (int)(bmp.Width * scale));
            var h = Math.Max(1, (int)(bmp.Height * scale));
            using var small = new Bitmap(w, h);
            using (var g = Graphics.FromImage(small))
                g.DrawImage(bmp, 0, 0, w, h);

            long total = w * h, white = 0, red = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var p = small.GetPixel(x, y);
                    // PDF 图片常带透明通道（SMask），透明区域在页面上即白底
                    if (p.A < 16 || (p.R > 230 && p.G > 230 && p.B > 230)) white++;
                    else if (p.R > 140 && p.G < 110 && p.B < 110) red++;
                }
            }

            var whiteRatio = (double)white / total;
            var redRatio = (double)red / total;
            var contentRatio = 1 - whiteRatio; // 非白像素占比

            // 空白图：几乎没有内容
            if (contentRatio < 0.003) return false;

            // 公章特征：白底稀疏内容，且红色占非白内容主导（红线图黑色线条占主导，不受影响）
            if (contentRatio < 0.3 && redRatio / contentRatio > 0.4) return false;

            return true;
        }
        catch
        {
            // 无法解码（非标准 JPEG 等）时保留，交给尺寸/大小过滤兜底
            return true;
        }
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
}
/// <summary>PDF 图片提取结果。</summary>
public class PdfImage
{
    public string FileName { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string MimeType { get; set; } = "image/jpeg";
    public string SourceTitle { get; set; } = "";
    public string? Caption { get; set; }
}