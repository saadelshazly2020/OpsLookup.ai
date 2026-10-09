namespace OpsLookup.Api.Contracts;

/// <summary>
/// Response shape for GET /api/contracts
/// </summary>
public sealed record ContractResult(
    long ContractCode,
    long SocialId,
    string PartyName,
    string ContractName);