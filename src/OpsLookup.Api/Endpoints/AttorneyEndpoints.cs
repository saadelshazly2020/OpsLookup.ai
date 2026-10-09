using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Data;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Endpoints;

public static class AttorneyEndpoints
{
    public static IEndpointRouteBuilder MapAttorneyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/attorneys").RequireAuthorization();

        group.MapGet("", SearchAsync)
            .WithName("SearchAttorneys")
            .WithSummary("Look up the attorney acting for a party.")
            .WithDescription(
                "Finds attorneys through the parties they act for. Supply `socialId`, "
                + "`partyName`, or both. Parties with no attorney on file are not "
                + "returned. An empty array means nothing matched; that is not an error.")
            .Produces<IReadOnlyList<AttorneyResult>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<AttorneyResult>>, ProblemHttpResult>> SearchAsync(
        IAttorneyRepository repository,
        CancellationToken cancellationToken,
        [FromQuery] long? socialId,
        [FromQuery] string? partyName)
    {
        // At least one filter is required. Without it this endpoint would return
        // every attorney on file, which is a data dump rather than a lookup.
        if (socialId is null && string.IsNullOrWhiteSpace(partyName))
        {
            return TypedResults.Problem(
                title: "Missing search criteria",
                detail: "Provide socialId, partyName, or both.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Validate only when supplied, so a name-only search still works.
        if (socialId is not null && !SocialIdRule.IsValid(socialId.Value))
        {
            return TypedResults.Problem(
                title: "Invalid social ID",
                detail: SocialIdRule.Description,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var results = await repository.SearchAsync(socialId, partyName, cancellationToken);

        return TypedResults.Ok(results);
    }
}