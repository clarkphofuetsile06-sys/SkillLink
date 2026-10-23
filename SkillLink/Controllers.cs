using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

public abstract class Base : Controller
{
    protected readonly Db db;
    protected readonly Mailer mail;
    readonly List<(int UserId, string Message)> queued = new();
    protected Base(Db d, Mailer m) { db = d; mail = m; }
    protected int Uid => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Adds an in-app notification and queues the same message as an email (sent by SaveAsync).</summary>
    protected void Notify(int userId, string msg) { db.Notifications.Add(new Notification { UserId = userId, Message = msg }); queued.Add((userId, msg)); }

    /// <summary>Saves changes, then sends any queued notification emails.</summary>
    protected async Task SaveAsync()
    {
        await db.SaveChangesAsync();
        if (queued.Count == 0) return;
        var ids = queued.Select(q => q.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.UserId)).ToListAsync();
        foreach (var q in queued)
        {
            var u = users.FirstOrDefault(x => x.UserId == q.UserId);
            if (u != null) await mail.SendAsync(u.Email, "SkillLink: " + q.Message, $"Hello {u.FullName},\n\n{q.Message}\n\nLog in to see the details:\n{Request.Scheme}://{Request.Host}/\n\nSkillLink");
        }
        queued.Clear();
    }

    protected void Flash(string msg, bool error = false) => TempData[error ? "FlashError" : "Flash"] = msg;
}

public class AccountController : Base
{
    readonly Tokens tokens;
    public AccountController(Db d, Mailer m, Tokens t) : base(d, m) { tokens = t; }
    static readonly PasswordHasher<AppUser> H = new();

    public IActionResult Register() => View();

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(string fullName, string email, string password, string? phone, string role)
    {
        email = (email ?? "").Trim().ToLower();
        if (string.IsNullOrWhiteSpace(fullName) || !email.Contains('@') || (password ?? "").Length < 8 || (role != "Customer" && role != "Provider"))
        { ViewBag.Error = "Enter your name, a valid email, a password of at least 8 characters and a role."; return View(); }
        if (await db.Users.AnyAsync(u => u.Email == email)) { ViewBag.Error = "That email is already registered."; return View(); }
        var u = new AppUser { FullName = fullName.Trim(), Email = email, PhoneNumber = phone, Role = role };
        u.PasswordHash = H.HashPassword(u, password!);
        db.Users.Add(u);
        if (role == "Provider") db.Providers.Add(new Provider { User = u, DistrictId = (await db.Districts.FirstAsync()).DistrictId });
        await db.SaveChangesAsync();
        await SendVerification(u);
        await Enter(u);
        Flash("Welcome to SkillLink. We sent you an email to confirm your address.");
        return RedirectToAction("Index", role == "Provider" ? "Provider" : "Home");
    }

