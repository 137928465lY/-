using System;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.ViewModels;

public class LicenseWindowViewModel : ViewModelBase
{
    private readonly LicenseService _licenseService;
    private readonly SmtpEmailService _emailService;

    public LicenseWindowViewModel(LicenseService licenseService, SmtpEmailService emailService)
    {
        _licenseService = licenseService;
        _emailService = emailService;
        isActivateMode = !licenseService.IsLicensed();
        ActivateCommand = ReactiveCommand.Create(Activate);
        LoginCommand = ReactiveCommand.Create(Login);
    }

    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }

    public ReactiveCommand<Unit, Unit> LoginCommand { get; }

    /// <summary>回到激活模式（删除授权后调用）。</summary>
    public void SwitchToActivateMode()
    {
        isActivateMode = true;
        this.RaisePropertyChanged(nameof(IsActivateMode));
    }

    /// <summary>登录成功（打开主窗口）。</summary>
    public event Action? RequestOpenMainWindow;

    /// <summary>是否处于激活模式（无有效授权时）。</summary>
    public bool IsActivateMode => isActivateMode;

    private bool isActivateMode;

    private string phone = "";
    public string Phone
    {
        get => phone;
        set => this.RaiseAndSetIfChanged(ref phone, value);
    }

    private string password = "";
    public string Password
    {
        get => password;
        set => this.RaiseAndSetIfChanged(ref password, value);
    }

    private string message = "";
    public string Message
    {
        get => message;
        set => this.RaiseAndSetIfChanged(ref message, value);
    }

    /// <summary>激活成功后显示的派生密码。</summary>
    private string generatedPassword = "";
    public string GeneratedPassword
    {
        get => generatedPassword;
        set => this.RaiseAndSetIfChanged(ref generatedPassword, value);
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        set => this.RaiseAndSetIfChanged(ref isBusy, value);
    }

    public async void Activate()
    {
        IsBusy = true;
        Message = "";
        GeneratedPassword = "";
        try
        {
            var result = await Task.Run(() => _licenseService.Activate(Phone));
            if (!result.Success)
            {
                Message = result.Message;
                return;
            }

            // 派生密码只通过邮件发送给管理员，界面上不显示
            var machine = Environment.MachineName;
            var (ok, error) = await _emailService.SendAsync(
                $"【叶片维修工程文件编制审核系统】新激活申请 - {Phone}",
                $"""
                有新的软件激活：

                手机号：{Phone}
                机器名：{machine}
                操作系统：{Environment.OSVersion.VersionString}
                激活时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}

                该机器的登录密码为：{result.Password}

                请将以上密码告知客户。用户名即客户手机号。
                """);
            if (ok)
            {
                Message = "激活成功！登录密码已发送给管理员，请联系管理员获取（用户名为手机号）。";
                isActivateMode = false;
                this.RaisePropertyChanged(nameof(IsActivateMode));
            }
            else
            {
                // 邮件发送失败：授权已生成，提示重新激活重试（重新激活会生成新密码并重发邮件）
                Message = $"激活已生成，但密码邮件发送失败：{error}。请检查网络后重新激活（将重发邮件）。";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async void Login()
    {
        IsBusy = true;
        Message = "";
        try
        {
            var result = await Task.Run(() => _licenseService.Login(Phone, Password));
            if (result.Success)
            {
                RequestOpenMainWindow?.Invoke();
            }
            else
            {
                Message = result.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
