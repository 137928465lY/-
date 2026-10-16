using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PdfiumViewer;
using 叶片缺陷审核.Models;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace 叶片缺陷审核.Services;

public class PdfRecognizeService 
{
    private const int Dpi = 150;
    /// <summary>页面文本层字符数低于该阈值视为扫描页（无有效文本层），需视觉识别兜底。</summary>
    private const int TextLayerMinChars = 30;
    private const string RecognizePrompt =
        "你是专业的 OCR 识别助手。请识别图片中的所有文字内容，要求：" +
        "1) 按原始阅读顺序输出纯文本；" +
        "2) 表格内容按行输出，单元格之间用两个空格分隔；" +
        "3) 不要输出任何解释、页码装饰或图片说明，只输出识别到的文字；" +
        "4) 保持标题的层级结构（如 一、2.1、3.2.1 等）。";

    private readonly LLMService _llm;

    public PdfRecognizeService(LLMService llm)
    {
        _llm = llm;
    }

    /// <summary>
    /// 识别整份 PDF：优先直读文本层（带文本层的电子 PDF 秒级完成、零成本），
    /// 文本层缺失的扫描页才回退"渲染成图 → 多模态 LLM 识别"。
    /// </summary>
    /// <param name="allowOcrFallback">false 时只做文本层直读，扫描页标记 NeedsOcr 而不调用 LLM
    /// （供后台预热使用，避免自动烧视觉模型 token）。</param>
    public async Task<PdfRecognizeResult> RecognizeAsync(string pdfPath, IProgress<string>? log = null,
        CancellationToken ct = default, bool allowOcrFallback = true)
    {
        var pages = new List<PdfPageText>();
        var needsOcr = false;

        // Step 1: PdfPig 逐页抽取文本层
        var textLayerPages = await Task.Run(() =>
        {
            var dict = new Dictionary<int, string>();
            using var pdf = PdfPigDocument.Open(pdfPath);
            foreach (var page in pdf.GetPages())
                dict[page.Number - 1] = (page.Text ?? "").Trim();
            return dict;
        }, ct);

        var pageCount = textLayerPages.Count;
        var ocrPageIndexes = new List<int>();

        await Task.Run(async () =>
        {
            using var document = PdfDocument.Load(pdfPath); // 仅在需要 OCR 时用于渲染
            for (var i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var layerText = textLayerPages.GetValueOrDefault(i, "");

                if (layerText.Length >= TextLayerMinChars)
                {
                    pages.Add(new PdfPageText { PageIndex = i, Text = layerText });
                    continue;
                }

                // 扫描页：无法免费直读
                if (!allowOcrFallback)
                {
                    needsOcr = true;
                    pages.Add(new PdfPageText { PageIndex = i, Text = layerText });
                    continue;
                }

                ocrPageIndexes.Add(i);
                log?.Report($"识别 PDF 第 {i + 1}/{pageCount} 页…");

                var size = document.PageSizes[i];
                var width = (int)(size.Width / 72.0 * Dpi);
                var height = (int)(size.Height / 72.0 * Dpi);

                using var image = document.Render(i, width, height, Dpi, Dpi, PdfRenderFlags.Annotations);
                using var ms = new MemoryStream();
                image.Save(ms, ImageFormat.Png);

                // 页面图压缩转 JPEG，控制多模态请求体大小
                var (data, mime) = ImageCompressor.PrepareForUpload(ms.ToArray(), "image/png");
                var dataUri = $"data:{mime};base64,{Convert.ToBase64String(data)}";

                var message = new LlmMessage
                {
                    Role = "user",
                    Parts =
                    [
                        LlmMessagePart.TextPart($"这是第 {i + 1}/{pageCount} 页。"),
                        LlmMessagePart.ImagePart(dataUri),
                    ],
                };

                var text = await _llm.SendAsync([message], RecognizePrompt, ct: ct);
                pages.Add(new PdfPageText { PageIndex = i, Text = text.Trim() });
            }
        }, ct);

        // Step 3: 组装全文（按页序拼接）
        var fullText = new StringBuilder();
        foreach (var page in pages.OrderBy(p => p.PageIndex))
        {
            if (page.Text.Length == 0) continue;
            if (fullText.Length > 0) fullText.AppendLine().AppendLine();
            fullText.Append(page.Text);
        }

        if (needsOcr)
            log?.Report("PDF 含扫描页，未做视觉识别（仅文本层直读）");
        else
            log?.Report(ocrPageIndexes.Count > 0 ? "PDF 识别完成" : "PDF 文本层直读完成（未调用识别模型）");

        return new PdfRecognizeResult { Pages = pages, FullText = fullText.ToString(), NeedsOcr = needsOcr };
    }
}
/// <summary>整份 PDF 的识别结果。</summary>
public class PdfRecognizeResult
{
    public List<PdfPageText> Pages { get; set; } = [];
    public string FullText { get; set; } = "";
    /// <summary>true = 存在无文本层的扫描页且本次未做视觉识别（内容不完整）。</summary>
    public bool NeedsOcr { get; set; }
}

/// <summary>单页识别文本。</summary>
public class PdfPageText
{
    public int PageIndex { get; set; }
    public string Text { get; set; } = "";
}
