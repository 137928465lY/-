using System.Collections.Generic;

namespace 叶片缺陷审核.Models;

/// <summary>
/// 缺陷文档生成输入。
/// </summary>
public class DocGenInput
{
    /// <summary>缺陷信息描述（位置/布层数/材料/损伤程度等）。</summary>
    public string DefectInfo { get; set; } = "";

    /// <summary>检索参考的知识库（可多选；空 = 全部）。</summary>
    public List<string> KnowledgeBaseIds { get; set; } = [];

    /// <summary>表格样式：三线表（true）或全框线（false）。</summary>
    public bool TableThreeLine { get; set; } = true;

    /// <summary>快速模式：跳过审校步骤（约省 1/3 耗时）。</summary>
    public bool FastMode { get; set; }

    /// <summary>快速模型名（规划/审校用小模型提速，null = 当前启用模型）。</summary>
    public string? FastModelName { get; set; }

    /// <summary>标准输出模板提取的样式（字体/字号/表格），null 时使用默认。</summary>
    public DocxStyle? Style { get; set; }

    public string Company { get; set; } = "XXX有限公司";
    public string Version { get; set; } = "A1.0";
}

/// <summary>
/// Agent 执行步骤通知。
/// </summary>
public class AgentStep
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>
/// 可插入生成文档的知识库图片（仅来自非隐藏知识库）。
/// </summary>
public class DocGenImage
{
    /// <summary>HTML 中的占位符（如 IMG_1），构建 docx 时替换为真实图片。</summary>
    public string Token { get; set; } = "";

    /// <summary>图片 dataURI。</summary>
    public string DataUrl { get; set; } = "";

    /// <summary>来源条目标题（供模型写图题）。</summary>
    public string Title { get; set; } = "";

    /// <summary>是否为 AI 补充的标准工序示意图（图题追加"（示意）"标注，与真实案例照片区分）。</summary>
    public bool IsSupplement { get; set; }
}

/// <summary>
/// 生成的文档（结构化中间产物）。
/// </summary>
public class DocGenDocument
{
    public string Title { get; set; } = "叶片缺陷维修文档";
    /// <summary>规划大纲 JSON（原文保留）。</summary>
    public string PlanJson { get; set; } = "";
    /// <summary>大纲章节（规划失败回退为内嵌模板结构）。</summary>
    public List<string> Sections { get; set; } = [];
    /// <summary>撰写步骤草稿 HTML。</summary>
    public string DraftHtml { get; set; } = "";
    /// <summary>审校后的正文 HTML。</summary>
    public string FinalHtml { get; set; } = "";
    /// <summary>可插入的图片占位符映射（构建 docx 时替换）。</summary>
    public List<DocGenImage> Images { get; set; } = [];
}
