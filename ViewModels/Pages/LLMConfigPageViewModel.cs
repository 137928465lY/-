using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using ReactiveUI;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using 叶片缺陷审核.Models;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.ViewModels.Pages;

public class LLMConfigPageViewModel : PageBase
{
    private readonly LLMService _llm;
    private readonly ISukiDialogManager _dialogManager;
    private readonly ISukiToastManager _toastManager;

    public LLMConfigPageViewModel(LLMService llm, ISukiDialogManager dialogManager, ISukiToastManager toastManager)
        : base("模型配置", "\ue648", 3, "系统配置")
    {
        _llm = llm;
        _dialogManager = dialogManager;
        _toastManager = toastManager;

        NewModelCommand = ReactiveCommand.Create(NewModel);
        AddModelCommand = ReactiveCommand.Create(AddModel);
        DeleteModelCommand = ReactiveCommand.Create(DeleteModel);
        EnableModelCommand = ReactiveCommand.Create(EnableModel);
        SaveModelCommand = ReactiveCommand.Create(SaveModel);
        TestCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowToast("正在测试连接…", NotificationType.Information);
            var (ok, msg) = await _llm.TestConnectionAsync(EditingModel ?? _llm.Current ?? new LlmModelConfig());
            ShowToast(msg, ok ? NotificationType.Success : NotificationType.Error);
        });

        // 进入页面即显示当前启用的模型参数；无任何模型时显示空白新建表单
        EditingModel = _llm.Current ?? Models.FirstOrDefault() ?? NewModelInstance();
    }

    public ObservableCollection<LlmModelConfig> Models => _llm.Models;

    private LlmModelConfig? editingModel;
    public LlmModelConfig? EditingModel
    {
        get => editingModel;
        set => this.RaiseAndSetIfChanged(ref editingModel, value);
    }

    /// <summary>Toast 通知（SukiToastManager，参考 KN1 用法）。</summary>
    private void ShowToast(string message, NotificationType type)
    {
        _toastManager.CreateToast()
            .OfType(type)
            .WithTitle("模型配置")
            .WithContent(message)
            .Dismiss().After(TimeSpan.FromSeconds(6))
            .Queue();
    }

    public ReactiveCommand<Unit, Unit> NewModelCommand { get; }
    public ReactiveCommand<Unit, Unit> AddModelCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteModelCommand { get; }
    public ReactiveCommand<Unit, Unit> EnableModelCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveModelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestCommand { get; }

    private LlmModelConfig NewModelInstance() => new() { Name = "新模型" };

    private void NewModel()
    {
        EditingModel = NewModelInstance();
    }

    private void SaveModel()
    {
        if (EditingModel == null || string.IsNullOrWhiteSpace(EditingModel.Name))
        {
            ShowDialog("请先填写配置名称");
            return;
        }

        var name = EditingModel.Name.Trim();
        if (Models.Any(m => m.Name.Trim() == name && !ReferenceEquals(m, EditingModel)))
        {
            ShowDialog($"已存在同名模型配置「{name}」，请更换名称，或从左侧列表选中后直接修改");
            return;
        }

        if (!Models.Contains(EditingModel))
        {
            _llm.AddModel(EditingModel);
        }
        _llm.Persist();
        ShowDialog($"模型配置「{name}」已保存");
    }

    private void AddModel() => SaveModel();

    private void DeleteModel()
    {
        if (EditingModel == null) return;
        var name = EditingModel.Name;
        _llm.RemoveModel(EditingModel);
        EditingModel = Models.FirstOrDefault();
        ShowDialog($"模型配置「{name}」已删除");
    }

    private void EnableModel()
    {
        if (EditingModel == null) return;
        SaveModel();
        if (!Models.Contains(EditingModel)) return; // 重名被拒时中止
        _llm.SelectModel(EditingModel);
        ShowDialog($"已启用模型：{EditingModel.Name}");
    }

    private void ShowDialog(string content)
    {
        _dialogManager.CreateDialog()
            .WithTitle("提示")
            .WithContent(content)
            .WithOkResult("知道了")
            .Dismiss().ByClickingBackground()
            .TryShow();
    }
}
