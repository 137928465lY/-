using 叶片缺陷审核.Models;
using 叶片缺陷审核.Services;

namespace 叶片缺陷审核.ViewModels.Pages;

public class SettingPageViewModel : PageBase
{
    private readonly LicenseService _licenseService;
    private readonly AppConfigService _config;

    public SettingPageViewModel(LicenseService licenseService, AppConfigService config)
        : base("信息", "\ue600", 4, "系统配置")
    {
        _licenseService = licenseService;
        _config = config;

        LicenseInfo = _licenseService.IsLicensed()
            ? "本机已激活（授权与手机号+MAC 绑定，登录时校验）"
            : "本机尚未激活，请退出后在登录界面重新激活";
        DomainFieldsText = _config.DomainFields.Count > 0
            ? string.Join("、", _config.DomainFields)
            : "（未配置）";
    }

    public string LicenseInfo { get; }

    public string DomainFieldsText { get; }
}
