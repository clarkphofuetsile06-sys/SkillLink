using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var b = WebApplication.CreateBuilder(args);
b.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
b.Services.AddDbContext<Db>(o => o.UseSqlServer(b.Configuration.GetConnectionString("Default")));
b.Services.AddDataProtection();
b.Services.AddSingleton<Mailer>();
b.Services.AddSingleton<Tokens>();
b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/Account/Login"; o.AccessDeniedPath = "/Account/Login"; });
// Rate limiting for sign-in, register and password-reset requests: 20 per minute per address.
b.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = b.Build();
using (var s = app.Services.CreateScope()) Seed.Run(s.ServiceProvider.GetRequiredService<Db>());
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapDefaultControllerRoute();
app.Run();
