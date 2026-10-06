using IncidentDesk.Api.Auth;
using IncidentDesk.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Persistence;

public sealed class IncidentDbContext(DbContextOptions<IncidentDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentComment> Comments => Set<IncidentComment>();
    public DbSet<StatusHistory> StatusHistory => Set<StatusHistory>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AppUser>().Property(user => user.DisplayName).HasMaxLength(120).IsRequired();

        var incident = builder.Entity<Incident>();
        incident.ToTable("incidents", table =>
        {
            table.HasCheckConstraint("ck_incidents_title", "length(btrim(\"Title\")) > 0");
            table.HasCheckConstraint("ck_incidents_description", "length(btrim(\"Description\")) > 0");
            table.HasCheckConstraint("ck_incidents_severity", "\"Severity\" IN ('Low', 'Medium', 'High', 'Critical')");
            table.HasCheckConstraint("ck_incidents_status", "\"Status\" IN ('Open', 'Investigating', 'Resolved')");
            table.HasCheckConstraint("ck_incidents_resolution",
                "(\"Status\" = 'Resolved' AND \"ResolutionNote\" IS NOT NULL AND length(btrim(\"ResolutionNote\")) > 0 AND \"ResolvedAt\" IS NOT NULL) OR " +
                "(\"Status\" <> 'Resolved' AND \"ResolutionNote\" IS NULL AND \"ResolvedAt\" IS NULL)");
            table.HasCheckConstraint("ck_incidents_investigation_assignee",
                "\"Status\" = 'Open' OR \"AssigneeId\" IS NOT NULL");
        });
        incident.HasKey(value => value.Id);
        incident.Property(value => value.Id).ValueGeneratedNever();
        incident.Property(value => value.Title).HasMaxLength(200).IsRequired();
        incident.Property(value => value.Description).HasMaxLength(10000).IsRequired();
        incident.Property(value => value.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
        incident.Property(value => value.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        incident.Property(value => value.ResolutionNote).HasMaxLength(5000);
        incident.Property(value => value.Summary).HasMaxLength(10000);
        incident.Property(value => value.Version).IsConcurrencyToken().ValueGeneratedNever();
        incident.HasOne<AppUser>().WithMany().HasForeignKey(value => value.ReporterId).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<AppUser>().WithMany().HasForeignKey(value => value.AssigneeId).OnDelete(DeleteBehavior.Restrict);
        incident.HasOne<AppUser>().WithMany().HasForeignKey(value => value.SummaryReviewedById).OnDelete(DeleteBehavior.Restrict);
        incident.HasIndex(value => new { value.CreatedAt, value.Id }).IsDescending(true, true);
        incident.HasIndex(value => new { value.ReporterId, value.CreatedAt });
        incident.HasIndex(value => new { value.Status, value.CreatedAt });
        incident.HasIndex(value => new { value.AssigneeId, value.CreatedAt });
        incident.HasIndex(value => new { value.Severity, value.CreatedAt });

        var comment = builder.Entity<IncidentComment>();
        comment.ToTable("comments", table =>
            table.HasCheckConstraint("ck_comments_body", "length(btrim(\"Body\")) > 0"));
        comment.HasKey(value => value.Id);
        comment.Property(value => value.Id).ValueGeneratedNever();
        comment.Property(value => value.Body).HasMaxLength(5000).IsRequired();
        comment.HasOne<Incident>().WithMany().HasForeignKey(value => value.IncidentId).OnDelete(DeleteBehavior.Restrict);
        comment.HasOne<AppUser>().WithMany().HasForeignKey(value => value.AuthorId).OnDelete(DeleteBehavior.Restrict);
        comment.HasIndex(value => new { value.IncidentId, value.CreatedAt, value.Id });

        var history = builder.Entity<StatusHistory>();
        history.ToTable("status_history", table =>
        {
            table.HasCheckConstraint("ck_status_history_from",
                "\"FromStatus\" IS NULL OR \"FromStatus\" IN ('Open', 'Investigating', 'Resolved')");
            table.HasCheckConstraint("ck_status_history_to", "\"ToStatus\" IN ('Open', 'Investigating', 'Resolved')");
        });
        history.HasKey(value => value.Id);
        history.Property(value => value.Id).ValueGeneratedNever();
        history.Property(value => value.FromStatus).HasConversion<string>().HasMaxLength(16);
        history.Property(value => value.ToStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        history.Property(value => value.Note).HasMaxLength(5000);
        history.HasOne<Incident>().WithMany().HasForeignKey(value => value.IncidentId).OnDelete(DeleteBehavior.Restrict);
        history.HasOne<AppUser>().WithMany().HasForeignKey(value => value.ActorId).OnDelete(DeleteBehavior.Restrict);
        history.HasIndex(value => new { value.IncidentId, value.CreatedAt, value.Id });
    }
}
