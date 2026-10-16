using System.Collections.Generic;
using ReactiveUI;
using 叶片缺陷审核.ViewModels;

namespace 叶片缺陷审核.Models;

/// <summary>
/// 页面基类：主窗口侧边菜单的页面 ViewModel 基类（参考 KN1）。
/// </summary>
public abstract class PageBase(string displayName, string iconCode, int index, string groupName) : ViewModelBase
{
    public string DisplayName { get; } = displayName;

    public string IconCode { get; } = iconCode;

    /// <summary>菜单内排序（越小越靠前）。</summary>
    public int Index { get; } = index;

    /// <summary>菜单分组名（相同分组的页面归入同一级节点）。</summary>
    public string GroupName { get; } = groupName;

    /// <summary>子项（分组节点覆写；普通页面为 null 即叶子节点）。</summary>
    public virtual List<ViewModelBase>? SubItems => null;

    /// <summary>是否为一级分组节点（用于导航字号区分）。</summary>
    public bool IsGroup => this is MenuGroupNode;

    /// <summary>导航文字字号（一级分组大一号）。</summary>
    public double TitleSize => IsGroup ? 15.0 : 12.0;

    /// <summary>导航图标字号（一级分组大一号）。</summary>
    public double IconSize => IsGroup ? 20.0 : 16.0;

    private bool isGroupExpanded;

    /// <summary>
    /// 二级导航展开状态（仅分组节点有意义；放在基类以便 XAML 编译绑定解析）。
    /// 绑定 SukiSideMenuItem.IsExpanded；点击分组行由 MainWindow 代码后置切换。
    /// </summary>
    public bool IsGroupExpanded
    {
        get => isGroupExpanded;
        set => this.RaiseAndSetIfChanged(ref isGroupExpanded, value);
    }
}

/// <summary>
/// 菜单分组节点：一级节点，承载同组页面的子项集合。
/// </summary>
public class MenuGroupNode(string displayName, int index, string iconCode = "") : PageBase(displayName, iconCode, index, "")
{
    public override List<ViewModelBase>? SubItems { get; } = [];
}