    public IActionResult Login() => View();

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(string email, string password)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == (email ?? "").Trim().ToLower());
        if (u == null || H.VerifyHashedPassword(u, u.PasswordHash, password ?? "") == PasswordVerificationResult.Failed)
        { ViewBag.Error = "Wrong email or password."; return View(); }
        await Enter(u);
        return RedirectToAction("Index", u.Role == "Provider" ? "Provider" : u.Role == "Admin" ? "Admin" : "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(); return RedirectToAction("Index", "Home"); }

    // ----- email confirmation -----
    async Task SendVerification(AppUser u)
    {
        var link = Url.Action("Verify", "Account", new { token = tokens.Create(u, "verify", TimeSpan.FromDays(3)) }, Request.Scheme)!;
        await mail.SendAsync(u.Email, "Confirm your SkillLink email", $"Hello {u.FullName},\n\nConfirm your email address by opening this link:\n{link}\n\nThe link works for 3 days.\n\nSkillLink");
    }

    public async Task<IActionResult> Verify(string? token)
    {
        if (tokens.TryRead(token, "verify", out var id, out var stamp))
        {
            var u = await db.Users.FindAsync(id);
            if (u != null && Tokens.Stamp(u) == stamp) { u.EmailConfirmed = true; await db.SaveChangesAsync(); Flash("Your email address is confirmed. Thank you."); return RedirectToAction("Index", "Home"); }
        }
        Flash("That confirmation link is invalid or has already been used.", true);
        return RedirectToAction("Index", "Home");
    }

    [Authorize, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendVerification()
    {
        var u = await db.Users.FindAsync(Uid);
        if (u == null) return NotFound();
        if (u.EmailConfirmed) Flash("Your email address is already confirmed.");
        else { await SendVerification(u); Flash("We sent a new confirmation email."); }
        return RedirectToAction("Profile");
    }

    // ----- forgot / reset password -----
    public IActionResult ForgotPassword() => View();

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ForgotPassword(string email)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Email == (email ?? "").Trim().ToLower());
        if (u != null)
        {
            var link = Url.Action("ResetPassword", "Account", new { token = tokens.Create(u, "reset", TimeSpan.FromHours(1)) }, Request.Scheme)!;
            await mail.SendAsync(u.Email, "Reset your SkillLink password", $"Hello {u.FullName},\n\nSomeone asked to reset your SkillLink password. Open this link to choose a new one:\n{link}\n\nThe link works for 1 hour and only once. If you did not ask for this, ignore this email.\n\nSkillLink");
        }
        ViewBag.Sent = true;   // same answer whether or not the email exists, so nobody can probe for accounts
        return View();
    }

    public IActionResult ResetPassword(string? token)
    {
        ViewBag.Token = token;
        ViewBag.Invalid = !tokens.TryRead(token, "reset", out _, out _);
        return View();
    }

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(string token, string password)
    {
        ViewBag.Token = token;
        if (!tokens.TryRead(token, "reset", out var id, out var stamp)) { ViewBag.Invalid = true; return View(); }
        var u = await db.Users.FindAsync(id);
        if (u == null || Tokens.Stamp(u) != stamp) { ViewBag.Invalid = true; return View(); }
        if ((password ?? "").Length < 8) { ViewBag.Error = "Choose a password of at least 8 characters."; return View(); }
        u.PasswordHash = H.HashPassword(u, password!);
        await db.SaveChangesAsync();
        Flash("Your password was changed. You can log in now.");
        return RedirectToAction("Login");
    }

    // ----- my account (all roles) -----
    [Authorize]
    public async Task<IActionResult> Profile()
    {
        var u = await db.Users.FindAsync(Uid);
        return u == null ? NotFound() : View(u);
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Profile(string fullName, string? phone)
    {
        var u = await db.Users.FindAsync(Uid);
        if (u == null) return NotFound();
        fullName = (fullName ?? "").Trim();
        if (fullName.Length == 0 || fullName.Length > 100) { Flash("Enter your name (up to 100 characters).", true); return RedirectToAction("Profile"); }
        u.FullName = fullName; u.PhoneNumber = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        await db.SaveChangesAsync();
        await Enter(u);   // refresh the name stored in the login cookie
        Flash("Your details were saved.");
        return RedirectToAction("Profile");
    }

    [Authorize, HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(string current, string password)
    {
        var u = await db.Users.FindAsync(Uid);
        if (u == null) return NotFound();
        if (H.VerifyHashedPassword(u, u.PasswordHash, current ?? "") == PasswordVerificationResult.Failed) { Flash("Your current password is not correct.", true); return RedirectToAction("Profile"); }
        if ((password ?? "").Length < 8) { Flash("The new password must have at least 8 characters.", true); return RedirectToAction("Profile"); }
        u.PasswordHash = H.HashPassword(u, password!);
        await db.SaveChangesAsync();
        Flash("Your password was changed.");
        return RedirectToAction("Profile");
    }

    Task Enter(AppUser u) => HttpContext.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(new[] {
        new Claim(ClaimTypes.NameIdentifier, u.UserId.ToString()), new Claim(ClaimTypes.Name, u.FullName), new Claim(ClaimTypes.Role, u.Role) },
        CookieAuthenticationDefaults.AuthenticationScheme)));
}

