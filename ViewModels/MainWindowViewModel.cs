using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(IEnumerable<PageBase> pages,
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager)
    {
        ToastManager = toastManager;
        DialogManager = dialogManager;

        // 按 GroupName 分组组装菜单（组内按 Index 排序，组间按组内最小 Index 排序）
        var groupIcons = new Dictionary<string, string>
        {
            ["业务功能"] = "\ue62d",  // 闪电
            ["系统配置"] = "\ue609",  // 仪表盘
        };
        MenuItems = [.. pages
            .GroupBy(p => p.GroupName)
            .OrderBy(g => g.Min(p => p.Index))
            .Select(g =>
            {
                var node = new MenuGroupNode(g.Key, g.Min(p => p.Index), groupIcons.GetValueOrDefault(g.Key, ""));
                node.SubItems!.AddRange(g.OrderBy(p => p.Index).Cast<ViewModelBase>());
                return (MenuGroupNode)node;
            })];

        activePage = pages.OrderBy(p => p.Index).First();
    }

    public ISukiToastManager ToastManager { get; }

    public ISukiDialogManager DialogManager { get; }

    public IReadOnlyList<MenuGroupNode> MenuItems { get; }

    private PageBase? activePage;

    public PageBase? ActivePage
    {
        get => activePage;
        set
        {
            // 组节点不是内容页：点击一级分组行时由 MainWindow 代码后置转义为展开/收起，
            // 这里直接忽略，避免内容区被切到无视图的分组节点
            if (value is MenuGroupNode) return;
            this.RaiseAndSetIfChanged(ref activePage, value);
        }
    }
}
