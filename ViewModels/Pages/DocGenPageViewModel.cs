using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using 叶片缺陷审核.Models;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.ViewModels.Pages;

public class DocGenPageViewModel : PageBase
{
    private readonly DocGenAgentService _agent;
    private readonly DocxBuilderService _docxBuilder;
    private readonly KnowledgeBaseService _knowledgeBase;
    private readonly DocumentConvertService _convertService;
    private readonly DocxStyleService _docxStyleService;
    private readonly ISukiToastManager _toastManager;
    private readonly ISukiDialogManager _dialogManager;

    public DocGenPageViewModel(DocGenAgentService agent, DocxBuilderService docxBuilder,
        KnowledgeBaseService knowledgeBase, DocumentConvertService convertService,
        DocxStyleService docxStyleService,
        ISukiToastManager toastManager, ISukiDialogManager dialogManager)
        : base("文档生成", "\ue604", 2, "业务功能")
    {
        _agent = agent;
        _docxBuilder = docxBuilder;
        _knowledgeBase = knowledgeBase;
        _convertService = convertService;
        _docxStyleService = docxStyleService;
        _toastManager = toastManager;
        _dialogManager = dialogManager;

        // 默认使用内嵌标准输出模板；若用户曾导入模板则沿用
        var embeddedTemplate = Path.Combine(AppContext.BaseDirectory, "SeedKB", "reference_template.docx");
        var userTemplate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "叶片缺陷审核", "output_template.docx");
        if (File.Exists(userTemplate))
        {
            _style = _docxStyleService.Extract(userTemplate);
            TemplateName = "导入：" + _importedTemplateName;
        }
        else
        {
            if (File.Exists(embeddedTemplate))
                _style = _docxStyleService.Extract(embeddedTemplate);
            TemplateName = "内嵌默认模板";
        }
        TableThreeLine = _style.TableThreeLine;

        foreach (var b in _knowledgeBase.Bases.Where(b => !b.Hidden)) { b.IsSelected = true; Bases.Add(b); }

