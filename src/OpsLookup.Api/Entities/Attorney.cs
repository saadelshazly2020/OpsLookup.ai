namespace OpsLookup.Api.Entities;

/// <summary>
/// Attorney (Id PK, Name, Status)
/// </summary>
public class Attorney
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="OpsLookup.Api.Security.AttorneyStatus" />. Constrained in
    /// the database as well as in code, so a bad value cannot arrive by any route.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }

    public List<Party> Parties { get; set; } = [];
}