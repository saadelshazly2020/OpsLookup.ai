namespace OpsLookup.Api.Entities;

/// <summary>
/// Party (Id PK, Name, ContractId FK, SocialId, CreatedDate)
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

    public DateTime CreatedDate { get; set; }

    public Contract Contract { get; set; } = null!;
}