namespace OpsLookup.Api.Entities;

/// <summary>
/// Contract (Id PK, Name, Code, CreatedDate)
/// </summary>
public class Contract
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public long Code { get; set; }

    public DateTime CreatedDate { get; set; }

    public List<Party> Parties { get; set; } = [];
}