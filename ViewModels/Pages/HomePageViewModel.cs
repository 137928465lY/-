using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using 叶片缺陷审核.Models;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.ViewModels.Pages;

public class HomePageViewModel : PageBase
{
    private readonly KnowledgeBaseService _knowledgeBase;
    private readonly DocImporterService _importer;
    private readonly LLMService _llm;
    private readonly ISukiDialogManager _dialogManager;
    private readonly ISukiToastManager _toastManager;

    public HomePageViewModel(KnowledgeBaseService knowledgeBase, DocImporterService importer, LLMService llm,
        ISukiDialogManager dialogManager, ISukiToastManager toastManager)
        : base("知识库", "\ue60c", 0, "业务功能")
    {
        _knowledgeBase = knowledgeBase;
        _importer = importer;
        _llm = llm;
        _dialogManager = dialogManager;
        _toastManager = toastManager;

        CreateBaseCommand = ReactiveCommand.Create(CreateBase);
        DeleteBaseCommand = ReactiveCommand.Create(DeleteBase);
        ImportFileCommand = ReactiveCommand.CreateFromTask(ImportFileAsync);
        DeleteEntryCommand = ReactiveCommand.Create(DeleteEntry);
        SearchCommand = ReactiveCommand.Create(DoSearch);
        AskCommand = ReactiveCommand.CreateFromTask(AskAsync);
        CancelAskCommand = ReactiveCommand.Create(_llm.Cancel);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _knowledgeBase.InitializeAsync();
        Bases.Clear();
        foreach (var b in _knowledgeBase.Bases.Where(b => !b.Hidden)) Bases.Add(b);
        SelectedBase = Bases.FirstOrDefault();
        RefreshEntries();
    }

    // ─────────────── 知识库 ───────────────

    public ObservableCollection<KnowledgeBaseInfo> Bases { get; } = [];

