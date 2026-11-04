using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Azure.Data.Tables;
using RNM.Platform.Application.Crm;

namespace RNM.Platform.Infrastructure.Crm;

/// <summary>
/// Decides where each contact attribute is stored so a row never exceeds Azure Table Storage's 255-property
/// limit (3 system properties included). Platform-managed attributes always stay as <c>Attr_*</c> columns
/// (some are filtered server-side). Other attributes get a column while room remains and then spill into one
/// JSON column. Placement is a pure function of the existing row and the incoming values, and an attribute
/// never moves from a column back into the JSON.
/// </summary>
internal static class ContactAttributeStorage
{
    public const string ColumnPrefix = "Attr_";
    public const string OverflowPropertyName = "AttributesOverflowJson";

    /// <summary>
    /// 255 properties max, minus 48 reserved: 3 system, about 22 fixed columns (Phone, Email, ...),
    /// 1 overflow column, and spare room for fixed columns added later.
    /// </summary>
    public const int MaxAttributeColumns = 207;

    /// <summary>Kept under the 64 KiB (32K character) Edm.String limit.</summary>
    public const int MaxOverflowJsonLength = 32000;

    public const int MaxValueLength = 32000;

    private const int MaxNameLength = 48;

    private static readonly HashSet<string> AlwaysColumnNames = CrmContactAttributeNames.PlatformManaged
        .Select(SanitizePropertyName)
        .ToHashSet(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions OverflowJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Columns available to attributes that are not platform-managed.</summary>
    public static int UnknownAttributeColumnLimit => MaxAttributeColumns - AlwaysColumnNames.Count;

    public static string SanitizePropertyName(string value)
    {
        var characters = value
            .Where(char.IsLetterOrDigit)
            .Take(MaxNameLength)
            .ToArray();
        return characters.Length == 0 ? "Value" : new string(characters);
    }

    public static string PropertyName(string attributeName) =>
        $"{ColumnPrefix}{SanitizePropertyName(attributeName)}";

    /// <summary>True when the (sanitized) attribute name is platform-managed and must be a real column.</summary>
    public static bool IsAlwaysColumn(string attributeName) =>
        AlwaysColumnNames.Contains(SanitizePropertyName(attributeName));

    public static ContactAttributePlacement Place(
        TableEntity? existing,
        IEnumerable<KeyValuePair<string, string>> attributes)
    {
        // Sanitized-name collisions: the last value wins, as when each was written to the same column.
        var incoming = new Dictionary<string, string>(StringComparer.Ordinal);
        var truncated = 0;
        foreach (var attribute in attributes)
        {
            var value = attribute.Value;
            if (value.Length > MaxValueLength)
            {
                value = value[..MaxValueLength];
                truncated++;
            }

            incoming[SanitizePropertyName(attribute.Key)] = value;
        }

        var existingColumns = existing is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : existing.Keys
                .Where(key => key.StartsWith(ColumnPrefix, StringComparison.Ordinal))
                .Select(key => key[ColumnPrefix.Length..])
                .ToHashSet(StringComparer.Ordinal);
        var overflow = existing is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ReadOverflow(existing);
        var originalOverflowKeys = overflow.Keys.ToHashSet(StringComparer.Ordinal);

        var unknownColumns = existingColumns.Count(name => !AlwaysColumnNames.Contains(name));
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        var overflowCandidates = new List<KeyValuePair<string, string>>();
        var overflowChanged = false;
        foreach (var (name, value) in incoming)
        {
            if (AlwaysColumnNames.Contains(name) || existingColumns.Contains(name))
            {
                columns[ColumnPrefix + name] = value;
                overflowChanged |= overflow.Remove(name);
            }
            else if (overflow.ContainsKey(name))
            {
                overflowCandidates.Add(new(name, value));
            }
            else if (unknownColumns < UnknownAttributeColumnLimit)
            {
                columns[ColumnPrefix + name] = value;
                unknownColumns++;
            }
            else
            {
                overflowCandidates.Add(new(name, value));
            }
        }

        var dropped = 0;
        foreach (var (name, value) in overflowCandidates)
        {
            var hadKey = overflow.TryGetValue(name, out var previous);
            overflow[name] = value;
            if (Serialize(overflow).Length > MaxOverflowJsonLength)
            {
                // Does not fit: keep the previous state and count the loss.
                if (hadKey)
                {
                    overflow[name] = previous!;
                }
                else
                {
                    overflow.Remove(name);
                }

                dropped++;
                continue;
            }

            overflowChanged = true;
        }

        string? overflowJson = null;
        var overflowLength = 0;
        if (overflowChanged)
        {
            overflowJson = overflow.Count == 0 ? string.Empty : Serialize(overflow);
            overflowLength = overflowJson.Length;
        }
        else if (overflow.Count > 0)
        {
            overflowLength = Serialize(overflow).Length;
        }

        var newOverflow = overflow.Keys.Count(name => !originalOverflowKeys.Contains(name));
        var totalColumns = existingColumns.Union(columns.Keys.Select(key => key[ColumnPrefix.Length..])).Count();
        return new ContactAttributePlacement(
            columns,
            overflowJson,
            totalColumns,
            overflow.Count,
            newOverflow,
            dropped,
            truncated,
            overflowLength);
    }

    /// <summary>All attributes of a row: <c>Attr_*</c> columns plus the overflow JSON; a column wins.</summary>
    public static IReadOnlyDictionary<string, string> ReadAll(TableEntity entity)
    {
        var attributes = entity
            .Where(property => property.Key.StartsWith(ColumnPrefix, StringComparison.Ordinal))
            .Where(property => property.Value is not null)
            .ToDictionary(
                property => property.Key[ColumnPrefix.Length..],
                property => property.Value.ToString() ?? string.Empty,
                StringComparer.Ordinal);

        foreach (var (name, value) in ReadOverflow(entity))
        {
            attributes.TryAdd(name, value);
        }

        return attributes;
    }

    public static string? ReadAttribute(TableEntity? entity, string attributeName)
    {
        if (entity is null)
        {
            return null;
        }

        if (entity.TryGetValue(PropertyName(attributeName), out var column) && column is not null)
        {
            return column.ToString();
        }

        return ReadOverflow(entity).TryGetValue(SanitizePropertyName(attributeName), out var value) ? value : null;
    }

    private static Dictionary<string, string> ReadOverflow(TableEntity entity)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!entity.TryGetValue(OverflowPropertyName, out var raw)
            || raw is not string json
            || string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? empty
                : new Dictionary<string, string>(parsed.Where(pair => pair.Value is not null), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Unparseable overflow is ignored; the next overflow write replaces it.
            return empty;
        }
    }

    private static string Serialize(Dictionary<string, string> overflow) =>
        JsonSerializer.Serialize(overflow, OverflowJsonOptions);
}

internal sealed record ContactAttributePlacement(
    IReadOnlyDictionary<string, string> Columns,
    string? OverflowJson,
    int AttributeColumnCount,
    int OverflowAttributeCount,
    int NewOverflowAttributeCount,
    int DroppedAttributeCount,
    int TruncatedValueCount,
    int OverflowJsonLength);
