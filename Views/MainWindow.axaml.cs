using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SukiUI.Controls;
using 叶片缺陷审核.Models;
using 叶片缺陷审核.ViewModels;

namespace 叶片缺陷审核.Views;

public partial class MainWindow : SukiWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>最近一次选中的内容页（点击分组行后恢复选中用）。</summary>
    private PageBase? _lastSelectedPage;

    /// <summary>
    /// 一级导航整行可点：点击分组行的图标/文字区域时，TreeView 基类会把组节点设为选中项
    /// （无内容页，界面无变化）；此处拦截该选中，转义为展开/收起二级导航并恢复原页面选中。
    /// </summary>
    private void SideMenu_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SideMenu.SelectedItem is MenuGroupNode group)
        {
            group.IsGroupExpanded = !group.IsGroupExpanded;

            var restore = _lastSelectedPage ?? (DataContext as MainWindowViewModel)?.ActivePage;
            if (restore != null)
                SideMenu.SelectedItem = restore;
            return;
        }

        if (SideMenu.SelectedItem is PageBase page)
            _lastSelectedPage = page;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = Avalonia.Controls.WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
        // 图标随状态切换（KN1 iconfont：e620=最大化 / e623=还原）
        MaxIcon.Text = WindowState == Avalonia.Controls.WindowState.Maximized ? "\ue623" : "\ue620";
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
