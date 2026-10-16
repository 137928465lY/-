using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using 叶片缺陷审核.Common;
using 叶片缺陷审核.Services;
using 叶片缺陷审核.ViewModels;
using 叶片缺陷审核.ViewModels.Pages;
using 叶片缺陷审核.Views;
using 叶片缺陷审核.Views.Pages;

namespace 叶片缺陷审核;

public class App : Application
{
    /// <summary>
    /// 全局 IoC 容器（应用启动时构建，供各处解析服务）。
    /// </summary>
    public static ServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();

            // 基础设施：桌面生命周期与配置文件（appsettings.json）
            services.AddSingleton(desktop);
            services.AddSingleton<IClassicDesktopStyleApplicationLifetime>(desktop);
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();
            services.AddSingleton<IConfiguration>(configuration);

            // 视图/视图模型注册（VM→View 映射 + ViewModel 注册进容器）
            var views = ConfigureViews(services);
            services.AddSingleton(views);

            Services = ConfigureServices(services);

            // 启动流程：授权窗口内部自动区分 激活模式 / 登录模式，登录成功后进主窗口
            DataTemplates.Add(new ViewLocator(views));
            desktop.MainWindow = Services.GetRequiredService<Func<LicenseWindow>>()();

            // 知识库初始化（内置种子合并）提前到应用启动，无需等待登录
            _ = Services.GetRequiredService<KnowledgeBaseService>().InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static SukiViews ConfigureViews(ServiceCollection services)
    {
        return new SukiViews()
            // 主窗口
            .AddView<MainWindow, MainWindowViewModel>(services)
            // 业务功能页
            .AddView<HomePage, HomePageViewModel>(services)
            .AddView<AuditPage, AuditPageViewModel>(services)
            .AddView<DocGenPage, DocGenPageViewModel>(services)
            // 系统配置页
            .AddView<LLMConfigPage, LLMConfigPageViewModel>(services)
            .AddView<SettingPage, SettingPageViewModel>(services);
    }

    private static ServiceProvider ConfigureServices(ServiceCollection services)
    {
        // ═══════════ 业务服务 ═══════════

        // SukiUI Toast / Dialog 管理器（主窗口与各页面共享同一实例）
        services.AddSingleton<ISukiToastManager>(new SukiToastManager());
        services.AddSingleton<ISukiDialogManager>(new SukiDialogManager());

        // 授权（手机号+MAC 机器绑定，密码邮件通知管理员）
        services.AddSingleton<LicenseService>();
        services.AddSingleton<SmtpEmailService>();

        // 统一 LLM 网关（OpenAI 兼容协议）
        services.AddSingleton<LLMService>();

        // 应用配置（领域字段清单等）
        services.AddSingleton<AppConfigService>();

        // 知识库（导入/检索/按需 PDF 识别）
        services.AddSingleton<DocImporterService>();
        services.AddSingleton<PdfRecognizeService>();
        services.AddSingleton<PdfImageExtractor>();
        services.AddSingleton<CaseStructuringService>();
        services.AddSingleton<KnowledgeBaseService>();

        // 文档审核管线（迁自 AiAuditDemo）
        services.AddSingleton<DocumentExtractService>();
        services.AddSingleton<DocumentConvertService>();
        services.AddSingleton<DocumentModifyService>();
        services.AddSingleton<StandardDocumentService>();
        services.AddSingleton<AuditService>();

        // 缺陷文档生成（Agent 流水线 + 真 .docx 构建，迁自 Qt 版）
        services.AddSingleton<DocGenAgentService>();
        services.AddSingleton<DocxBuilderService>();
        services.AddSingleton<DocxStyleService>();

        // ═══════════ 窗口工厂（Microsoft DI 不隐式支持 Func<T> 解析） ═══════════
        services.AddSingleton<Func<LicenseWindow>>(sp => () =>
        {
            var window = new LicenseWindow(
                new LicenseWindowViewModel(
                    sp.GetRequiredService<LicenseService>(),
                    sp.GetRequiredService<SmtpEmailService>()),
                sp.GetRequiredService<LicenseService>());
            return window;
        });

        services.AddSingleton<Func<MainWindow>>(sp => () =>
        {
            var window = new MainWindow();
            window.DataContext = sp.GetRequiredService<MainWindowViewModel>();
            return window;
        });

        return services.BuildServiceProvider();
    }
}
