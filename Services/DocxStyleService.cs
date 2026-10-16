using System;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class DocxStyleService 
{
    public DocxStyle Extract(string docxPath)
    {
        var style = new DocxStyle();
        try
        {
            using var doc = WordprocessingDocument.Open(docxPath, false);
            var main = doc.MainDocumentPart;
            var styles = main?.StyleDefinitionsPart?.Styles;
            if (styles == null) return style;

            // 1) 文档默认字体与字号（docDefaults / rPrDefault）
            var defaultRun = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
            if (defaultRun != null)
            {
                var font = defaultRun.RunFonts?.EastAsia?.Value
                           ?? defaultRun.RunFonts?.Ascii?.Value;
                if (!string.IsNullOrWhiteSpace(font)) style.FontName = font;

                if (int.TryParse(defaultRun.FontSize?.Val?.Value, out var hp) && hp > 0)
                    style.BodyPt = hp / 2.0;
            }

            // 2) 具名样式：正文（Normal）、一级标题（heading 1）、标题（Title）
            foreach (var s in styles.Elements<Style>())
            {
                var styleName = s.StyleName?.Val?.Value ?? "";
                if (!int.TryParse(s.StyleRunProperties?.FontSize?.Val?.Value, out var halfPoints) || halfPoints <= 0)
                    continue;

                var pt = halfPoints / 2.0;
                if (styleName.Contains("heading 1", StringComparison.OrdinalIgnoreCase) || s.StyleId?.Value == "Heading1")
                    style.HeadingPt = pt;
                else if (styleName.Equals("Title", StringComparison.OrdinalIgnoreCase) || s.StyleId?.Value == "Title")
                    style.TitlePt = pt;
                else if (styleName.Equals("Normal", StringComparison.OrdinalIgnoreCase))
                    style.BodyPt = pt;
            }

            // 3) 首个表格的边框样式判断（三线表：无左右框线/无内部竖线）
            var firstTable = main?.Document?.Body?.Descendants<Table>().FirstOrDefault();
            var borders = firstTable?.GetFirstChild<TableProperties>()?.GetFirstChild<TableBorders>();
            if (borders != null)
            {
                static bool IsNone(BorderValues? v) =>
                    v.HasValue && v.Value.ToString() is "none" or "nil";

                var left = borders.LeftBorder?.Val;
                var insideV = borders.InsideVerticalBorder?.Val;
                if (IsNone(left) || IsNone(insideV))
                    style.TableThreeLine = true;
                else if (left != null)
                    style.TableThreeLine = false;
            }
        }
        catch
        {
            // 解析失败回退默认样式
        }
        return style;
    }
}
