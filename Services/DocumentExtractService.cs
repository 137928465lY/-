using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NPOI.XWPF.UserModel;

namespace 叶片缺陷审核.Services;

public class DocumentContentItem
{
    public string? Text { get; set; }
    public byte[]? ImageData { get; set; }
    public string? ImageMimeType { get; set; }
    public bool IsImage => ImageData != null;
}


public class DocumentExtractService 
{
    public async Task<List<DocumentContentItem>> ExtractContentAsync(string filePath)
    {
        var items = new List<DocumentContentItem>();

        await Task.Run(() =>
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var doc = new XWPFDocument(fs);

            // 提取段落内容（文字和图片）
            foreach (var para in doc.Paragraphs)
            {
                // 先收集段落文字
                var text = para.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    items.Add(new DocumentContentItem { Text = text });
                }

                // 提取段落中的图片
                foreach (var run in para.Runs)
                {
                    var pictures = run.GetEmbeddedPictures();
                    foreach (var pic in pictures)
                    {
                        try
                        {
                            var data = pic.GetPictureData()?.Data;
                            if (data != null && data.Length > 0)
                            {
                                var mimeType = GetMimeType(pic.GetPictureData());
                                items.Add(new DocumentContentItem 
                                { 
                                    ImageData = data,
                                    ImageMimeType = mimeType
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"图片提取失败: {ex.Message}");
                        }
                    }
                }
            }

            // 提取表格内容
            foreach (var table in doc.Tables)
            {
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row.GetTableCells())
                    {
                        // 提取单元格文字
                        var cellText = cell.GetText()?.Trim();
                        if (!string.IsNullOrEmpty(cellText))
                        {
                            items.Add(new DocumentContentItem { Text = cellText });
                        }

                        // 提取单元格图片
                        foreach (var para in cell.Paragraphs)
                        {
                            foreach (var run in para.Runs)
                            {
                                var pictures = run.GetEmbeddedPictures();
                                foreach (var pic in pictures)
                                {
                                    try
                                    {
                                        var data = pic.GetPictureData()?.Data;
                                        if (data != null && data.Length > 0)
                                        {
                                            var mimeType = GetMimeType(pic.GetPictureData());
                                            items.Add(new DocumentContentItem 
                                            { 
                                                ImageData = data,
                                                ImageMimeType = mimeType
                                            });
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"表格图片提取失败: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 提取页眉内容
            foreach (var header in doc.HeaderList)
            {
                foreach (var para in header.Paragraphs)
                {
                    var text = para.Text?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        items.Add(new DocumentContentItem { Text = $"[页眉] {text}" });
                    }
                }
            }

            // 提取页脚内容
            foreach (var footer in doc.FooterList)
            {
                foreach (var para in footer.Paragraphs)
                {
                    var text = para.Text?.Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        items.Add(new DocumentContentItem { Text = $"[页脚] {text}" });
                    }
                }
            }
        });

        return items;
    }

    private string GetMimeType(XWPFPictureData? pictureData)
    {
        if (pictureData == null) return "image/jpeg";
        
        var fileName = pictureData.FileName?.ToLower() ?? "";
        return fileName switch
        {
            var n when n.EndsWith(".png") => "image/png",
            var n when n.EndsWith(".gif") => "image/gif",
            var n when n.EndsWith(".svg") => "image/svg+xml",
            _ => "image/jpeg"
        };
    }
}

