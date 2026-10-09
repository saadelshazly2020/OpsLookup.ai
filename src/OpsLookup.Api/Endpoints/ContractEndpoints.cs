using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OpsLookup.Api.Contracts;
using OpsLookup.Api.Data;
using OpsLookup.Api.Security;

namespace OpsLookup.Api.Endpoints;

public static class ContractEndpoints
{
    public static IEndpointRouteBuilder MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts").RequireAuthorization();

        group.MapGet("", GetContractsAsync)
            .WithName("GetContracts")
            .WithSummary("Look up contracts for a party social ID.")
            .WithDescription(
                "Returns every contract held by the party with this social ID. "
                + "Supply `name` to narrow results to parties whose name contains it. "
                + "An empty array means no contracts matched; that is not an error.")
            .Produces<IReadOnlyList<ContractResult>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<ContractResult>>, ProblemHttpResult>> GetContractsAsync(
        IContractRepository repository,
        CancellationToken cancellationToken,
        [FromQuery] long socialId,
        [FromQuery] string? name)
    {
        if (!SocialIdRule.IsValid(socialId))
        {
            return TypedResults.Problem(
                title: "Invalid social ID",
                detail: SocialIdRule.Description,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var results = await repository.GetBySocialIdAsync(socialId, name, cancellationToken);

        return TypedResults.Ok(results);
    }
}