using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Common;

/// <summary>
/// 视图/视图模型映射注册表：维护 VM→View 映射，
/// 并将 ViewModel 注册进 IoC 容器（参考 KN1 的 SukiViews 实现）。
/// </summary>
public class SukiViews
{
    private readonly Dictionary<Type, Type> _vmToViewMap = [];

    /// <summary>
    /// 注册常驻页面：映射 VM→View，并将 ViewModel 注册为 Singleton。
    /// </summary>
    public SukiViews AddView<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(ServiceCollection services)
        where TView : ContentControl
        where TViewModel : ReactiveObject
    {
        var viewModelType = typeof(TViewModel);

        _vmToViewMap.Add(viewModelType, typeof(TView));

        // 页面 ViewModel 同时以 PageBase 注册（供主窗口菜单按分组组装）
        if (viewModelType.IsAssignableTo(typeof(PageBase)))
        {
            services.AddSingleton(typeof(PageBase), viewModelType);
        }
        else
        {
            services.AddSingleton(viewModelType);
        }

        return this;
    }

    /// <summary>
    /// 注册临时页面（每次导航创建独立实例）：仅映射 VM→View 并注册为 Transient。
    /// </summary>
    public SukiViews AddTransientView<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(ServiceCollection services)
        where TView : ContentControl
        where TViewModel : ReactiveObject
    {
        _vmToViewMap.Add(typeof(TViewModel), typeof(TView));
        services.AddTransient<TViewModel>();

        return this;
    }

    public bool TryCreateView(IServiceProvider provider, Type viewModelType, [NotNullWhen(true)] out Control? view)
    {
        var viewModel = provider.GetRequiredService(viewModelType);
        return TryCreateView(viewModel, out view);
    }

    public bool TryCreateView(object? viewModel, [NotNullWhen(true)] out Control? view)
    {
        view = null;

        if (viewModel == null)
        {
            return false;
        }

        if (_vmToViewMap.TryGetValue(viewModel.GetType(), out var viewType))
        {
            view = Activator.CreateInstance(viewType) as Control;

            if (view != null)
            {
                view.DataContext = viewModel;
            }
        }

        return view != null;
    }

    public Control? CreateView<TViewModel>(IServiceProvider provider) where TViewModel : ReactiveObject
    {
        return TryCreateView(provider, typeof(TViewModel), out var view) ? view : null;
    }
}
