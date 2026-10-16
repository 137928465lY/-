using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using 叶片缺陷审核.Common;
using 叶片缺陷审核.ViewModels;

namespace 叶片缺陷审核;

/// <summary>
/// 根据 ViewModel 从 SukiViews 映射表解析对应视图（ViewModel 由 IoC 容器注入）。
/// </summary>
public class ViewLocator(SukiViews views) : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        // 未映射的类型（如一级导航分组节点）显示空白，不创建页面
        return views.TryCreateView(param, out var view) ? view : new Grid();
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