public class HomeController : Base
{
    public HomeController(Db d, Mailer m) : base(d, m) { }
    public async Task<IActionResult> Index(int? categoryId, int? districtId, int? minRating)
    {
        var sw = Stopwatch.StartNew();
        var q = db.Providers.Include(p => p.User).Include(p => p.District).Include(p => p.Skills).ThenInclude(s => s.Category).Include(p => p.Photos).AsSplitQuery().Where(p => p.IsApproved);
        if (categoryId != null) q = q.Where(p => p.Skills.Any(s => s.CategoryId == categoryId));
        if (districtId != null) q = q.Where(p => p.DistrictId == districtId);
        if (minRating != null) q = q.Where(p => p.AverageRating >= minRating);
        ViewBag.Cats = await db.Categories.OrderBy(c => c.Name).ToListAsync(); ViewBag.Dists = await db.Districts.OrderBy(d => d.Name).ToListAsync();
        ViewBag.Cat = categoryId; ViewBag.Dist = districtId; ViewBag.Min = minRating;
        var list = await q.OrderByDescending(p => p.AverageRating).ToListAsync();
        sw.Stop(); ViewBag.Ms = sw.ElapsedMilliseconds;   // shown under the results; used as evidence for the 3-second search requirement (NFR-4)
        return View(list);
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await db.Providers.Include(x => x.User).Include(x => x.District).Include(x => x.Skills).ThenInclude(s => s.Category).Include(x => x.Photos).AsSplitQuery()
            .FirstOrDefaultAsync(x => x.ProviderId == id && x.IsApproved);
        if (p == null) return NotFound();
        ViewBag.Reviews = await db.Reviews.Where(r => r.ProviderRequest!.ProviderId == id).OrderByDescending(r => r.CreatedAt).ToListAsync();
        return View(p);
    }
}

[Authorize(Roles = "Customer")]
public class CustomerController : Base
{
    public CustomerController(Db d, Mailer m) : base(d, m) { }
    const int MaxProviders = 5;

