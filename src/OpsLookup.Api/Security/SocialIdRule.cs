using System.Globalization;
using System.Text.RegularExpressions;

namespace OpsLookup.Api.Security;

/// <summary>
/// Social IDs are 10 digits and always start with 1 or 2.
/// </summary>
/// <remarks>
/// This exists because a mistyped social ID and a non-existent social ID both return
/// an empty result set. Without validation an operations member reads "no contracts
/// found" as "this customer does not exist", which is a wrong answer rather than a
/// failed lookup.
/// </remarks>
public static partial class SocialIdRule
{
    public const string Description = "Social ID must be 10 digits starting with 1 or 2.";

    [GeneratedRegex(@"^[12]\d{9}$")]
    private static partial Regex Pattern();

    public static bool IsValid(long value) =>
        value > 0 && Pattern().IsMatch(value.ToString(CultureInfo.InvariantCulture));
}