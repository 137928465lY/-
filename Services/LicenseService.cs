using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 叶片缺陷审核.Models;

namespace 叶片缺陷审核.Services;

public class LicenseService 
{
    private const string AppSalt = "YePianQueXianShenHe.v1";
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "叶片缺陷审核");
    private static readonly string LicensePath = Path.Combine(DataDir, "license.dat");

    public bool IsLicensed()
    {
        try
        {
            if (!File.Exists(LicensePath)) return false;
            // 文件能被本机任一候选 MAC 派生密钥解开即视为有效
            return TryReadLicense(File.ReadAllBytes(LicensePath), out _, null);
        }
        catch
        {
            return false;
        }
    }

    public ActivationResult Activate(string phone)
    {
        phone = NormalizePhone(phone);
        if (phone.Length != 11 || !phone.All(char.IsDigit))
        {
            return new ActivationResult { Success = false, Message = "请输入 11 位手机号" };
        }

        try
        {
            Directory.CreateDirectory(DataDir);

            // 主 MAC = 候选列表中排序最靠前者（确定性选择）
            var candidates = GetMacCandidates();
            if (candidates.Count == 0)
                return new ActivationResult { Success = false, Message = "无法获取本机 MAC 地址" };
            var primaryMac = candidates[0];

            var info = new LicenseInfo
            {
                Phone = phone,
                MacHash = Sha256Hex(primaryMac),
                CreatedAt = DateTime.Now,
            };

            // 密钥 = PBKDF2(固定盐 + 主 MAC)；手机号绑定通过登录密码 HMAC 校验
            var key = DeriveKey(primaryMac);
            var plain = JsonSerializer.SerializeToUtf8Bytes(info);
            var encrypted = AesEncrypt(plain, key);
            File.WriteAllBytes(LicensePath, encrypted);

            // 登录密码 = HMAC-SHA256(手机号 | MAC指纹 | 盐) 前 8 位 → XXXX-XXXX
            // 确定性派生：同一手机号 + 同一台机器（主 MAC 不变）永远得到同一个密码
            return new ActivationResult
            {
                Success = true,
                Password = DerivePassword(phone, info.MacHash),
                Message = "激活成功",
            };
        }
        catch (Exception ex)
        {
            return new ActivationResult { Success = false, Message = "激活失败：" + ex.Message };
        }
    }

    public LoginResult Login(string phone, string password)
    {
        phone = NormalizePhone(phone);
        var diag = new List<string>();
        try
        {
            if (!File.Exists(LicensePath))
            {
                return Fail(diag, "NoLicenseFile", "未找到授权文件：本机尚未激活成功，或授权已被删除");
            }

            var encrypted = File.ReadAllBytes(LicensePath);

            // 1) 机器校验：用当前任一在线网卡 MAC 尝试解密（容忍网卡状态变化），核对手机号
            if (!TryReadLicense(encrypted, out var info, diag))
            {
                return Fail(diag, "MachineMismatch",
                    "机器校验失败：授权文件无法用当前任何在线网卡解密。" +
                    "可能原因：①激活与登录不是同一台电脑；②激活后所有当时在用的网卡都已断开；③授权文件损坏。" +
                    "可点击右上角 ↻ 重新激活。");
            }

            if (info.Phone != phone)
            {
                diag.Add($"phoneMismatch: licensed={info.Phone}, input={phone}");
                WriteDebugLog(diag);
                return new LoginResult
                {
                    Success = false,
                    Reason = "PhoneMismatch",
                    Message = $"该机器的授权属于手机号 {MaskPhone(info.Phone)}，与当前输入不符",
                };
            }

            // 2) 密码校验：按确定性算法重算派生密码比对（双方统一去掉横杠后比较）
            var expected = NormalizePassword(DerivePassword(phone, info.MacHash));
            var entered = NormalizePassword(password);
            if (!string.Equals(expected, entered, StringComparison.Ordinal))
            {
                diag.Add($"passwordMismatch: expectedHash={Sha256Hex(expected)}, enteredHash={Sha256Hex(entered)}, enteredLen={entered.Length}");
                WriteDebugLog(diag);
                return new LoginResult
                {
                    Success = false,
                    Reason = "PasswordMismatch",
                    Message = "密码不匹配：请使用管理员最新邮件中的密码（含横杠或不含均可，如 B838-08EE 或 B83808EE）",
                };
            }

            WriteDebugLog(diag.Append("login=OK").ToList());
            return new LoginResult { Success = true, Message = "登录成功" };
        }
        catch (Exception ex)
        {
            return Fail(new List<string> { "exception: " + ex }, "LicenseCorrupted", "授权校验异常：" + ex.Message);
        }
    }

    private static LoginResult Fail(List<string> diag, string reason, string message)
    {
        diag.Add("reason=" + reason);
        WriteDebugLog(diag);
        return new LoginResult { Success = false, Reason = reason, Message = message };
    }

    private static string MaskPhone(string phone) =>
        phone.Length == 11 ? phone[..3] + "****" + phone[7..] : phone;

    /// <summary>诊断日志：%AppData%/叶片缺陷审核/license_debug.log（便于远程排查，不记录明文密码）。</summary>
    private static void WriteDebugLog(IEnumerable<string> lines)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {string.Join(" | ", lines)}";
            File.AppendAllText(Path.Combine(DataDir, "license_debug.log"), entry + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响登录
        }
    }

    // ─────────────── 内部实现 ───────────────

    /// <summary>
    /// 用本机所有在线网卡的候选 MAC 逐一尝试解密授权文件；
    /// 任一成功即认为机器匹配（容忍 Wi-Fi/以太网/VPN 等网卡状态在激活与登录之间发生变化）。
    /// </summary>
    private static bool TryReadLicense(byte[] encrypted, out LicenseInfo info, List<string>? diag)
    {
        info = new LicenseInfo();
        var candidates = GetMacCandidates();
        diag?.Add($"candidates={candidates.Count}");
        foreach (var mac in candidates)
        {
            try
            {
                var plain = AesDecrypt(encrypted, DeriveKey(mac));
                var parsed = JsonSerializer.Deserialize<LicenseInfo>(plain);
                if (parsed != null && parsed.MacHash == Sha256Hex(mac))
                {
                    info = parsed;
                    diag?.Add($"decrypt=OK macHash={Sha256Hex(mac)[..12]}");
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // 该候选 MAC 非激活时的主 MAC，继续尝试下一个
            }
            catch
            {
                // 忽略其他异常继续尝试
            }
        }
        return false;
    }

    private static string NormalizePhone(string phone) => (phone ?? "").Trim();

    private static string NormalizePassword(string password) =>
        (password ?? "").Trim().Replace("-", "").Replace("：", "").ToUpperInvariant();

    /// <summary>
    /// 候选 MAC 列表：所有非回环且在线网卡的物理地址，按地址排序保证确定性。
    /// </summary>
    private static List<string> GetMacCandidates()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetPhysicalAddress()?.ToString())
            .Where(a => !string.IsNullOrEmpty(a) && a != "000000000000")
            .Select(a => a!.ToLowerInvariant())
            .Distinct()
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();
    }

    private static byte[] DeriveKey(string mac) =>
        Pbkdf2($"{AppSalt}|{mac}");

    private static byte[] Pbkdf2(string material) =>
        Rfc2898DeriveBytes.Pbkdf2(material, Encoding.UTF8.GetBytes(AppSalt), 100_000, HashAlgorithmName.SHA256, 32);

    /// <summary>
    /// 确定性密码派生：HMAC-SHA256(手机号, 手机号|MAC指纹|盐) 前 8 位 → XXXX-XXXX。
    /// 同一手机号 + 同一台机器（激活主 MAC 不变）重复激活得到相同密码。
    /// </summary>
    private static string DerivePassword(string phone, string macHash)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(phone));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{phone}|{macHash}|{AppSalt}")));
        return hex[..8].Insert(4, "-");
    }

    private static byte[] AesEncrypt(byte[] plain, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
        return [.. aes.IV, .. cipher];
    }

    private static byte[] AesDecrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = data[..16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 16, data.Length - 16);
    }

    private static string Sha256Hex(string material)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
/// <summary>登录结果（诊断模式：失败时给出具体原因）。</summary>
public class LoginResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? Reason { get; init; }
}