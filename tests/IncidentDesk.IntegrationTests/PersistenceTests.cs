using IncidentDesk.Api.Auth;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentDesk.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class PersistenceTests(ApiFixture fixture)
{
    [Fact]
    public async Task Committed_migrations_and_repeat_seeding_preserve_existing_data()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        var before = new
        {
            Users = await db.Users.CountAsync(TestContext.Current.CancellationToken),
            Roles = await db.Roles.CountAsync(TestContext.Current.CancellationToken),
            Incidents = await db.Incidents.CountAsync(TestContext.Current.CancellationToken),
            Comments = await db.Comments.CountAsync(TestContext.Current.CancellationToken),
            History = await db.StatusHistory.CountAsync(TestContext.Current.CancellationToken)
        };
        await DemoSeeder.SeedAsync(fixture.Factory.Services, TestContext.Current.CancellationToken);
        Assert.Equal(before.Users, await db.Users.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before.Roles, await db.Roles.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before.Incidents, await db.Incidents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before.Comments, await db.Comments.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before.History, await db.StatusHistory.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.Roles.CountAsync(TestContext.Current.CancellationToken));
        Assert.Contains(await db.Roles.Select(role => role.Name).ToListAsync(TestContext.Current.CancellationToken), role => role == Roles.Engineer);
    }

    [Fact]
    public async Task PostgreSQL_rejects_an_orphan_comment()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        db.Comments.Add(IncidentComment.Create(Guid.NewGuid(), DemoSeeder.EngineerOneId,
            "This incident does not exist.", DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task PostgreSQL_enforces_resolution_and_assignment_invariants()
    {
        var incident = await fixture.CreateAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        var unassigned = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE incidents SET \"Status\" = 'Investigating' WHERE \"Id\" = {incident.Value.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unassigned.SqlState);
        var unresolved = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE incidents SET \"Status\" = 'Resolved', \"AssigneeId\" = {DemoSeeder.EngineerOneId} WHERE \"Id\" = {incident.Value.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unresolved.SqlState);
        var persisted = await db.Incidents.AsNoTracking().SingleAsync(value => value.Id == incident.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(IncidentStatus.Open, persisted.Status);
        Assert.Null(persisted.AssigneeId);
    }

    [Fact]
    public async Task PostgreSQL_prevents_deleting_an_incident_with_history()
    {
        var incident = await fixture.CreateAsync();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM incidents WHERE \"Id\" = {incident.Value.Id}", TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        Assert.True(await db.Incidents.AnyAsync(value => value.Id == incident.Value.Id, TestContext.Current.CancellationToken));
    }
}
