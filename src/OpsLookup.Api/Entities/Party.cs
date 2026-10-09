namespace OpsLookup.Api.Entities;

/// <summary>
/// Party (Id PK, Name, ContractId FK, SocialId, AttorneyId FK, CreatedDate)
/// </summary>
public class Party
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long ContractId { get; set; }

    /// <summary>
    /// 10 digits, must start with 1 or 2. See <see cref="OpsLookup.Api.Security.SocialIdRule" />.
    /// </summary>
    public long SocialId { get; set; }

    /// <summary>
    /// Optional. A party may have no attorney on file, so this is nullable rather
    /// than a required relationship.
    /// </summary>
    public int? AttorneyId { get; set; }

    public DateTime CreatedDate { get; set; }

    public Contract Contract { get; set; } = null!;

    public Attorney? Attorney { get; set; }
}