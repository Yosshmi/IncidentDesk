using System.Security.Claims;
using IncidentDesk.Api.Common;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Incidents;

public sealed class IncidentAccess(IncidentDbContext db)
{
    public IQueryable<Incident> VisibleTo(ClaimsPrincipal user)
    {
        var query = db.Incidents.AsQueryable();
        if (!CurrentUser.IsEngineer(user))
        {
            var userId = CurrentUser.Id(user);
            query = query.Where(incident => incident.ReporterId == userId);
        }
        return query;
    }

    public async Task<Incident> FindAsync(Guid id, ClaimsPrincipal user, CancellationToken cancellationToken)
        => await VisibleTo(user).SingleOrDefaultAsync(incident => incident.Id == id, cancellationToken)
            ?? throw new ApiException(404, "incident_not_found", "The incident could not be found.");
}
