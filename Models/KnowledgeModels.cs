using System;
using System.Collections.Generic;

namespace 叶片缺陷审核.Models;

/// <summary>
/// 知识条目（迁自 Qt 版 KnowledgeBase）。
/// </summary>
public class KnowledgeEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public List<string> Tags { get; set; } = [];

    /// <summary>文本内容（PDF 条目在按需识别后填充）。</summary>
    public string Content { get; set; } = "";

    public DateTime Updated { get; set; } = DateTime.Now;

    /// <summary>存档类型：text | image | pdf。</summary>
    public string ArchiveType { get; set; } = "text";

    /// <summary>图片/PDF 原文件存档路径。</summary>
    public string? FilePath { get; set; }

    /// <summary>内置种子条目不可删除。</summary>
    public bool IsBuiltin { get; set; }
}

/// <summary>
/// 知识库。
/// </summary>
public class KnowledgeBaseInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public bool IsBuiltin { get; set; }

    /// <summary>
    /// 隐藏知识库：不显示在界面上、不参与知识库页检索/问答；
    /// 仅作为文档生成 Agent 的参考资料（搜索时需显式 includeHidden）。
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>文档生成页的勾选状态（随 JSON 持久化）。</summary>
    public bool IsSelected { get; set; }

    public List<KnowledgeEntry> Entries { get; set; } = [];
}

/// <summary>
/// 检索结果：命中条目 + 命中位置摘要。
/// </summary>
public class SearchResultItem
{
    public KnowledgeEntry Entry { get; init; } = null!;

    public string Snippet { get; init; } = "";

    public int Score { get; init; }
}
