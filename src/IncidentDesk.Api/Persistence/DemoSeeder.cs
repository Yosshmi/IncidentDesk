using IncidentDesk.Api.Auth;
using IncidentDesk.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Persistence;

/// <summary>Explicit fictional demo setup; never called by ordinary API startup.</summary>
public static class DemoSeeder
{
    public const string Password = "IncidentDesk1!";
    public static readonly Guid ReporterOneId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ReporterTwoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid EngineerOneId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid EngineerTwoId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var role in new[] { Roles.Reporter, Roles.Engineer })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                EnsureSuccess(await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() }),
                    $"create role {role}");
            }
        }

        await EnsureUserAsync(users, ReporterOneId, "reporter1@example.test", "Alex Morgan", Roles.Reporter);
        await EnsureUserAsync(users, ReporterTwoId, "reporter2@example.test", "Sam Rivera", Roles.Reporter);
        await EnsureUserAsync(users, EngineerOneId, "engineer1@example.test", "Jordan Patel", Roles.Engineer);
        await EnsureUserAsync(users, EngineerTwoId, "engineer2@example.test", "Casey Chen", Roles.Engineer);

        if (!await db.Incidents.AnyAsync(cancellationToken))
        {
            SeedIncidents(db, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureUserAsync(UserManager<AppUser> users, Guid id, string email,
        string displayName, string role)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser
            {
                Id = id,
                UserName = email,
                Email = email,
                DisplayName = displayName,
                EmailConfirmed = true,
                LockoutEnabled = true
            };
            EnsureSuccess(await users.CreateAsync(user, Password), $"create demo account {email}");
        }
        else if (user.Id != id)
        {
            throw new InvalidOperationException($"Demo account {email} already exists with a different ID.");
        }

        if (!await users.IsInRoleAsync(user, role))
        {
            EnsureSuccess(await users.AddToRoleAsync(user, role), $"assign {role} to {email}");
        }
    }

    private static void SeedIncidents(IncidentDbContext db, DateTimeOffset now)
    {
        var open = Incident.Create("Invoice export stalls for a large date range",
            "Fictional demo: exporting more than 90 days of invoices leaves the report at Processing.",
            Severity.Medium, ReporterOneId, now.AddHours(-3));
        db.StatusHistory.Add(StatusHistory.Create(open.Id, ReporterOneId, null, IncidentStatus.Open,
            null, now.AddHours(-3)));

        var investigating = Incident.Create("Intermittent payment confirmation delays",
            "Fictional demo: a subset of successful sandbox payments take several minutes to appear in the dashboard.",
            Severity.High, ReporterTwoId, now.AddHours(-6));
        db.StatusHistory.Add(StatusHistory.Create(investigating.Id, ReporterTwoId, null, IncidentStatus.Open,
            null, now.AddHours(-6)));
        investigating.Assign(EngineerOneId, now.AddHours(-5));
        investigating.Transition(IncidentStatus.Investigating, null, now.AddHours(-5));
        db.StatusHistory.Add(StatusHistory.Create(investigating.Id, EngineerOneId, IncidentStatus.Open,
            IncidentStatus.Investigating, null, now.AddHours(-5)));
        db.Comments.Add(investigating.AddComment(
            "The sandbox queue depth increased during the scheduled import. Correlation is confirmed; root cause is still under investigation.",
            EngineerOneId, now.AddHours(-4)));

        var resolved = Incident.Create("Support search omits recently updated tickets",
            "Fictional demo: searching a ticket title returns old results after an update.",
            Severity.Low, ReporterOneId, now.AddDays(-1));
        db.StatusHistory.Add(StatusHistory.Create(resolved.Id, ReporterOneId, null, IncidentStatus.Open,
            null, now.AddDays(-1)));
        resolved.Assign(EngineerTwoId, now.AddHours(-20));
        resolved.Transition(IncidentStatus.Investigating, null, now.AddHours(-20));
        db.StatusHistory.Add(StatusHistory.Create(resolved.Id, EngineerTwoId, IncidentStatus.Open,
            IncidentStatus.Investigating, null, now.AddHours(-20)));
        db.Comments.Add(resolved.AddComment(
            "Reproduced stale search results in the demo environment. The cache invalidation event was not handled for title edits.",
            EngineerTwoId, now.AddHours(-18)));
        const string resolution = "Added cache invalidation for ticket title edits and verified that updated tickets appear immediately.";
        resolved.Transition(IncidentStatus.Resolved, resolution, now.AddHours(-12));
        db.StatusHistory.Add(StatusHistory.Create(resolved.Id, EngineerTwoId, IncidentStatus.Investigating,
            IncidentStatus.Resolved, resolution, now.AddHours(-12)));
        resolved.SaveSummary(
            "The demo support search served stale cached titles. The engineer added cache invalidation on title changes and verified immediate search visibility.",
            EngineerTwoId, now.AddHours(-11));

        db.Incidents.AddRange(open, investigating, resolved);
    }

    private static void EnsureSuccess(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {string.Join("; ", result.Errors.Select(error => error.Description))}");
        }
    }
}