    public async Task<IActionResult> Requests() => View(await db.ProviderRequests
        .Include(r => r.Provider!).ThenInclude(p => p.User).Include(r => r.ServiceRequest!).ThenInclude(s => s.Category).Include(r => r.Review)
        .Where(r => r.ServiceRequest!.CustomerId == Uid).OrderByDescending(r => r.SentAt).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Send(int providerId, int categoryId, string jobDetails, DateTime preferredDate)
    {
        var p = await db.Providers.Include(x => x.Skills).FirstOrDefaultAsync(x => x.ProviderId == providerId && x.IsApproved);
        if (p == null || string.IsNullOrWhiteSpace(jobDetails) || !p.Skills.Any(s => s.CategoryId == categoryId)) return BadRequest();
        if (preferredDate.Date < DateTime.Today) { Flash("Choose a date that is today or later.", true); return RedirectToAction("Details", "Home", new { id = providerId }); }
        db.ProviderRequests.Add(new ProviderRequest { Provider = p, ServiceRequest = new ServiceRequest { CustomerId = Uid, CategoryId = categoryId, JobDetails = jobDetails.Trim(), PreferredDate = preferredDate } });
        Notify(p.UserId, "You have a new service request.");
        await SaveAsync();
        Flash("Your request was sent.");
        return RedirectToAction("Requests");
    }

    // ----- one job sent to several providers (FR-9) -----
    public async Task<IActionResult> Compose(int[] ids, int? categoryId)
    {
        ids = (ids ?? Array.Empty<int>()).Distinct().Take(MaxProviders).ToArray();
        var ps = ids.Length == 0 ? new List<Provider>() : await db.Providers.Include(p => p.User).Include(p => p.District).Include(p => p.Skills).ThenInclude(s => s.Category)
            .Where(p => ids.Contains(p.ProviderId) && p.IsApproved).ToListAsync();
        if (ps.Count == 0) { Flash("Select at least one provider first.", true); return RedirectToAction("Index", "Home"); }
        ViewBag.Cat = categoryId;
        return View(ps);
    }

    [HttpPost]
    public async Task<IActionResult> SendMany(int[] providerIds, int categoryId, string jobDetails, DateTime preferredDate)
    {
        providerIds = (providerIds ?? Array.Empty<int>()).Distinct().Take(MaxProviders).ToArray();
        if (providerIds.Length == 0) return RedirectToAction("Index", "Home");
        if (string.IsNullOrWhiteSpace(jobDetails) || preferredDate.Date < DateTime.Today)
        { Flash("Describe the job and choose a date that is today or later.", true); return RedirectToAction("Compose", new { ids = providerIds, categoryId }); }
        var ps = await db.Providers.Include(p => p.Skills).Where(p => providerIds.Contains(p.ProviderId) && p.IsApproved && p.Skills.Any(s => s.CategoryId == categoryId)).ToListAsync();
        if (ps.Count == 0) { Flash("None of the selected providers offer that service.", true); return RedirectToAction("Compose", new { ids = providerIds }); }
        var job = new ServiceRequest { CustomerId = Uid, CategoryId = categoryId, JobDetails = jobDetails.Trim(), PreferredDate = preferredDate };
        foreach (var p in ps) { db.ProviderRequests.Add(new ProviderRequest { Provider = p, ServiceRequest = job }); Notify(p.UserId, "You have a new service request."); }
        await SaveAsync();
        var skipped = providerIds.Length - ps.Count;
        Flash((ps.Count == 1 ? "Your request was sent to 1 provider." : $"Your request was sent to {ps.Count} providers.") + (skipped > 0 ? $" {skipped} selected provider(s) do not offer that service and were skipped." : ""));
        return RedirectToAction("Requests");
    }

    [HttpPost]
    public async Task<IActionResult> Rate(int id, int rating, string? comment)
    {
        var r = await db.ProviderRequests.Include(x => x.Provider).FirstOrDefaultAsync(x => x.ProviderRequestId == id && x.ServiceRequest!.CustomerId == Uid);
        if (r == null || r.Status != "Completed" || rating < 1 || rating > 5 || await db.Reviews.AnyAsync(x => x.ProviderRequestId == id)) return BadRequest();
        db.Reviews.Add(new Review { ProviderRequestId = id, Rating = rating, Comment = comment });
        await db.SaveChangesAsync();
        r.Provider!.AverageRating = Math.Round((decimal)await db.Reviews.Where(x => x.ProviderRequest!.ProviderId == r.ProviderId).AverageAsync(x => (double)x.Rating), 2);
        Notify(r.Provider.UserId, "You received a new review.");
        await SaveAsync();
        Flash("Thank you. Your review was saved.");
        return RedirectToAction("Requests");
    }
}

[Authorize(Roles = "Provider")]
public class ProviderController : Base
{
    readonly IWebHostEnvironment env;
    public ProviderController(Db d, Mailer m, IWebHostEnvironment e) : base(d, m) { env = e; }
    const long MaxPhotoBytes = 2 * 1024 * 1024;   // NFR-7: work photos are limited to 2 MB each
    const int MaxPhotos = 12;

    async Task<Provider> Me() => await db.Providers.Include(p => p.Skills).ThenInclude(s => s.Category).Include(p => p.Photos).AsSplitQuery().FirstAsync(p => p.UserId == Uid);
    string PhotoDir => Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "uploads", "work");

    public async Task<IActionResult> Index()
    {
        var me = await Me();
        ViewBag.Cats = await db.Categories.OrderBy(c => c.Name).ToListAsync(); ViewBag.Dists = await db.Districts.OrderBy(d => d.Name).ToListAsync();
        ViewBag.Reqs = await db.ProviderRequests.Include(r => r.ServiceRequest!).ThenInclude(s => s.Category)
            .Include(r => r.ServiceRequest!).ThenInclude(s => s.Customer).Where(r => r.ProviderId == me.ProviderId).OrderByDescending(r => r.SentAt).ToListAsync();
        return View(me);
    }

