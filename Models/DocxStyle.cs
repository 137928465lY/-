namespace 叶片缺陷审核.Models;

/// <summary>
/// 从标准输出模板 docx 中提取的格式信息（段落字体/字号、表格样式）。
/// </summary>
public class DocxStyle
{
    public string FontName { get; set; } = "宋体";

    /// <summary>正文字号（pt）。</summary>
    public double BodyPt { get; set; } = 10.5;

    /// <summary>章节标题字号（pt）。</summary>
    public double HeadingPt { get; set; } = 13;

    /// <summary>大标题字号（pt）。</summary>
    public double TitlePt { get; set; } = 18;

    /// <summary>表格为三线表（true）或全框线（false）。</summary>
    public bool TableThreeLine { get; set; } = true;
}
