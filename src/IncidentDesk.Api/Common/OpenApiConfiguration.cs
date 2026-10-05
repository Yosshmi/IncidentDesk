using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace IncidentDesk.Api.Common;

public static class OpenApiConfiguration
{
    public static IServiceCollection AddIncidentDeskOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "IncidentDesk";
                document.Info.Version = "v1";
                document.Info.Description = "Incident investigation and support-ticket API. Sign in with a demo account, then supply its access token using Bearer authentication. Existing-incident writes require the ETag from GET in If-Match.";
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
