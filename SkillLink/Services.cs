using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

/// <summary>
/// Sends email. If Email:Host is set in appsettings.json the message goes out over SMTP.
/// If it is empty (the default) each message is written as a text file in App_Data/outbox,
/// which the administrator can read on the "Outbox" page. Sending never throws.
/// </summary>
public class Mailer
{
    readonly IConfiguration cfg; readonly ILogger<Mailer> log;
    public string OutboxPath { get; }
    public Mailer(IConfiguration c, IWebHostEnvironment env, ILogger<Mailer> l) { cfg = c; log = l; OutboxPath = Path.Combine(env.ContentRootPath, "App_Data", "outbox"); }
    public bool UsesSmtp => !string.IsNullOrWhiteSpace(cfg["Email:Host"]);

    public async Task SendAsync(string to, string subject, string body)
    {
        try
        {
            if (UsesSmtp)
            {
                var from = cfg["Email:From"] ?? cfg["Email:User"] ?? "no-reply@skilllink.bw";
                var port = int.TryParse(cfg["Email:Port"], out var p) ? p : 587;
                using var client = new System.Net.Mail.SmtpClient(cfg["Email:Host"], port) { EnableSsl = !string.Equals(cfg["Email:UseSsl"], "false", StringComparison.OrdinalIgnoreCase) };
                if (!string.IsNullOrWhiteSpace(cfg["Email:User"])) client.Credentials = new NetworkCredential(cfg["Email:User"], cfg["Email:Password"]);
                await client.SendMailAsync(new System.Net.Mail.MailMessage(from, to, subject, body));
            }
            else
            {
                Directory.CreateDirectory(OutboxPath);
                var file = Path.Combine(OutboxPath, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(file, $"To: {to}\nSubject: {subject}\nDate: {DateTime.UtcNow:u}\n\n{body}");
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not send email to {To}", to); }
    }
}

public class OutboxItem { public string To { get; set; } = ""; public string Subject { get; set; } = ""; public string Date { get; set; } = ""; public string Html { get; set; } = ""; }

/// <summary>Creates and reads time-limited tokens for email confirmation and password reset.</summary>
public class Tokens
{
    readonly IDataProtector p;
    public Tokens(IDataProtectionProvider dp) { p = dp.CreateProtector("SkillLink.Tokens.v1"); }

    /// <summary>A fingerprint of the account state, so a token stops working once it has been used.</summary>
    public static string Stamp(AppUser u) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(u.PasswordHash + "|" + u.Email + "|" + u.EmailConfirmed))).Substring(0, 16);

    public string Create(AppUser u, string purpose, TimeSpan life) => p.Protect($"{u.UserId}|{purpose}|{DateTime.UtcNow.Add(life).Ticks}|{Stamp(u)}");

    public bool TryRead(string? token, string purpose, out int userId, out string stamp)
    {
        userId = 0; stamp = "";
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var parts = p.Unprotect(token).Split('|');
            if (parts.Length != 4 || parts[1] != purpose || !long.TryParse(parts[2], out var ticks) || ticks < DateTime.UtcNow.Ticks || !int.TryParse(parts[0], out userId)) return false;
            stamp = parts[3]; return true;
        }
        catch { return false; }
    }
}
