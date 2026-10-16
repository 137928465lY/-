using System;

namespace 叶片缺陷审核.Models;

/// <summary>
/// 授权信息（AES 加密后存 license.dat）。
/// </summary>
public class LicenseInfo
{
    public string Phone { get; set; } = "";

    /// <summary>本机 MAC 的 SHA256 指纹（机器绑定）。</summary>
    public string MacHash { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>激活时的版本标记，便于后续升级校验。</summary>
    public string AppVersion { get; set; } = "1.0";
}

/// <summary>
/// 激活结果。
/// </summary>
public class ActivationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    /// <summary>激活成功时由授权文件派生的登录密码（格式 XXXX-XXXX）。</summary>
    public string? Password { get; init; }
}
