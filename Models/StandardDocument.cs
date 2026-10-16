using System.Collections.Generic;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.Models;

/// <summary>
/// 标准维修文档数据模型，存储从标准文档中提取的内容（包含文字和图片）和补充审核条款
/// </summary>
public class StandardDocument
{
    /// <summary>
    /// 完整内容列表（补充审核条款 + 标准文档提取的文字和图片）
    /// </summary>
    public List<DocumentContentItem> ContentItems { get; set; } = new();

    /// <summary>
    /// 完整文本内容（仅文字，用于日志显示等场景）
    /// </summary>
    public string FullContent { get; set; } = string.Empty;

    /// <summary>
    /// 标准文档提取的文字内容
    /// </summary>
    public string DocumentText { get; set; } = string.Empty;

    /// <summary>
    /// 用户补充的审核条款
    /// </summary>
    public string AuditClauses { get; set; } = string.Empty;

    /// <summary>
    /// 源文件路径
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 源文件名
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// 是否已加载内容
    /// </summary>
    public bool HasContent => ContentItems.Count > 0;
}
