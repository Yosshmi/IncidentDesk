using System.Data;
using IncidentDesk.Api.Common;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Summaries;

[ApiController]
[Route("api/v1/incidents")]
[Authorize(Roles = "Engineer")]
public sealed class SummariesController(
    IncidentDbContext db, IncidentAccess access, IIncidentSummaryGenerator generator) : ControllerBase
{
    [HttpPost("{id:guid}/summary-draft")]
    [EnableRateLimiting("summary")]
    public async Task<ActionResult<SummaryDraftResponse>> Generate(Guid id, CancellationToken cancellationToken)
    {
        SummaryInput input;
        Guid sourceVersion;
        // Read the incident and its notes from one snapshot. Release the database
        // transaction before making the network call; the draft retains that version.
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
        {
            var incident = await access.FindAsync(id, User, cancellationToken);
            var notes = await db.Comments.AsNoTracking().Where(comment => comment.IncidentId == id)
                .OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id).Take(101)
                .Select(comment => new SummaryNote(comment.Body, comment.CreatedAt)).ToListAsync(cancellationToken);
            input = new SummaryInput(incident.Title, incident.Description, incident.Status.ToString(), incident.ResolutionNote, notes);
            sourceVersion = incident.Version;
            await transaction.CommitAsync(cancellationToken);
        }

        var text = await generator.GenerateAsync(input, cancellationToken);
        Response.Headers.ETag = IncidentVersions.Format(sourceVersion);
        Response.Headers.CacheControl = "no-store";
        return Ok(new SummaryDraftResponse(text, sourceVersion));
    }
}

public sealed record SummaryDraftResponse(string Text, Guid SourceVersion);
