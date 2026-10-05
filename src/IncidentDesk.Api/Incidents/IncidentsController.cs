using IncidentDesk.Api.Auth;
using IncidentDesk.Api.Common;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Incidents;

[ApiController]
[Route("api/v1/incidents")]
[Authorize]
public sealed class IncidentsController(
    IncidentDbContext db, IncidentAccess access, UserManager<AppUser> users, TimeProvider clock) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<IncidentResponse>> Create(CreateIncidentRequest request, CancellationToken cancellationToken)
    {
        var actorId = CurrentUser.Id(User);
        var now = clock.GetUtcNow();
        var incident = Incident.Create(request.Title, request.Description, request.Severity, actorId, now);
        db.Incidents.Add(incident);
        db.StatusHistory.Add(StatusHistory.Create(incident.Id, actorId, null, IncidentStatus.Open, null, now));
        await db.SaveChangesAsync(cancellationToken);
        SetVersion(incident);
        return CreatedAtAction(nameof(Get), new { id = incident.Id }, IncidentResponse.From(incident));
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<IncidentResponse>>> List([FromQuery] IncidentQuery request, CancellationToken cancellationToken)
    {
        var query = access.VisibleTo(User).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Q))
        {
            var pattern = "%" + request.Q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(incident => EF.Functions.ILike(incident.Title, pattern, "\\")
                || EF.Functions.ILike(incident.Description, pattern, "\\"));
        }
        if (request.Status.HasValue) query = query.Where(i => i.Status == request.Status.Value);
        if (request.Severity.HasValue) query = query.Where(i => i.Severity == request.Severity.Value);
        if (request.ReporterId.HasValue) query = query.Where(i => i.ReporterId == request.ReporterId.Value);
        if (request.AssigneeId.HasValue) query = query.Where(i => i.AssigneeId == request.AssigneeId.Value);
        if (request.Unassigned == true) query = query.Where(i => i.AssigneeId == null);
        if (request.Unassigned == false) query = query.Where(i => i.AssigneeId != null);
        if (request.CreatedFrom.HasValue)
        {
            var from = request.CreatedFrom.Value.ToUniversalTime();
            query = query.Where(i => i.CreatedAt >= from);
        }
        if (request.CreatedTo.HasValue)
        {
            var to = request.CreatedTo.Value.ToUniversalTime();
            query = query.Where(i => i.CreatedAt <= to);
        }
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToListAsync(cancellationToken);
        return Ok(new PagedResult<IncidentResponse>(items.Select(IncidentResponse.From).ToList(), request.Page, request.PageSize, count));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<IncidentResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var incident = await access.FindAsync(id, User, cancellationToken);
        return IncidentResult(incident);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Engineer")]
    public async Task<ActionResult<IncidentResponse>> Update(Guid id, UpdateIncidentRequest request, CancellationToken cancellationToken)
    {
        var incident = await LoadForWrite(id, cancellationToken);
        incident.UpdateDetails(request.Title, request.Description, request.Severity, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return IncidentResult(incident);
    }

    [HttpPut("{id:guid}/assignment")]
    [Authorize(Roles = "Engineer")]
    public async Task<ActionResult<IncidentResponse>> Assign(Guid id, AssignIncidentRequest request, CancellationToken cancellationToken)
    {
        var incident = await LoadForWrite(id, cancellationToken);
        if (request.AssigneeId.HasValue)
        {
            var user = await users.FindByIdAsync(request.AssigneeId.Value.ToString());
            if (user is null || !await users.IsInRoleAsync(user, "Engineer"))
            {
                throw new ApiException(400, "invalid_assignee", "The assignee must be an existing engineer.", "assigneeId");
            }
        }
        incident.Assign(request.AssigneeId, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return IncidentResult(incident);
    }

    [HttpPost("{id:guid}/transitions")]
    [Authorize(Roles = "Engineer")]
    public async Task<ActionResult<IncidentResponse>> Transition(Guid id, TransitionIncidentRequest request, CancellationToken cancellationToken)
    {
        var incident = await LoadForWrite(id, cancellationToken);
        var previous = incident.Status;
        var now = clock.GetUtcNow();
        var target = request.Status ?? throw new ApiException(400, "invalid_input", "Status is required.", "status");
        incident.Transition(target, request.ResolutionNote, now);
        db.StatusHistory.Add(StatusHistory.Create(id, CurrentUser.Id(User), previous, target,
            target == IncidentStatus.Resolved ? incident.ResolutionNote : null, now));
        await db.SaveChangesAsync(cancellationToken);
        return IncidentResult(incident);
    }

    [HttpGet("{id:guid}/comments")]
    public async Task<ActionResult<PagedResult<CommentResponse>>> Comments(Guid id, [FromQuery] PageQuery request, CancellationToken cancellationToken)
    {
        await access.FindAsync(id, User, cancellationToken);
        var query = db.Comments.AsNoTracking().Where(comment => comment.IncidentId == id);
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize).Select(c => new CommentResponse(c.Id, c.IncidentId, c.AuthorId, c.Body, c.CreatedAt))
            .ToListAsync(cancellationToken);
        return Ok(new PagedResult<CommentResponse>(items, request.Page, request.PageSize, count));
    }

    [HttpPost("{id:guid}/comments")]
    public async Task<ActionResult<CommentResponse>> AddComment(Guid id, AddCommentRequest request, CancellationToken cancellationToken)
    {
        var incident = await LoadForWrite(id, cancellationToken);
        var comment = incident.AddComment(request.Body, CurrentUser.Id(User), clock.GetUtcNow());
        db.Comments.Add(comment);
        await db.SaveChangesAsync(cancellationToken);
        SetVersion(incident);
        return StatusCode(201, new CommentResponse(comment.Id, comment.IncidentId, comment.AuthorId, comment.Body, comment.CreatedAt));
    }

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<PagedResult<HistoryResponse>>> History(Guid id, [FromQuery] PageQuery request, CancellationToken cancellationToken)
    {
        await access.FindAsync(id, User, cancellationToken);
        var query = db.StatusHistory.AsNoTracking().Where(history => history.IncidentId == id);
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(h => h.CreatedAt).ThenBy(h => h.Id).Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize).Select(h => new HistoryResponse(h.Id, h.IncidentId, h.ActorId, h.FromStatus, h.ToStatus, h.Note, h.CreatedAt))
            .ToListAsync(cancellationToken);
        return Ok(new PagedResult<HistoryResponse>(items, request.Page, request.PageSize, count));
    }

    [HttpPut("{id:guid}/summary")]
    [Authorize(Roles = "Engineer")]
    public async Task<ActionResult<IncidentResponse>> SaveSummary(Guid id, SaveSummaryRequest request, CancellationToken cancellationToken)
    {
        var incident = await LoadForWrite(id, cancellationToken);
        incident.SaveSummary(request.Text, CurrentUser.Id(User), clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return IncidentResult(incident);
    }

    private async Task<Incident> LoadForWrite(Guid id, CancellationToken cancellationToken)
    {
        var incident = await access.FindAsync(id, User, cancellationToken);
        IncidentVersions.RequireMatch(Request, incident.Version);
        return incident;
    }

    private void SetVersion(Incident incident) => Response.Headers.ETag = IncidentVersions.Format(incident.Version);

    private ActionResult<IncidentResponse> IncidentResult(Incident incident)
    {
        SetVersion(incident);
        return Ok(IncidentResponse.From(incident));
    }
}
