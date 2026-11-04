namespace RNM.Platform.Application.Qualification;

/// <summary>
/// USPS state and territory codes (50 states, DC, PR, GU, VI, AS, MP) and normalization of caller input.
/// Military codes (AA, AE, AP) are not service areas and are intentionally absent.
/// </summary>
public static class UsStates
{
    private static readonly Dictionary<string, string> NamesByCode = new(StringComparer.Ordinal)
    {
        ["AL"] = "alabama",
        ["AK"] = "alaska",
        ["AZ"] = "arizona",
        ["AR"] = "arkansas",
        ["CA"] = "california",
        ["CO"] = "colorado",
        ["CT"] = "connecticut",
        ["DE"] = "delaware",
        ["DC"] = "district of columbia",
        ["FL"] = "florida",
        ["GA"] = "georgia",
        ["HI"] = "hawaii",
        ["ID"] = "idaho",
        ["IL"] = "illinois",
        ["IN"] = "indiana",
        ["IA"] = "iowa",
        ["KS"] = "kansas",
        ["KY"] = "kentucky",
        ["LA"] = "louisiana",
        ["ME"] = "maine",
        ["MD"] = "maryland",
        ["MA"] = "massachusetts",
        ["MI"] = "michigan",
        ["MN"] = "minnesota",
        ["MS"] = "mississippi",
        ["MO"] = "missouri",
        ["MT"] = "montana",
        ["NE"] = "nebraska",
        ["NV"] = "nevada",
        ["NH"] = "new hampshire",
        ["NJ"] = "new jersey",
        ["NM"] = "new mexico",
        ["NY"] = "new york",
        ["NC"] = "north carolina",
        ["ND"] = "north dakota",
        ["OH"] = "ohio",
        ["OK"] = "oklahoma",
        ["OR"] = "oregon",
        ["PA"] = "pennsylvania",
        ["RI"] = "rhode island",
        ["SC"] = "south carolina",
        ["SD"] = "south dakota",
        ["TN"] = "tennessee",
        ["TX"] = "texas",
        ["UT"] = "utah",
        ["VT"] = "vermont",
        ["VA"] = "virginia",
        ["WA"] = "washington",
        ["WV"] = "west virginia",
        ["WI"] = "wisconsin",
        ["WY"] = "wyoming",
        ["PR"] = "puerto rico",
        ["GU"] = "guam",
        ["VI"] = "virgin islands",
        ["AS"] = "american samoa",
        ["MP"] = "northern mariana islands"
    };

    private static readonly Dictionary<string, string> CodesByName = BuildCodesByName();

    public static IReadOnlyCollection<string> Codes { get; } = NamesByCode.Keys.ToArray();

    public static bool IsValidCode(string? code)
    {
        return code is not null && NamesByCode.ContainsKey(code);
    }

    /// <summary>
    /// Accepts a USPS code or an English state name (case, dots, and extra spaces ignored) and returns the
    /// uppercase USPS code. Anything else, including text with a ZIP or address, is rejected.
    /// </summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var cleaned = string.Join(' ', input.Replace(".", string.Empty, StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (cleaned.Length == 2)
        {
            var upper = cleaned.ToUpperInvariant();
            if (NamesByCode.ContainsKey(upper))
            {
                code = upper;
                return true;
            }

            return false;
        }

        if (CodesByName.TryGetValue(cleaned.ToLowerInvariant(), out var byName))
        {
            code = byName;
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> BuildCodesByName()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, name) in NamesByCode)
        {
            map[name] = code;
        }

        map["us virgin islands"] = "VI";
        map["united states virgin islands"] = "VI";
        map["washington dc"] = "DC";
        map["commonwealth of the northern mariana islands"] = "MP";
        return map;
    }
}
