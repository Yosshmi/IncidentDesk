using IncidentDesk.Api.Common;
using Microsoft.AspNetCore.Http;

namespace IncidentDesk.UnitTests;

public sealed class IncidentVersionHeadersTests
{
    [Fact]
    public void Original_version_remains_available_when_a_proxy_weakens_the_standard_ETag()
    {
        var context = new DefaultHttpContext();
        var version = Guid.NewGuid();
        IncidentVersions.WriteHeaders(context.Response, version);
        var original = context.Response.Headers.ETag.ToString();
        context.Response.Headers.ETag = "W/" + original;
        Assert.Equal(original, context.Response.Headers["X-Incident-ETag"].ToString());
        context.Request.Headers.IfMatch = context.Response.Headers["X-Incident-ETag"];
        IncidentVersions.RequireMatch(context.Request, version);
        Assert.Throws<ApiException>(() => IncidentVersions.RequireMatch(context.Request, Guid.NewGuid()));
    }
}
