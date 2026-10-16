using System;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace 叶片缺陷审核.Services;

/// <summary>
/// SMTP 邮件服务：appsettings.json "Smtp" 节配置（Host/Port/From/AuthCode/To）。
/// 默认 QQ 邮箱：smtp.qq.com:587（STARTTLS），AuthCode 为 QQ 邮箱设置里生成的授权码。
/// </summary>
public class SmtpEmailService 
{
    private readonly IConfiguration _configuration;

    public SmtpEmailService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<(bool Ok, string Error)> SendAsync(string subject, string body, CancellationToken ct = default)
    {
        try
        {
            var host = _configuration["Smtp:Host"];
            var port = int.TryParse(_configuration["Smtp:Port"], out var p) ? p : 587;
            var from = _configuration["Smtp:From"];
            var authCode = _configuration["Smtp:AuthCode"];
            var to = _configuration["Smtp:To"];

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(from) ||
                string.IsNullOrEmpty(authCode) || string.IsNullOrEmpty(to))
            {
                return (false, "邮件服务未配置（appsettings.json 的 Smtp 节缺少 Host/From/AuthCode/To）");
            }

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(from, authCode),
            };

            var message = new MailMessage(from, to, subject, body)
            {
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
                IsBodyHtml = false,
            };

            ct.ThrowIfCancellationRequested();
            await client.SendMailAsync(message, ct);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
