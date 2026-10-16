using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace 叶片缺陷审核.Services;

/// <summary>
/// 多模态上传前的图片压缩：限制最长边并转 JPEG，控制请求体大小，
/// 避免大请求上传被服务端中断（"Error while copying content to a stream"）。
/// </summary>
public static class ImageCompressor
{
    public static (byte[] Data, string Mime) PrepareForUpload(
        byte[] imageData, string mimeType,
        int maxDimension = 1600, long quality = 80)
    {
        try
        {
            var ms = new MemoryStream(imageData);
            using var src = Image.FromStream(ms);

            var scale = Math.Min(1.0, Math.Min((double)maxDimension / src.Width, (double)maxDimension / src.Height));
            var width = Math.Max(1, (int)(src.Width * scale));
            var height = Math.Max(1, (int)(src.Height * scale));

            using var target = scale < 1.0 ? new Bitmap(width, height) : new Bitmap(src);
            if (scale < 1.0)
            {
                using var g = Graphics.FromImage(target);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, width, height);
            }

            using var output = new MemoryStream();
            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            target.Save(output, jpegEncoder, encoderParams);

            var compressed = output.ToArray();
            // 压缩反而更大时退回原图
            return compressed.Length < imageData.Length
                ? (compressed, "image/jpeg")
                : (imageData, mimeType);
        }
        catch
        {
            // 解码失败（非常规格式）退回原图
            return (imageData, mimeType);
        }
    }
}
