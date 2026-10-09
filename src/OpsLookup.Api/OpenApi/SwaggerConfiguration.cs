using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace OpsLookup.Api.OpenApi;

/// <summary>
/// Shapes the OpenAPI document so Swagger UI has a working Authorize button.
/// </summary>
/// <remarks>
/// Signatures follow the .NET 9 OpenAPI package, where document and operation
/// transformers return the mutated object rather than just completing a task.
/// </remarks>
public static class SwaggerConfiguration
{
    public const string BearerScheme = "Bearer";

    public static void AddOpsLookupDocument(this OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "OpsLookup API",
                Version = "v1",
                Description = """
                    Contract lookup for IT operations tickets.

                    **Getting a token**
                    1. Expand `POST /auth/dev-token` and click **Try it out**, then
                       **Execute**. This one needs no token.
                    2. Copy the `accessToken` value from the response body.
                    3. Click the **Authorize** button at the top of this page and paste
                       that token. The `Bearer ` prefix is optional.

                    Every other endpoint can then be executed straight from this page.

                    Note: `/auth/dev-token` is registered only when the environment is
                    Development and cannot ship.
                    """,
            };

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??=
                new Dictionary<string, OpenApiSecurityScheme>();
            document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the accessToken returned by /auth/dev-token.",
                Name = "Authorization",
                In = ParameterLocation.Header,
            };

            return Task.FromResult(document);
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            // Microsoft.OpenApi 1.6 has no document-level Security, so the requirement
            // is attached per operation instead.
            var isAnonymous = context.Description.ActionDescriptor.EndpointMetadata
                .OfType<IAllowAnonymous>()
                .Any();

            operation.Security = isAnonymous
                ? new List<OpenApiSecurityRequirement>()
                : new List<OpenApiSecurityRequirement>
                {
                    new()
                    {
                        [new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = BearerScheme,
                            },
                        }] = new List<string>(),
                    },
                };

            return Task.FromResult(operation);
        });
    }
}