    [HttpPost]
    public async Task<IActionResult> Profile(string? bio, int districtId, string availabilityStatus)
    {
        var me = await Me();
        if (!await db.Districts.AnyAsync(d => d.DistrictId == districtId)) return BadRequest();
        me.Bio = bio; me.DistrictId = districtId; me.AvailabilityStatus = availabilityStatus == "Busy" ? "Busy" : "Available";
        await db.SaveChangesAsync(); Flash("Your profile was saved."); return RedirectToAction("Index");
    }

    // ----- skills: add, edit years, remove (FR-3) -----
    [HttpPost]
    public async Task<IActionResult> AddSkill(int categoryId, int years)
    {
        var me = await Me();
        if (!await db.Categories.AnyAsync(c => c.CategoryId == categoryId)) return BadRequest();
        if (!me.Skills.Any(s => s.CategoryId == categoryId)) { me.Skills.Add(new ProviderSkill { CategoryId = categoryId, YearsExperience = Math.Clamp(years, 0, 60) }); await db.SaveChangesAsync(); Flash("Skill added."); }
        else Flash("You already listed that skill. Edit its years below.", true);
        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> UpdateSkill(int id, int years)
    {
        var me = await Me();
        var s = me.Skills.FirstOrDefault(x => x.SkillId == id);
        if (s == null) return NotFound();
        s.YearsExperience = Math.Clamp(years, 0, 60); await db.SaveChangesAsync(); Flash("Skill updated.");
        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> RemoveSkill(int id)
    {
        var me = await Me();
        var s = me.Skills.FirstOrDefault(x => x.SkillId == id);
        if (s == null) return NotFound();
        db.Skills.Remove(s); await db.SaveChangesAsync(); Flash("Skill removed.");
        return RedirectToAction("Index");
    }

    // ----- work photos with captions (FR-4, NFR-7) -----
    [HttpPost, RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> UploadPhoto(IFormFile? photo, string? caption)
    {
        var me = await Me();
        if (photo == null || photo.Length == 0) { Flash("Choose a photo to upload.", true); return RedirectToAction("Index"); }
        if (photo.Length > MaxPhotoBytes) { Flash("That photo is larger than 2 MB. Choose a smaller one.", true); return RedirectToAction("Index"); }
        if (me.Photos.Count >= MaxPhotos) { Flash($"You can keep up to {MaxPhotos} photos. Delete one first.", true); return RedirectToAction("Index"); }
        var ext = Path.GetExtension(photo.FileName).ToLowerInvariant();
        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp") { Flash("Only JPG, PNG or WEBP photos are allowed.", true); return RedirectToAction("Index"); }

        // Check the real file signature, not just the extension.
        var head = new byte[12];
        await using (var rs = photo.OpenReadStream()) { await rs.ReadAtLeastAsync(head.AsMemory(), 12, false); }
        bool jpg = head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
        bool png = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
        bool webp = head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50;
        if (!jpg && !png && !webp) { Flash("That file is not a valid JPG, PNG or WEBP image.", true); return RedirectToAction("Index"); }

        Directory.CreateDirectory(PhotoDir);
        var name = Guid.NewGuid().ToString("N") + (ext == ".jpeg" ? ".jpg" : ext);
        await using (var fs = System.IO.File.Create(Path.Combine(PhotoDir, name))) await photo.CopyToAsync(fs);
        var cap = (caption ?? "").Trim(); if (cap.Length > 120) cap = cap.Substring(0, 120);
        db.WorkPhotos.Add(new WorkPhoto { ProviderId = me.ProviderId, FileName = name, Caption = cap.Length == 0 ? null : cap });
        await db.SaveChangesAsync();
        Flash("Photo added to your work gallery.");
        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> DeletePhoto(int id)
    {
        var me = await Me();
        var ph = me.Photos.FirstOrDefault(x => x.PhotoId == id);
        if (ph == null) return NotFound();
        try { var path = Path.Combine(PhotoDir, ph.FileName); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* the database row is removed either way */ }
        db.WorkPhotos.Remove(ph); await db.SaveChangesAsync(); Flash("Photo deleted.");
        return RedirectToAction("Index");
    }

    [HttpPost] public Task<IActionResult> Respond(int id, bool accept) => Move(id, "Pending", accept ? "Accepted" : "Declined");
    [HttpPost] public Task<IActionResult> Complete(int id) => Move(id, "Accepted", "Completed");

    async Task<IActionResult> Move(int id, string from, string to)
    {
        var me = await Me();
        var r = await db.ProviderRequests.Include(x => x.ServiceRequest).FirstOrDefaultAsync(x => x.ProviderRequestId == id && x.ProviderId == me.ProviderId && x.Status == from);
        if (r == null) return BadRequest();
        r.Status = to;
        if (to == "Completed") r.CompletedAt = DateTime.UtcNow; else r.RespondedAt = DateTime.UtcNow;
        Notify(r.ServiceRequest!.CustomerId, $"Your request was {to.ToLower()}.");
        await SaveAsync(); return RedirectToAction("Index");
    }
}

[Authorize(Roles = "Admin")]
public class AdminController : Base
{
    public AdminController(Db d, Mailer m) : base(d, m) { }
    public async Task<IActionResult> Index() => View(await db.Providers.Include(p => p.User).Include(p => p.District)
        .Include(p => p.Skills).ThenInclude(s => s.Category).OrderBy(p => p.IsApproved).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Approve(int id)
    {
        var p = await db.Providers.FindAsync(id); if (p == null) return NotFound();
        p.IsApproved = true; Notify(p.UserId, "Your profile was approved and now appears in search.");
        await SaveAsync(); Flash("Provider approved."); return RedirectToAction("Index");
    }

    // ----- manage categories and districts (FR-16, NFR-6) -----
    public async Task<IActionResult> Lists()
    {
        ViewBag.Cats = await db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.Dists = await db.Districts.OrderBy(d => d.Name).ToListAsync();
        ViewBag.CatUse = await db.Skills.GroupBy(s => s.CategoryId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
        ViewBag.DistUse = await db.Providers.GroupBy(p => p.DistrictId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
        return View();
    }

    static string Clean(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s.Substring(0, max) : s; }
    static bool Coord(string? s, decimal min, decimal max, out decimal v) => decimal.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    [HttpPost]
    public async Task<IActionResult> AddCategory(string name, string? description)
    {
        name = Clean(name, 60);
        if (name.Length == 0) Flash("Enter a category name.", true);
        else if (await db.Categories.AnyAsync(c => c.Name.ToLower() == name.ToLower())) Flash("That category already exists.", true);
        else { var desc = Clean(description, 200); db.Categories.Add(new ServiceCategory { Name = name, Description = desc.Length > 0 ? desc : null }); await db.SaveChangesAsync(); Flash("Category added."); }
        return RedirectToAction("Lists");
    }

    [HttpPost]
    public async Task<IActionResult> UpdateCategory(int id, string name, string? description)
    {
        var c = await db.Categories.FindAsync(id); if (c == null) return NotFound();
        name = Clean(name, 60);
        if (name.Length == 0) Flash("Enter a category name.", true);
        else if (await db.Categories.AnyAsync(x => x.CategoryId != id && x.Name.ToLower() == name.ToLower())) Flash("Another category already has that name.", true);
        else { var desc = Clean(description, 200); c.Name = name; c.Description = desc.Length > 0 ? desc : null; await db.SaveChangesAsync(); Flash("Category saved."); }
        return RedirectToAction("Lists");
    }

    [HttpPost]
    public async Task<IActionResult> DeleteCategory(int id)
    {
        var c = await db.Categories.FindAsync(id); if (c == null) return NotFound();
        if (await db.Skills.AnyAsync(s => s.CategoryId == id) || await db.ServiceRequests.AnyAsync(r => r.CategoryId == id)) Flash($"\"{c.Name}\" is in use by providers or requests, so it cannot be deleted. Rename it instead.", true);
        else { db.Categories.Remove(c); await db.SaveChangesAsync(); Flash("Category deleted."); }
        return RedirectToAction("Lists");
    }

    [HttpPost]
    public async Task<IActionResult> AddDistrict(string name, string lat, string lng)
    {
        name = Clean(name, 60);
        if (name.Length == 0) Flash("Enter a district name.", true);
        else if (!Coord(lat, -90, 90, out var la) || !Coord(lng, -180, 180, out var lo)) Flash("Enter valid latitude and longitude numbers, for example -24.65 and 25.91.", true);
        else if (await db.Districts.AnyAsync(d => d.Name.ToLower() == name.ToLower())) Flash("That district already exists.", true);
        else { db.Districts.Add(new District { Name = name, Latitude = la, Longitude = lo }); await db.SaveChangesAsync(); Flash("District added."); }
        return RedirectToAction("Lists");
    }

    [HttpPost]
    public async Task<IActionResult> UpdateDistrict(int id, string name, string lat, string lng)
    {
        var d = await db.Districts.FindAsync(id); if (d == null) return NotFound();
        name = Clean(name, 60);
        if (name.Length == 0) Flash("Enter a district name.", true);
        else if (!Coord(lat, -90, 90, out var la) || !Coord(lng, -180, 180, out var lo)) Flash("Enter valid latitude and longitude numbers.", true);
        else if (await db.Districts.AnyAsync(x => x.DistrictId != id && x.Name.ToLower() == name.ToLower())) Flash("Another district already has that name.", true);
        else { d.Name = name; d.Latitude = la; d.Longitude = lo; await db.SaveChangesAsync(); Flash("District saved."); }
        return RedirectToAction("Lists");
    }

    [HttpPost]
    public async Task<IActionResult> DeleteDistrict(int id)
    {
        var d = await db.Districts.FindAsync(id); if (d == null) return NotFound();
        if (await db.Providers.AnyAsync(p => p.DistrictId == id)) Flash($"Providers are registered in {d.Name}, so it cannot be deleted. Rename it instead.", true);
        else { db.Districts.Remove(d); await db.SaveChangesAsync(); Flash("District deleted."); }
        return RedirectToAction("Lists");
    }

    // ----- development outbox: emails written to disk when no SMTP server is configured -----
    public IActionResult Outbox()
    {
        var items = new List<OutboxItem>();
        if (!mail.UsesSmtp && Directory.Exists(mail.OutboxPath))
        {
            foreach (var f in new DirectoryInfo(mail.OutboxPath).GetFiles("*.txt").OrderByDescending(fi => fi.Name).Take(40))
            {
                var text = System.IO.File.ReadAllText(f.FullName).Replace("\r\n", "\n");
                var split = text.Split("\n\n", 2);
                var it = new OutboxItem();
                foreach (var line in split[0].Split('\n'))
                {
                    if (line.StartsWith("To: ")) it.To = line.Substring(4);
                    else if (line.StartsWith("Subject: ")) it.Subject = line.Substring(9);
                    else if (line.StartsWith("Date: ")) it.Date = line.Substring(6);
                }
                var enc = System.Net.WebUtility.HtmlEncode(split.Length > 1 ? split[1] : "");
                it.Html = Regex.Replace(enc, @"https?://[^\s<]+", m => $"<a href=\"{m.Value}\">{m.Value}</a>").Replace("\n", "<br>");
                items.Add(it);
            }
        }
        ViewBag.Smtp = mail.UsesSmtp;
        return View(items);
    }
}

[Authorize]
public class NotificationsController : Base
{
    public NotificationsController(Db d, Mailer m) : base(d, m) { }
    public async Task<IActionResult> Index()
    {
        var list = await db.Notifications.Where(n => n.UserId == Uid).OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync();
        foreach (var n in list) n.IsRead = true;
        await db.SaveChangesAsync();
        return View(list);
    }
}
