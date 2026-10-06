using IncidentDesk.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace IncidentDesk.Api.Common;

public static class OpenApiConfiguration
{
    public static IServiceCollection AddIncidentDeskOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Info.Title = "IncidentDesk";
                document.Info.Version = "v1";
                document.Servers = [new OpenApiServer { Url = "/" }];
                document.Info.Description = "Incident investigation and support-ticket API. Sign in using POST /api/v1/auth/login, then supply the returned accessToken using Bearer authentication. Existing-incident writes require the quoted ETag from GET in If-Match.";
                if (context.ApplicationServices.GetRequiredService<IConfiguration>().GetValue<bool>("Demo:Enabled"))
                {
                    document.Info.Description += $"\n\n## Public fictional demo\nThis shared sandbox contains fictional data. Use only fictional input; other visitors can view or modify shared demo incidents.\n\n" +
                        $"All demo accounts use password `{DemoSeeder.Password}`.\n\n" +
                        "| Email | Role |\n| --- | --- |\n" +
                        "| reporter1@example.test | Reporter |\n| reporter2@example.test | Reporter |\n" +
                        "| engineer1@example.test | Engineer |\n| engineer2@example.test | Engineer |\n\n" +
                        "Log in, copy accessToken into Bearer authentication, then try the incident endpoints. The free hosting service can take a minute to wake up after inactivity.";
                }
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "ASP.NET Core Identity access token from POST /api/v1/auth/login."
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                {
                    operation.Security = [new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
                    }];
                }
                if (context.Description.RelativePath?.Contains("{id}", StringComparison.Ordinal) == true
                    && context.Description.HttpMethod is "PUT" or "POST"
                    && !context.Description.RelativePath.EndsWith("summary-draft", StringComparison.Ordinal))
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "If-Match",
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = "The quoted ETag returned by GET /api/v1/incidents/{id}.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String }
                    });
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
