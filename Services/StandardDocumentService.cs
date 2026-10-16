using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

/// <summary>
/// 标准维修文档存储服务，保存用户导入的标准文档路径
/// </summary>
public class StandardDocumentService(DocumentExtractService extractService) 
{
    private string? _standardDocumentPath;

    /// <summary>
    /// 解析后的标准文档数据
    /// </summary>
    public StandardDocument? Document { get; private set; }

    /// <summary>
    /// 标准文档路径
    /// </summary>
    public string? StandardDocumentPath
    {
        get => _standardDocumentPath;
        set
        {
            _standardDocumentPath = value;
            OnDocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 标准文档文件名
    /// </summary>
    public string? StandardDocumentName =>
        !string.IsNullOrEmpty(_standardDocumentPath) && File.Exists(_standardDocumentPath)
            ? Path.GetFileName(_standardDocumentPath)
            : null;

    /// <summary>
    /// 是否已导入标准文档
    /// </summary>
    public bool HasDocument => !string.IsNullOrEmpty(_standardDocumentPath) && File.Exists(_standardDocumentPath);

    /// <summary>
    /// 文档变更事件
    /// </summary>
    public event EventHandler? OnDocumentChanged;

    /// <summary>
    /// 设置标准文档路径
    /// </summary>
    public void SetDocument(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException("文件不存在", filePath);

        StandardDocumentPath = filePath;
    }

    /// <summary>
    /// 清除标准文档
    /// </summary>
    public void ClearDocument()
    {
        StandardDocumentPath = null;
        Document = null;
    }

    /// <summary>
    /// 使用 DocumentExtractService 提取标准文档中的文字和图片，
    /// 与补充审核条款合并后存储到 StandardDocument
    /// </summary>
    /// <param name="auditClauses">用户补充的审核条款，将放在最前面</param>
    public async Task<StandardDocument> ExtractAndBuildAsync(string auditClauses)
    {
        var result = new StandardDocument
        {
            FilePath = _standardDocumentPath,
            FileName = StandardDocumentName,
            AuditClauses = auditClauses ?? string.Empty
        };

        // 1. 补充审核条款作为第一个内容项
        if (!string.IsNullOrWhiteSpace(result.AuditClauses))
        {
            result.ContentItems.Add(new DocumentContentItem
            {
                Text = $"【补充审核条款】\n{result.AuditClauses}"
            });
        }

        // 2. 提取标准文档中的文字和图片
        if (HasDocument)
        {
            var extractedItems = await extractService.ExtractContentAsync(_standardDocumentPath!);

            // 如果已有审核条款内容，在前面加分隔标记
            if (result.ContentItems.Count > 0 && extractedItems.Count > 0)
            {
                result.ContentItems.Add(new DocumentContentItem
                {
                    Text = "【标准维修文档内容】"
                });
            }

            result.ContentItems.AddRange(extractedItems);

            // 收集纯文本用于 FullContent
            var textBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.AuditClauses))
            {
                textBuilder.AppendLine("【补充审核条款】");
                textBuilder.AppendLine(result.AuditClauses);
                textBuilder.AppendLine();
            }
            if (extractedItems.Count > 0)
            {
                textBuilder.AppendLine("【标准维修文档内容】");
                foreach (var item in extractedItems)
                {
                    if (!item.IsImage && !string.IsNullOrEmpty(item.Text))
                    {
                        textBuilder.AppendLine(item.Text);
                    }
                }
            }
            result.FullContent = textBuilder.ToString().Trim();

            // 仅文本内容（不含图片）
            var docTextBuilder = new StringBuilder();
            foreach (var item in extractedItems)
            {
                if (!item.IsImage && !string.IsNullOrEmpty(item.Text))
                {
                    docTextBuilder.AppendLine(item.Text);
                }
            }
            result.DocumentText = docTextBuilder.ToString().Trim();
        }
        else
        {
            // 没有导入文档时，只使用审核条款
            result.FullContent = result.AuditClauses;
        }

        Document = result;
        return result;
    }
}
