using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using MiniSoftware;
using PdfiumViewer;

namespace 叶片缺陷审核.Services;



public class DocumentConvertService 
{
    public async Task<ObservableCollection<Avalonia.Media.Imaging.Bitmap>> ConvertDocxToPngAsync(
        string docxPath, IProgress<(int Page, int Total)>? progress = null)
    {
        var pngImages = new ObservableCollection<Avalonia.Media.Imaging.Bitmap>();
        string tempPdfPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.pdf");
        
        try
        {
            // Step 1: DOCX → PDF
            await Task.Run(() => MiniPdf.ConvertToPdf(docxPath, tempPdfPath));
            
            if (!File.Exists(tempPdfPath))
                return pngImages;

            // Step 2: PDF → PNG using PdfiumViewer
            using var document = PdfDocument.Load(tempPdfPath);
            var total = document.PageCount;

            for (int i = 0; i < total; i++)
            {
                progress?.Report((i + 1, total));
                var pageSize = document.PageSizes[i];
                // 使用 150 DPI 渲染，清晰度足够
                int width = (int)(pageSize.Width / 72.0 * 150);
                int height = (int)(pageSize.Height / 72.0 * 150);
                
                using var image = document.Render(i, width, height, 150, 150, PdfRenderFlags.Annotations);
                
                using var ms = new MemoryStream();
                image.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                
                var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                pngImages.Add(avaloniaBitmap);
            }
        }
        finally
        {
            if (File.Exists(tempPdfPath))
                File.Delete(tempPdfPath);
        }

        return pngImages;
    }
}

