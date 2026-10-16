using System.Collections.Generic;

namespace 叶片缺陷审核.Models;

/// <summary>
/// LLM 模型配置（OpenAI 兼容协议）。
/// </summary>
public class LlmModelConfig
{
    public string Name { get; set; } = "";
    /// <summary>API 基地址，如 https://dashscope.aliyuncs.com/compatible-mode/v1</summary>
    public string ApiUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "";
    public double Temperature { get; set; } = 0.7;

    public override string ToString() => Name;
}

/// <summary>
/// 消息内容片段：文本或多模态图片（dataURI）。
/// </summary>
public class LlmMessagePart
{
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
    /// <summary>type=image_url 时的图片 dataURI（data:image/png;base64,xxx）。</summary>
    public string? ImageUrl { get; set; }

    public static LlmMessagePart TextPart(string text) => new() { Type = "text", Text = text };
    public static LlmMessagePart ImagePart(string dataUri) => new() { Type = "image_url", ImageUrl = dataUri };
}

/// <summary>
/// 对话消息。
/// </summary>
public class LlmMessage
{
    public string Role { get; set; } = "user";
    public List<LlmMessagePart> Parts { get; set; } = [];

    public static LlmMessage User(string text) => new()
    {
        Role = "user",
        Parts = [LlmMessagePart.TextPart(text)],
    };
}
