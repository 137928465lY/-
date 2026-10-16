using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using 叶片缺陷审核.Models;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.ViewModels.Pages;

public class AuditPageViewModel : PageBase
{
    private readonly AuditService _auditService;
    private readonly DocumentConvertService _convertService;
    private readonly StandardDocumentService _standardDocumentService;

    public AuditPageViewModel(AuditService auditService, DocumentConvertService convertService,
        StandardDocumentService standardDocumentService)
        : base("文档审核", "\ue61b", 1, "业务功能")
    {
        _auditService = auditService;
        _convertService = convertService;
        _standardDocumentService = standardDocumentService;

        AddDocumentCommand = ReactiveCommand.CreateFromTask(AddDocumentsAsync);
        RemoveDocumentCommand = ReactiveCommand.Create<DocumentInfo?>(doc =>
        {
            if (doc != null) Documents.Remove(doc);
        });
        SelectStandardCommand = ReactiveCommand.CreateFromTask(SelectStandardAsync);
        PreviewCommand = ReactiveCommand.CreateFromTask(PreviewAsync);
        AuditCommand = ReactiveCommand.CreateFromTask(AuditAsync, this.WhenAnyValue(x => x.IsBusy, b => !b));
        RefreshPreviewCommand = ReactiveCommand.CreateFromTask(RefreshPreviewAsync,
            this.WhenAnyValue(x => x.IsAuditComplete));
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync,
            this.WhenAnyValue(x => x.IsAuditComplete));
        ClearCommand = ReactiveCommand.Create(Clear);
    }

    // ─────────────── 待审文档 ───────────────

    public ObservableCollection<DocumentInfo> Documents { get; } = [];

    private DocumentInfo? selectedDocument;
    public DocumentInfo? SelectedDocument
    {
        get => selectedDocument;
        set => this.RaiseAndSetIfChanged(ref selectedDocument, value);
    }

    // ─────────────── 审核配置 ───────────────

    [Reactive] public string StandardDocumentName { get; set; } = "";
    [Reactive] public string AuditClausesText { get; set; } = "";
    [Reactive] public string CustomPrompt { get; set; } =
        "你是一个专业的维修文档审核助手，请仔细分析用户提供的文档内容，给出详细的审核意见。";

    // ─────────────── 审核状态 ───────────────

    [Reactive] public string LogMessage { get; set; } = "";
    [Reactive] public string AuditResult { get; set; } = "";
    [Reactive] public bool IsBusy { get; set; }
    [Reactive] public bool IsAuditComplete { get; set; }

    // ── 进度条（-1 表示不确定进度；审核条在 AI 审核意见区，渲染条在文档预览区，过程显示、完成隐藏） ──
    [Reactive] public double ProgressValue { get; set; }
    [Reactive] public bool ProgressIsIndeterminate { get; set; }
    [Reactive] public bool RenderProgressVisible { get; set; }
    [Reactive] public bool AuditProgressVisible { get; set; }

    private void SetProgress(double percent = -1)
    {
        if (percent < 0)
        {
            ProgressIsIndeterminate = true;
        }
        else
        {
            ProgressIsIndeterminate = false;
            ProgressValue = Math.Clamp(percent, 0, 100);
        }
    }

    /// <summary>预览缩放倍率（0.5~3），变化时联动刷新图片高度。</summary>
    private double previewZoom = 1.0;
    public double PreviewZoom
    {
        get => previewZoom;
        set
        {
            if (previewZoom != value)
            {
                this.RaiseAndSetIfChanged(ref previewZoom, value);
                this.RaisePropertyChanged(nameof(PreviewImageHeight));
            }
        }
    }

    /// <summary>预览图片显示高度 = 基准高度 × 缩放倍率。</summary>
    public double PreviewImageHeight => BasePreviewHeight * PreviewZoom;

    private const double BasePreviewHeight = 620;

    private string? _auditedFilePath;

    // ─────────────── 命令 ───────────────

    public ReactiveCommand<Unit, Unit> AddDocumentCommand { get; }
    public ReactiveCommand<DocumentInfo?, Unit> RemoveDocumentCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectStandardCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> AuditCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public async Task AddDroppedFilesAsync(string[] paths) => await AddDocumentsInternalAsync(paths);

    private async Task AddDocumentsAsync()
    {
        string[] files;
        try
        {
            files = await PickFilesAsync();
        }
        catch (Exception ex)
        {
            Log("打开文件选择器失败：" + ex.Message);
            return;
        }
        await AddDocumentsInternalAsync(files);
    }

    private async Task AddDocumentsInternalAsync(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var fi = new FileInfo(path);
            Documents.Add(new DocumentInfo { FilePath = path, FileName = fi.Name, FileSize = fi.Length });
            Log($"已添加文档：{fi.Name}");
        }
        SelectedDocument ??= Documents.FirstOrDefault();
        await Task.CompletedTask;
    }

    private async Task SelectStandardAsync()
    {
        string[] files;
        try
        {
            files = await PickFilesAsync("docx");
        }
        catch (Exception ex)
        {
            Log("打开文件选择器失败：" + ex.Message);
            return;
        }
        if (files.Length == 0) return;
        _standardDocumentService.SetDocument(files[0]);
        StandardDocumentName = _standardDocumentService.StandardDocumentName ?? "";
        Log($"已导入标准文档：{StandardDocumentName}");
        await Task.CompletedTask;
    }

    private async Task PreviewAsync()
    {
        var doc = Documents.FirstOrDefault(d => d.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
        if (doc == null)
        {
            Log("未找到 DOCX 文档");
            return;
        }
        IsBusy = true;
        RenderProgressVisible = true;
        SetProgress(-1);
        try
        {
            var renderProgress = new Progress<(int Page, int Total)>(p =>
                SetProgress(p.Page * 100.0 / p.Total));
            doc.PngImages = await _convertService.ConvertDocxToPngAsync(doc.FilePath, renderProgress);
            Log($"预览转换完成，共 {doc.PngImages.Count} 页");
        }
        finally
        {
            RenderProgressVisible = false;
            IsBusy = false;
        }
    }

    private async Task AuditAsync()
    {
        var doc = Documents.FirstOrDefault();
        if (doc == null)
        {
            Log("未找到上传的文档");
            return;
        }

        IsBusy = true;
        AuditProgressVisible = true;
        SetProgress(-1);
        try
        {
            // 不直接操作原文档：复制一份“_ai审核”副本，所有审核标注均在副本上进行
            var copyPath = Path.Combine(
                Path.GetDirectoryName(doc.FilePath)!,
                Path.GetFileNameWithoutExtension(doc.FilePath) + "_ai审核" + Path.GetExtension(doc.FilePath));
            File.Copy(doc.FilePath, copyPath, overwrite: true);
            Log($"已创建审核副本：{Path.GetFileName(copyPath)}（原文档保持不变）");

            // 组装标准文档（条款 + 标准文档内容）
            StandardDocument? standard = null;
            if (!string.IsNullOrWhiteSpace(AuditClausesText) || _standardDocumentService.Document is { HasContent: true })
            {
                standard = await _standardDocumentService.ExtractAndBuildAsync(AuditClausesText);
                Log($"审核基准已就绪（补充条款 {standard.AuditClauses.Length} 字 / 标准文档 {standard.DocumentText.Length} 字）");
            }

            var progress = new Progress<string>(msg => Log(msg));
            var percent = new Progress<double>(p => SetProgress(p));
            var outcome = await _auditService.AuditAsync(copyPath, standard, CustomPrompt, progress, default, percent);

            AuditResult = outcome.Formatted;
            _auditedFilePath = outcome.AuditedFilePath;
            IsAuditComplete = _auditedFilePath != null;
            SetProgress(100);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message is { Length: > 0 } m ? $"（{m}）" : "";
            Log("审核异常：" + ex.Message + inner);
            AuditResult = "审核失败：" + ex.Message + inner;
            SetProgress(0);
        }
        finally
        {
            AuditProgressVisible = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 导出 AI 审核后的文档副本（_ai审核.docx）到用户指定位置，原文件不受影响。
    /// </summary>
    private async Task ExportAsync()
    {
        if (string.IsNullOrEmpty(_auditedFilePath) || !File.Exists(_auditedFilePath))
        {
            Log("没有可导出的审核文档");
            return;
        }
        var topLevel = App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow;
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 AI 审核文档",
            SuggestedFileName = Path.GetFileName(_auditedFilePath),
            DefaultExtension = "docx",
            FileTypeChoices = [new FilePickerFileType("Word 文档") { Patterns = ["*.docx"] }],
        });
        if (file == null) return;

        var dest = file.Path.LocalPath;
        File.Copy(_auditedFilePath, dest, overwrite: true);
        Log($"AI 审核文档已导出：{dest}");
    }

    private async Task RefreshPreviewAsync()
    {
        if (string.IsNullOrEmpty(_auditedFilePath) || !File.Exists(_auditedFilePath))
        {
            Log("审核后的文件不存在");
            return;
        }
        var doc = Documents.FirstOrDefault(d => d.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
        if (doc == null) return;
        IsBusy = true;
        RenderProgressVisible = true;
        SetProgress(-1);
        try
        {
            var renderProgress = new Progress<(int Page, int Total)>(p =>
                SetProgress(p.Page * 100.0 / p.Total));
            doc.PngImages = await _convertService.ConvertDocxToPngAsync(_auditedFilePath, renderProgress);
            Log($"审核后文档预览刷新完成，共 {doc.PngImages.Count} 页");
        }
        finally
        {
            RenderProgressVisible = false;
            IsBusy = false;
        }
    }

    private void Clear()
    {
        Documents.Clear();
        AuditResult = "";
        LogMessage = "";
        IsAuditComplete = false;
        _auditedFilePath = null;
        ProgressValue = 0;
        RenderProgressVisible = false;
        AuditProgressVisible = false;
        Log("已清除所有文档和审核数据");
    }

    private void Log(string msg) => LogMessage += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";

    private async Task<string[]> PickFilesAsync(string kind = "docx")
    {
        var topLevel = App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow;
        if (topLevel == null)
        {
            Log("当前没有可用的主窗口，无法打开文件选择器");
            return [];
        }
        var patterns = kind == "docx" ? new[] { "*.docx", "*.doc" } : new[] { "*.docx" };
        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == "docx" ? "选择标准维修文档" : "选择待审文档",
            AllowMultiple = kind != "docx",
            FileTypeFilter = [new FilePickerFileType("Word 文档") { Patterns = patterns }],
        });
        return result.Select(f => f.Path.LocalPath).ToArray();
    }
}