    private KnowledgeBaseInfo? selectedBase;
    public KnowledgeBaseInfo? SelectedBase
    {
        get => selectedBase;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedBase, value);
            RefreshEntries();
        }
    }

    private string newBaseName = "";
    public string NewBaseName { get => newBaseName; set => this.RaiseAndSetIfChanged(ref newBaseName, value); }

    public ObservableCollection<KnowledgeEntry> Entries { get; } = [];

    private KnowledgeEntry? selectedEntry;
    public KnowledgeEntry? SelectedEntry
    {
        get => selectedEntry;
        set => this.RaiseAndSetIfChanged(ref selectedEntry, value);
    }

    // ─────────────── 检索 ───────────────

    private string searchKeyword = "";
    public string SearchKeyword { get => searchKeyword; set => this.RaiseAndSetIfChanged(ref searchKeyword, value); }

    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];

    /// <summary>Toast 通知（SukiToastManager，参考 KN1 用法）；2 秒内相同内容去重，避免高频进度刷屏。</summary>
    private string _lastToastText = "";
    private DateTime _lastToastTime = DateTime.MinValue;

    private void ShowToast(string message, NotificationType type)
    {
        if (message == _lastToastText && (DateTime.Now - _lastToastTime).TotalSeconds < 2) return;
        _lastToastText = message;
        _lastToastTime = DateTime.Now;

        _toastManager.CreateToast()
            .OfType(type)
            .WithTitle("知识库")
            .WithContent(message)
            .Dismiss().After(TimeSpan.FromSeconds(6))
            .Queue();
    }

    // ─────────────── AI 问答 ───────────────

    private string question = "";
    public string Question { get => question; set => this.RaiseAndSetIfChanged(ref question, value); }

    private string answer = "";
    public string Answer { get => answer; set => this.RaiseAndSetIfChanged(ref answer, value); }

    /// <summary>操作状态提示。</summary>
    private string statusMessage = "";
    public string StatusMessage { get => statusMessage; set => this.RaiseAndSetIfChanged(ref statusMessage, value); }

    private bool isBusy;
    public bool IsBusy { get => isBusy; set => this.RaiseAndSetIfChanged(ref isBusy, value); }

    // ─────────────── 命令 ───────────────

    public ReactiveCommand<Unit, Unit> CreateBaseCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteBaseCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportFileCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> AskCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelAskCommand { get; }

    private void CreateBase()
    {
        var kb = _knowledgeBase.CreateBase(NewBaseName);
        Bases.Add(kb);
        SelectedBase = kb;
        NewBaseName = "";
    }

    private void DeleteBase()
    {
        if (SelectedBase == null) return;
        if (!_knowledgeBase.DeleteBase(SelectedBase.Id))
        {
            ShowForbiddenDialog($"内置知识库「{SelectedBase.Name}」禁止删除");
            return;
        }
        Bases.Remove(SelectedBase);
        SelectedBase = Bases.FirstOrDefault();
        ShowToast("已删除", NotificationType.Success);
    }

    private async Task ImportFileAsync()
    {
        if (SelectedBase == null)
        {
            ShowToast("请先选择知识库", NotificationType.Warning);
            return;
        }

        string[] files;
        try
        {
            files = await PickFilesAsync("导入文档");
        }
        catch (Exception ex)
        {
            ShowToast("打开文件选择器失败：" + ex.Message, NotificationType.Error);
            return;
        }
        if (files.Length == 0) return;

        IsBusy = true;
        try
        {
            foreach (var file in files)
            {
                ShowToast($"导入 {Path.GetFileName(file)}…", NotificationType.Information);
                try
                {
                    var entry = await _knowledgeBase.ImportAsync(SelectedBase.Id, file);
                    if (entry != null) Entries.Add(entry);
                }
                catch (Exception ex)
                {
                    ShowToast(ex.Message, NotificationType.Error);
                }
            }
            ShowToast("导入完成", NotificationType.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DeleteEntry()
    {
        if (SelectedBase == null || SelectedEntry == null) return;
        if (!_knowledgeBase.DeleteEntry(SelectedBase.Id, SelectedEntry.Id))
        {
            ShowForbiddenDialog($"内置条目「{SelectedEntry.Title}」禁止删除");
            return;
        }
        Entries.Remove(SelectedEntry);
        ShowToast("已删除", NotificationType.Success);
    }

    /// <summary>消息提示框（SukiDialog，参考 KN1 用法）。</summary>
    private void ShowForbiddenDialog(string content)
    {
        _dialogManager.CreateDialog()
            .WithTitle("操作受限")
            .WithContent(content)
            .WithOkResult("知道了")
            .Dismiss().ByClickingBackground()
            .TryShow();
    }

    private void RefreshEntries()
    {
        Entries.Clear();
        if (SelectedBase != null)
            foreach (var e in SelectedBase.Entries) Entries.Add(e);
    }

    private void DoSearch()
    {
        SearchResults.Clear();
        var baseIds = SelectedBase == null ? null : new[] { SelectedBase.Id };
        var results = _knowledgeBase.Search(SearchKeyword, baseIds);
        foreach (var r in results) SearchResults.Add(r);
        ShowToast($"检索到 {results.Count} 条", NotificationType.Information);
    }

    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Question))
        {
            ShowToast("请输入问题", NotificationType.Warning);
            return;
        }

        IsBusy = true;
        Answer = "";
        try
        {
            // 1) 检索知识库（PDF 命中时按需识别，显示进度）
            ShowToast("检索知识库…", NotificationType.Information);
            var baseIds = SelectedBase == null ? null : new[] { SelectedBase.Id };
            var hits = _knowledgeBase.Search(Question, baseIds, limit: 6);

            var contextBuilder = new System.Text.StringBuilder();
            var imageParts = new List<LlmMessagePart>();
            var pdfRecognized = 0;

            foreach (var hit in hits)
            {
                var content = await _knowledgeBase.GetContentAsync(hit.Entry,
                    new Progress<string>(s => ShowToast(s, NotificationType.Information)));

                if (hit.Entry.ArchiveType == "pdf" && content.Length > 0) pdfRecognized++;

                contextBuilder.AppendLine($"【{hit.Entry.Title}】");
                contextBuilder.AppendLine(content.Length > 3000 ? content[..3000] + "…" : content);
                contextBuilder.AppendLine();
            }

            // 2) 组装 RAG 上下文 + 多模态附图
            var parts = new List<LlmMessagePart>
            {
                LlmMessagePart.TextPart(
                    $"""
                    请根据以下知识库内容回答问题。如知识库中没有相关内容，请如实说明。

                    【知识库检索结果】
                    {contextBuilder}

                    【问题】{Question}
                    """),
            };
            parts.AddRange(imageParts);

            // 3) 流式回答
            ShowToast(pdfRecognized > 0 ? $"AI 回答中（已自动识别 {pdfRecognized} 个 PDF）…" : "AI 回答中…",
                NotificationType.Information);
            await foreach (var chunk in _llm.SendStreamAsync([new LlmMessage { Role = "user", Parts = parts }]))
            {
                Answer += chunk;
            }
            ShowToast("回答完成", NotificationType.Success);
        }
        catch (OperationCanceledException)
        {
            ShowToast("已取消", NotificationType.Warning);
        }
        catch (Exception ex)
        {
            ShowToast("出错了：" + ex.Message, NotificationType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string[]> PickFilesAsync(string title)
    {
        var topLevel = App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow;
        if (topLevel == null) return [];
        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("文档与图片")
                {
                    Patterns = ["*.txt", "*.md", "*.csv", "*.xlsx", "*.xls", "*.docx", "*.pdf", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"],
                },
            ],
        });
        return result.Select(f => f.Path.LocalPath).ToArray();
    }
}
