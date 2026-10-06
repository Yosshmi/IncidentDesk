using System.Data.Common;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using IncidentDesk.Api.Auth;
using IncidentDesk.Api.Common;
using IncidentDesk.Api.Incidents;
using IncidentDesk.Api.Persistence;
using IncidentDesk.Api.Summaries;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var seedDemo = args.Contains("--seed-demo", StringComparer.Ordinal);
var hostArgs = args.Where(arg => arg is not "--migrate" and not "--seed-demo").ToArray();
var builder = WebApplication.CreateBuilder(hostArgs);
if (seedDemo && (!migrate || (!builder.Environment.IsDevelopment() && !builder.Configuration.GetValue<bool>("Demo:Enabled"))))
{
    throw new InvalidOperationException("--seed-demo requires --migrate and either Development or explicit Demo:Enabled configuration.");
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<IncidentDbContext>((services, options) =>
{
    var connection = services.GetRequiredService<IConfiguration>().GetConnectionString("IncidentDesk")
        ?? throw new InvalidOperationException("Set ConnectionStrings__IncidentDesk to a PostgreSQL connection string.");
    options.UseNpgsql(connection);
});
builder.Services.AddIncidentDeskIdentity();
builder.Services.AddScoped<IncidentAccess>();
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.AddHttpClient<IIncidentSummaryGenerator, OpenAiIncidentSummaryGenerator>(client =>
{
    // The adapter owns its bounded timeout so it can distinguish it from caller cancellation.
    client.Timeout = Timeout.InfiniteTimeSpan;
});
var protection = builder.Services.AddDataProtection().SetApplicationName("IncidentDesk");
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (builder.Configuration.GetValue<bool>("DataProtection:PersistToDatabase"))
{
    protection.PersistKeysToDbContext<IncidentDbContext>();
}
else if (!string.IsNullOrWhiteSpace(keyPath))
{
    Directory.CreateDirectory(keyPath);
    protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}
builder.Services.AddSingleton<IProblemDetailsWriter, JsonProblemDetailsWriter>();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = 400,
            Title = "Invalid request",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["code"] = "invalid_input";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    };
});
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
    {
        401 => "authentication_required",
        403 => "permission_denied",
        404 => "not_found",
        429 => "rate_limit_exceeded",
        _ => "request_failed"
    });
    context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddIncidentDeskOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("summary", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "Too many requests", detail: "Wait before trying again.",
            extensions: new Dictionary<string, object?> { ["code"] = "rate_limit_exceeded", ["traceId"] = context.HttpContext.TraceIdentifier })
            .ExecuteAsync(context.HttpContext);
    };
});

var app = builder.Build();
if (migrate)
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<IncidentDbContext>();
        await db.Database.MigrateAsync();
    }
    if (seedDemo) await DemoSeeder.SeedAsync(app.Services);
    app.Logger.LogInformation("Database migration completed; demo seed requested: {SeedDemo}", seedDemo);
    await app.DisposeAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/health/ready", async (IncidentDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        if (await db.Database.CanConnectAsync(cancellationToken)
            && !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            return Results.Json(new { status = "Healthy" });
        }
    }
    catch (Exception exception) when (exception is DbException or TimeoutException)
    {
        // Readiness should become unavailable without exposing database error details.
    }
    return Results.Json(new { status = "Unhealthy" }, statusCode: 503);
});
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing") || app.Configuration.GetValue<bool>("Docs:Enabled"))
{
    app.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription();
    app.MapOpenApi();
    app.MapScalarApiReference("/docs", options => options.WithTitle("IncidentDesk API"));
}
app.Run();

public partial class Program;
