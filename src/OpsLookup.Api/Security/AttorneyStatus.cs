namespace OpsLookup.Api.Security;

/// <summary>
/// The only values <c>Attorney.Status</c> may take.
/// </summary>
/// <remarks>
/// Stored as a string rather than an int enum so the database stays readable and
/// adding a value does not renumber existing rows. Enforced twice: here, and as a
/// check constraint in <c>ContractDbContext</c>, so an invalid value cannot be
/// written by SQL either.
/// </remarks>
public static class AttorneyStatus
{
    /// <summary>Verified and cleared to act.</summary>
    public const string Approved = "approved";

    /// <summary>Flagged as fraudulent.</summary>
    public const string Faked = "faked";

    public static readonly IReadOnlyList<string> All = [Approved, Faked];

    public static bool IsValid(string? status) =>
        status is not null && All.Contains(status, StringComparer.OrdinalIgnoreCase);

    /// <summary>Human-readable list for error messages and tool descriptions.</summary>
    public const string Description = "approved or faked";
}