        GenerateCommand = ReactiveCommand.CreateFromTask(GenerateAsync, this.WhenAnyValue(x => x.IsBusy, b => !b));
        CancelCommand = ReactiveCommand.Create(_agent.Cancel);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, this.WhenAnyValue(x => x.CanSave));
        ImportTemplateCommand = ReactiveCommand.CreateFromTask(ImportTemplateAsync);
        ApplyRevisionCommand = ReactiveCommand.CreateFromTask(ApplyRevisionAsync,
            this.WhenAnyValue(x => x.IsBusy, b => !b).CombineLatest(this.WhenAnyValue(x => x.CanSave),
                (notBusy, canSave) => notBusy && canSave));
    }

    private DocxStyle _style = new();
    private string _importedTemplateName = "";

    /// <summary>当前输出模板来源显示。</summary>
    [Reactive] public string TemplateName { get; set; } = "内嵌默认模板";

    private string? _userTemplatePath;

    public ReactiveCommand<Unit, Unit> ApplyRevisionCommand { get; }

    [Reactive] public string RevisionText { get; set; } = "";

    /// <summary>
    /// 导入标准输出模板 docx：提取段落字体、字号、表格样式；同时作为生成时的格式来源。
    /// </summary>
    private async Task ImportTemplateAsync()
    {
        var topLevel = App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow;
        if (topLevel == null) return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入标准输出模板",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Word 模板文档") { Patterns = ["*.docx"] }],
            });
            if (files.Count == 0) return;
            var source = files[0].Path.LocalPath;

            // 拷贝到应用数据目录，跨启动沿用
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "叶片缺陷审核", "output_template.docx");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);

            _style = _docxStyleService.Extract(dest);
            TableThreeLine = _style.TableThreeLine;
            _importedTemplateName = Path.GetFileNameWithoutExtension(source);
            _userTemplatePath = dest;
            TemplateName = "导入：" + _importedTemplateName;
            ShowToast($"已导入模板并提取样式（字体 {_style.FontName} / 正文 {_style.BodyPt}pt / " +
                      $"标题 {_style.HeadingPt}pt / {(TableThreeLine ? "三线表" : "全框线")}）", NotificationType.Success);
        }
        catch (Exception ex)
        {
            ShowToast("模板导入失败：" + ex.Message, NotificationType.Error);
        }
    }

    public ObservableCollection<KnowledgeBaseInfo> Bases { get; } = [];

    /// <summary>勾选的知识库（多选；空 = 检索全部）。</summary>
    public List<string> SelectedBaseIds =>
        Bases.Where(b => b.IsSelected).Select(b => b.Id).ToList();

    [Reactive] public string DefectInfo { get; set; } = "";
    [Reactive] public bool TableThreeLine { get; set; } = true;
    /// <summary>快速模式：跳过审校步骤（草稿即终稿，省约 1/3 时间）。</summary>
    [Reactive] public bool FastMode { get; set; } = true;
    [Reactive] public string Company { get; set; } = "XXX有限公司";
    [Reactive] public string Version { get; set; } = "A1.0";

    [Reactive] public bool IsBusy { get; set; }
    [Reactive] public bool CanSave { get; set; }

    // ── 预览区进度条（-1 表示不确定进度；点击生成显示，渲染完成消失） ──
    [Reactive] public double ProgressValue { get; set; }
    [Reactive] public bool ProgressIsIndeterminate { get; set; }
    [Reactive] public bool RenderProgressVisible { get; set; }

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

    /// <summary>Toast 通知（SukiToastManager，参考 KN1 用法）。</summary>
    private void ShowToast(string message, NotificationType type)
    {
        _toastManager.CreateToast()
            .OfType(type)
            .WithTitle("文档生成")
            .WithContent(message)
            .Dismiss().After(TimeSpan.FromSeconds(6))
            .Queue();
    }

    /// <summary>生成文档的预览图片（临时 docx 渲染，与审核页预览逻辑一致）。</summary>
    public ObservableCollection<Bitmap> PreviewImages { get; } = [];

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

    private DocGenDocument? _document;
    private string? _tempDocxPath;
    private DocGenInput? _lastInput;

    public ReactiveCommand<Unit, Unit> GenerateCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportTemplateCommand { get; }

    /// <summary>
    /// 应用修改意见：确认后按意见修订文档 HTML，重建临时 docx 并刷新预览。
    /// </summary>
    private async Task ApplyRevisionAsync()
    {
        if (_document == null || _tempDocxPath == null || _lastInput == null) return;
        if (string.IsNullOrWhiteSpace(RevisionText))
        {
            ShowToast("请先填写修改意见", NotificationType.Warning);
            return;
        }
        var topLevel = App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow;
        if (topLevel == null) return;

        var confirmed = await _dialogManager.CreateDialog()
            .WithTitle("应用修改意见")
            .WithContent("是否根据修改意见修改文档？修订后将重新渲染预览。")
            .WithYesNoResult("确认修改", "取消")
            .Dismiss().ByClickingBackground()
            .TryShowAsync();
        if (confirmed != true) return;

        IsBusy = true;
        RenderProgressVisible = true;
        SetProgress(-1);
        try
        {
            ShowToast("正在按修改意见修订文档…", NotificationType.Information);
            var oldTitle = _document.Title;
            await _agent.ApplyRevisionAsync(_document, RevisionText, default);

            await RebuildPreviewAsync();
            if (_document.Title != oldTitle)
            {
                ShowToast($"文档标题已更新为《{_document.Title}》，封面已同步", NotificationType.Success);
            }
            else
            {
                ShowToast("修订完成，预览已更新（可继续填写意见或保存 .docx）", NotificationType.Success);
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast("已取消", NotificationType.Warning);
        }
        catch (Exception ex)
        {
            ShowToast("修订失败：" + ex.Message, NotificationType.Error);
        }
        finally
        {
            RenderProgressVisible = false;
            IsBusy = false;
        }
    }

    /// <summary>用当前文档重建临时 docx 并渲染预览图片。</summary>
    private async Task RebuildPreviewAsync()
    {
        // 同步左侧输入框的最新封面信息（公司名/版本/表格样式）
        if (_lastInput != null)
        {
            _lastInput.Company = Company;
            _lastInput.Version = Version;
            _lastInput.TableThreeLine = TableThreeLine;
            _lastInput.FastMode = FastMode;
            _lastInput.Style = _style;
        }

        SetProgress(-1);
        var tempDir = Path.Combine(Path.GetTempPath(), "叶片缺陷审核", "preview");
        _tempDocxPath = await _docxBuilder.BuildAsync(_document!, _lastInput!, tempDir);

        var renderProgress = new Progress<(int Page, int Total)>(p =>
            SetProgress(p.Page * 100.0 / p.Total));
        PreviewImages.Clear();
        foreach (var img in await _convertService.ConvertDocxToPngAsync(_tempDocxPath, renderProgress))
        {
            PreviewImages.Add(img);
        }
    }

    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(DefectInfo))
        {
            ShowToast("请先填写缺陷信息", NotificationType.Warning);
            return;
        }

        IsBusy = true;
        CanSave = false;
        PreviewImages.Clear();
        _document = null;
        _tempDocxPath = null;
        RenderProgressVisible = true;   // 进度条从点击生成起显示
        SetProgress(-1);
        try
        {
            // 知识库种子可能在页面构造后才完成合并，生成前补拉一次（隐藏库不显示，仅供 Agent 内部参考）
            if (Bases.Count == 0)
                foreach (var b in _knowledgeBase.Bases.Where(b => !b.Hidden)) { b.IsSelected = true; Bases.Add(b); }

            // Agent 步骤/检索命中 → Toast（PDF 逐页识别为高频子进度，不弹 Toast）
            var progress = new Progress<AgentStep>(s =>
            {
                if (s.Name.StartsWith("识别 PDF 第")) return;
                ShowToast($"Agent 执行中 {s.Index}/3：{s.Name}", NotificationType.Information);
            });

            var input = new DocGenInput
            {
                DefectInfo = DefectInfo,
                KnowledgeBaseIds = SelectedBaseIds,
                TableThreeLine = TableThreeLine,
                FastMode = FastMode,
                Company = Company,
                Version = Version,
                Style = _style,
            };
            _lastInput = input;

            _document = await _agent.GenerateAsync(input, progress);

            // 生成完成：构建临时 docx 并渲染为图片预览（与文档审核页预览逻辑一致）；
            // 用户点【保存】才复制到正式位置，否则临时文件随后被覆盖/清理
            await RebuildPreviewAsync();

            CanSave = true;
            ShowToast($"生成完成：《{_document.Title}》（共 {PreviewImages.Count} 页预览），可点击【保存 .docx】导出",
                NotificationType.Success);
        }
        catch (OperationCanceledException)
        {
            ShowToast("已取消", NotificationType.Warning);
        }
        catch (Exception ex)
        {
            ShowToast("生成失败：" + ex.Message, NotificationType.Error);
        }
        finally
        {
            // 渲染完成（或失败/取消）后进度条消失
            RenderProgressVisible = false;
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_tempDocxPath == null || !File.Exists(_tempDocxPath)) return;
        var topLevel = App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow;
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存维修文档",
            SuggestedFileName = _document?.Title ?? "叶片缺陷维修文档",
            DefaultExtension = "docx",
            FileTypeChoices = [new FilePickerFileType("Word 文档") { Patterns = ["*.docx"] }],
        });
        if (file == null) return;

        var dest = file.Path.LocalPath;
        File.Copy(_tempDocxPath, dest, overwrite: true);
        ShowToast($"已保存：{dest}", NotificationType.Success);
    }
}
