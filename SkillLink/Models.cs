using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

public class AppUser { public int UserId { get; set; } public string FullName { get; set; } = ""; public string Email { get; set; } = ""; public string PasswordHash { get; set; } = ""; public string? PhoneNumber { get; set; } public string Role { get; set; } = "Customer"; public bool EmailConfirmed { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
public class District { public int DistrictId { get; set; } public string Name { get; set; } = ""; public decimal Latitude { get; set; } public decimal Longitude { get; set; } }
public class ServiceCategory { public int CategoryId { get; set; } public string Name { get; set; } = ""; public string? Description { get; set; } }
public class Provider { public int ProviderId { get; set; } public int UserId { get; set; } public AppUser? User { get; set; } public int DistrictId { get; set; } public District? District { get; set; } public string? Bio { get; set; } public bool IsApproved { get; set; } public string AvailabilityStatus { get; set; } = "Available"; public decimal AverageRating { get; set; } public List<ProviderSkill> Skills { get; set; } = new(); public List<WorkPhoto> Photos { get; set; } = new(); }
public class ProviderSkill { public int SkillId { get; set; } public int ProviderId { get; set; } public int CategoryId { get; set; } public ServiceCategory? Category { get; set; } public int YearsExperience { get; set; } }
public class WorkPhoto { public int PhotoId { get; set; } public int ProviderId { get; set; } public Provider? Provider { get; set; } public string FileName { get; set; } = ""; public string? Caption { get; set; } public DateTime UploadedAt { get; set; } = DateTime.UtcNow; }
public class ServiceRequest { public int RequestId { get; set; } public int CustomerId { get; set; } public AppUser? Customer { get; set; } public int CategoryId { get; set; } public ServiceCategory? Category { get; set; } public string JobDetails { get; set; } = ""; public DateTime PreferredDate { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
public class ProviderRequest { public int ProviderRequestId { get; set; } public int RequestId { get; set; } public ServiceRequest? ServiceRequest { get; set; } public int ProviderId { get; set; } public Provider? Provider { get; set; } public string Status { get; set; } = "Pending"; public DateTime SentAt { get; set; } = DateTime.UtcNow; public DateTime? RespondedAt { get; set; } public DateTime? CompletedAt { get; set; } public Review? Review { get; set; } }
public class Review { public int ReviewId { get; set; } public int ProviderRequestId { get; set; } public ProviderRequest? ProviderRequest { get; set; } public int Rating { get; set; } public string? Comment { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }
public class Notification { public int NotificationId { get; set; } public int UserId { get; set; } public string Message { get; set; } = ""; public bool IsRead { get; set; } public DateTime CreatedAt { get; set; } = DateTime.UtcNow; }

public class Db : DbContext
{
    public Db(DbContextOptions<Db> o) : base(o) { }
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<ServiceCategory> Categories => Set<ServiceCategory>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderSkill> Skills => Set<ProviderSkill>();
    public DbSet<WorkPhoto> WorkPhotos => Set<WorkPhoto>();
    public DbSet<ServiceRequest> ServiceRequests => Set<ServiceRequest>();
    public DbSet<ProviderRequest> ProviderRequests => Set<ProviderRequest>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<AppUser>().HasKey(u => u.UserId);
        m.Entity<ServiceCategory>().HasKey(c => c.CategoryId);
        m.Entity<ProviderSkill>().HasKey(s => s.SkillId);
        m.Entity<ServiceRequest>().HasKey(r => r.RequestId);
        m.Entity<WorkPhoto>().HasKey(w => w.PhotoId);
        m.Entity<WorkPhoto>().Property(w => w.FileName).HasMaxLength(80);
        m.Entity<WorkPhoto>().Property(w => w.Caption).HasMaxLength(120);
        m.Entity<WorkPhoto>().HasOne(w => w.Provider).WithMany(p => p.Photos).HasForeignKey(w => w.ProviderId);
        m.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        m.Entity<Provider>().HasIndex(p => p.UserId).IsUnique();
        m.Entity<Review>().HasIndex(r => r.ProviderRequestId).IsUnique();
        m.Entity<ServiceCategory>().Property(c => c.Name).HasMaxLength(60);
        m.Entity<District>().Property(d => d.Name).HasMaxLength(60);
        m.Entity<ServiceCategory>().HasIndex(c => c.Name).IsUnique();
        m.Entity<District>().HasIndex(d => d.Name).IsUnique();
        m.Entity<Provider>().Property(p => p.AverageRating).HasPrecision(3, 2);
        m.Entity<District>().Property(d => d.Latitude).HasPrecision(9, 6);
        m.Entity<District>().Property(d => d.Longitude).HasPrecision(9, 6);
        m.Entity<ProviderRequest>().HasOne(p => p.Provider).WithMany().OnDelete(DeleteBehavior.Restrict);
        m.Entity<ServiceRequest>().HasOne(s => s.Customer).WithMany().HasForeignKey(s => s.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public static class Seed
{
    public static void Run(Db db)
    {
        db.Database.EnsureCreated();
        if (db.Categories.Any()) return;
        db.Categories.AddRange(new[] { "Plumbing", "Electrical work", "Carpentry", "Cleaning", "Hairdressing", "Photography", "Graphic design", "Mechanics", "Tutoring" }.Select(n => new ServiceCategory { Name = n }));
        db.Districts.AddRange(new (string, decimal, decimal)[] { ("Gaborone", -24.65m, 25.91m), ("Francistown", -21.17m, 27.51m), ("Maun", -19.98m, 23.42m), ("Kasane", -17.80m, 25.15m), ("Palapye", -22.55m, 27.13m), ("Serowe", -22.39m, 26.71m), ("Lobatse", -25.22m, 25.68m), ("Selebi-Phikwe", -21.98m, 27.84m), ("Kanye", -24.97m, 25.34m), ("Molepolole", -24.41m, 25.49m) }
            .Select(d => new District { Name = d.Item1, Latitude = d.Item2, Longitude = d.Item3 }));
        var a = new AppUser { FullName = "Administrator", Email = "admin@skilllink.bw", Role = "Admin", EmailConfirmed = true };
        a.PasswordHash = new PasswordHasher<AppUser>().HashPassword(a, "Admin@123");
        db.Users.Add(a);
        db.SaveChanges();
    }
}
