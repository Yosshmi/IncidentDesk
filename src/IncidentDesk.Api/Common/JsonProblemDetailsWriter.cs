using System.Text.Json;
using Microsoft.Extensions.Options;

namespace IncidentDesk.Api.Common;

/// <summary>API errors always have a JSON body, including requests with a non-JSON Accept header.</summary>
public sealed class JsonProblemDetailsWriter(IOptions<ProblemDetailsOptions> options) : IProblemDetailsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        context.ProblemDetails.Status ??= context.HttpContext.Response.StatusCode;
        options.Value.CustomizeProblemDetails?.Invoke(context);
        context.HttpContext.Response.ContentType = "application/problem+json";
        return new ValueTask(JsonSerializer.SerializeAsync(context.HttpContext.Response.Body,
            context.ProblemDetails, context.ProblemDetails.GetType(), JsonOptions,
            context.HttpContext.RequestAborted));
    }
}
