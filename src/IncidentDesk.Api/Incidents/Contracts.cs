using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using IncidentDesk.Domain;

namespace IncidentDesk.Api.Incidents;

public sealed record CreateIncidentRequest(
    [Required, StringLength(200)] string Title,
    [Required, StringLength(10000)] string Description,
    Severity Severity = Severity.Medium);

public sealed record UpdateIncidentRequest(
    [Required, StringLength(200)] string Title,
    [Required, StringLength(10000)] string Description,
    [property: JsonRequired] Severity Severity);

public sealed record AssignIncidentRequest([property: JsonRequired] Guid? AssigneeId);
public sealed record TransitionIncidentRequest([Required] IncidentStatus? Status, [StringLength(5000)] string? ResolutionNote);
public sealed record AddCommentRequest([Required, StringLength(5000)] string Body);
public sealed record SaveSummaryRequest([Required, StringLength(10000)] string Text);

public class PageQuery : IValidatableObject
{
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((long)(Page - 1) * PageSize > int.MaxValue)
        {
            yield return new ValidationResult("The requested page is too large.", [nameof(Page)]);
        }
    }
}

public sealed class IncidentQuery : PageQuery
{
    [StringLength(200)]
    public string? Q { get; set; }
    [EnumDataType(typeof(IncidentStatus))]
    public IncidentStatus? Status { get; set; }
    [EnumDataType(typeof(Severity))]
    public Severity? Severity { get; set; }
    public Guid? ReporterId { get; set; }
    public Guid? AssigneeId { get; set; }
    public bool? Unassigned { get; set; }
    public DateTimeOffset? CreatedFrom { get; set; }
    public DateTimeOffset? CreatedTo { get; set; }

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in base.Validate(validationContext))
        {
            yield return result;
        }
        if (Q?.Contains('\0', StringComparison.Ordinal) == true)
        {
            yield return new ValidationResult("Search text cannot contain a null character.", [nameof(Q)]);
        }
        if (AssigneeId.HasValue && Unassigned == true)
        {
            yield return new ValidationResult("Use assigneeId or unassigned=true, not both.", [nameof(AssigneeId), nameof(Unassigned)]);
        }
        if (CreatedFrom > CreatedTo)
        {
            yield return new ValidationResult("createdFrom must not be later than createdTo.", [nameof(CreatedFrom)]);
        }
        if (AssigneeId == Guid.Empty || ReporterId == Guid.Empty)
        {
            yield return new ValidationResult("User IDs must not be empty GUIDs.", [nameof(AssigneeId), nameof(ReporterId)]);
        }
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record IncidentResponse(
    Guid Id, string Title, string Description, Severity Severity, IncidentStatus Status,
    Guid ReporterId, Guid? AssigneeId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt, string? ResolutionNote, string? Summary,
    Guid? SummaryReviewedById, DateTimeOffset? SummaryReviewedAt, Guid Version)
{
    public static IncidentResponse From(Incident incident) => new(
        incident.Id, incident.Title, incident.Description, incident.Severity, incident.Status,
        incident.ReporterId, incident.AssigneeId, incident.CreatedAt, incident.UpdatedAt,
        incident.ResolvedAt, incident.ResolutionNote, incident.Summary,
        incident.SummaryReviewedById, incident.SummaryReviewedAt, incident.Version);
}

public sealed record CommentResponse(Guid Id, Guid IncidentId, Guid AuthorId, string Body, DateTimeOffset CreatedAt);
public sealed record HistoryResponse(Guid Id, Guid IncidentId, Guid ActorId, IncidentStatus? FromStatus,
    IncidentStatus ToStatus, string? Note, DateTimeOffset CreatedAt);
