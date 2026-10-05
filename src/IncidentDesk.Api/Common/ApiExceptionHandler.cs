using IncidentDesk.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentDesk.Api.Common;

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
            return true;
        }

        var (status, code, detail, field) = exception switch
        {
            ApiException error => (error.Status, error.Code, error.Message, error.Field),
            DomainException error => (error.Code == "invalid_input" ? 400 : 409, error.Code, error.Message, error.Field),
            DbUpdateConcurrencyException => (412, "version_conflict", "This incident changed. Fetch its latest version and review your changes before retrying.", null),
            BadHttpRequestException error => (error.StatusCode, "invalid_request", "The request could not be read. Check its content type and body.", null),
            _ => (500, "internal_error", "An unexpected error occurred. Use the trace ID when reporting this problem.", (string?)null)
        };

        if (status >= 500)
        {
            // Do not log request bodies, provider responses, credentials, or incident notes.
            logger.LogError("Request failed with {ExceptionType}; trace {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        }

        context.Response.StatusCode = status;
        ProblemDetails problem = field is null
            ? new ProblemDetails()
            : new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = [detail] });
        problem.Status = status;
        problem.Title = Titles.ForStatus(status);
        problem.Detail = detail;
        problem.Instance = context.Request.Path;
        problem.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}

internal static class Titles
{
    internal static string ForStatus(int status) => status switch
    {
        400 => "Invalid request",
        401 => "Authentication required",
        403 => "Permission denied",
        404 => "Resource not found",
        409 => "Operation conflicts with incident state",
        412 => "Incident version conflict",
        428 => "Incident version required",
        429 => "Too many requests",
        502 => "Summary provider failed",
        503 => "Service unavailable",
        504 => "Summary provider timed out",
        _ => "Request failed"
    };
}
