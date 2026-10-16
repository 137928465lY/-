using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using 叶片缺陷审核.Services;
using 叶片缺陷审核.ViewModels;

namespace 叶片缺陷审核.Views;

public partial class LicenseWindow : Window
{
    private readonly LicenseService _licenseService;

    public LicenseWindow(LicenseWindowViewModel viewModel, LicenseService licenseService)
    {
        InitializeComponent();
        _licenseService = licenseService;
        DataContext = viewModel;
        viewModel.RequestOpenMainWindow += () =>
        {
            var main = new MainWindow { DataContext = App.Services.GetRequiredService<MainWindowViewModel>() };
            // 关键：把生命周期主窗口切到新窗口，否则文件选择器等挂在已关闭的登录窗口上会静默失败
            App.Services.GetRequiredService<IClassicDesktopStyleApplicationLifetime>().MainWindow = main;
            main.Show();
            Close();
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Reactivate_Click(object? sender, RoutedEventArgs e)
    {
        // 删除本机授权文件 → 回到激活模式
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "叶片缺陷审核", "license.dat");
        if (File.Exists(path)) File.Delete(path);

        if (DataContext is LicenseWindowViewModel vm)
        {
            vm.Phone = "";
            vm.Password = "";
            vm.GeneratedPassword = "";
            vm.Message = "";
            vm.SwitchToActivateMode();
        }
    }
}
