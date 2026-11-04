using System.Text.Json;
using Azure.Data.Tables;
using RNM.Platform.Application.Crm;
using RNM.Platform.Infrastructure.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Crm;

public sealed class ContactAttributeStorageTests
{
    private static int Limit => ContactAttributeStorage.UnknownAttributeColumnLimit;

    [Fact]
    public void Budget_Invariants_KeepRowUnderTableStorageLimit()
    {
        // 255 properties max; 48 reserved for system, fixed columns, the overflow column, and spare room.
        Assert.Equal(207, ContactAttributeStorage.MaxAttributeColumns);
        Assert.Equal(
            ContactAttributeStorage.MaxAttributeColumns - CrmContactAttributeNames.PlatformManaged.Count,
            Limit);
        Assert.True(Limit > 100);
        Assert.True(ContactAttributeStorage.MaxOverflowJsonLength < 32768);
        Assert.False(ContactAttributeStorage.OverflowPropertyName.StartsWith("Attr_", StringComparison.Ordinal));
    }

    [Fact]
    public void Place_UnderLimit_WritesColumnsAndNoOverflow()
    {
        var placement = ContactAttributeStorage.Place(null, Pairs(5));

        Assert.Equal(5, placement.Columns.Count);
        Assert.All(placement.Columns.Keys, key => Assert.StartsWith("Attr_", key));
        Assert.Null(placement.OverflowJson);
        Assert.Equal(0, placement.OverflowAttributeCount);
        Assert.Equal(0, placement.NewOverflowAttributeCount);
        Assert.Equal(0, placement.DroppedAttributeCount);
    }

    [Fact]
    public void Place_300UnknownOnNewRow_FillsColumnLimitAndOverflowsTheRest()
    {
        var placement = ContactAttributeStorage.Place(null, Pairs(300));

        Assert.Equal(Limit, placement.Columns.Count);
        Assert.Equal(300 - Limit, placement.OverflowAttributeCount);
        Assert.Equal(300 - Limit, placement.NewOverflowAttributeCount);
        Assert.Equal(0, placement.DroppedAttributeCount);
        var overflow = ParseOverflow(placement.OverflowJson);
        Assert.Equal(300 - Limit, overflow.Count);
        Assert.Equal("v299", overflow["extra299"]);
        Assert.DoesNotContain("Attr_extra299", placement.Columns.Keys);
    }

    [Fact]
    public void Place_KnownAttributeLastInRequest_IsStillAColumn()
    {
        var attributes = Pairs(300).Append(new KeyValuePair<string, string>("leadStatus", "qualified"));

        var placement = ContactAttributeStorage.Place(null, attributes);

        Assert.Equal("qualified", placement.Columns["Attr_leadStatus"]);
        Assert.DoesNotContain("leadStatus", ParseOverflow(placement.OverflowJson).Keys);
        Assert.Equal(Limit + 1, placement.Columns.Count);
    }

    [Fact]
    public void Place_UpdatingKeyAlreadyInOverflow_StaysInOverflow()
    {
        var first = ContactAttributeStorage.Place(null, Pairs(300));
        var existing = ToEntity(first);

        var second = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("extra299", "changed")]);

        Assert.Empty(second.Columns);
        Assert.Equal("changed", ParseOverflow(second.OverflowJson)["extra299"]);
        Assert.Equal(300 - Limit, second.OverflowAttributeCount);
        Assert.Equal(0, second.NewOverflowAttributeCount);
    }

    [Fact]
    public void Place_UpdatingExistingColumnWhenFull_StaysColumn()
    {
        var existing = ToEntity(ContactAttributeStorage.Place(null, Pairs(300)));

        var second = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("extra000", "changed")]);

        Assert.Equal("changed", second.Columns["Attr_extra000"]);
        Assert.Null(second.OverflowJson);
        Assert.Equal(0, second.NewOverflowAttributeCount);
    }

    [Fact]
    public void Place_NewUnknownNameWhenFull_GoesToOverflow()
    {
        var existing = ToEntity(ContactAttributeStorage.Place(null, Pairs(Limit)));

        var second = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("brandnew", "x")]);

        Assert.Empty(second.Columns);
        Assert.Equal("x", ParseOverflow(second.OverflowJson)["brandnew"]);
        Assert.Equal(1, second.NewOverflowAttributeCount);
    }

    [Fact]
    public void Place_KnownNameFoundInOverflow_MovesToColumn()
    {
        var existing = new TableEntity("tenant", "row")
        {
            [ContactAttributeStorage.OverflowPropertyName] = "{\"leadStatus\":\"old\",\"other\":\"keep\"}"
        };

        var placement = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("leadStatus", "new")]);

        Assert.Equal("new", placement.Columns["Attr_leadStatus"]);
        var overflow = ParseOverflow(placement.OverflowJson);
        Assert.DoesNotContain("leadStatus", overflow.Keys);
        Assert.Equal("keep", overflow["other"]);
    }

    [Fact]
    public void Place_MovingLastOverflowEntryToColumn_ClearsOverflowColumn()
    {
        var existing = new TableEntity("tenant", "row")
        {
            [ContactAttributeStorage.OverflowPropertyName] = "{\"leadStatus\":\"old\"}"
        };

        var placement = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("leadStatus", "new")]);

        Assert.Equal(string.Empty, placement.OverflowJson);
    }

    [Fact]
    public void Place_OverflowBeyondCap_DropsExtraEntriesAndCountsThem()
    {
        var bigValue = new string('x', 5000);
        var attributes = Enumerable.Range(0, Limit + 20)
            .Select(i => new KeyValuePair<string, string>($"field{i:000}", i < Limit ? "a" : bigValue))
            .ToList();

        var placement = ContactAttributeStorage.Place(null, attributes);

        Assert.True(placement.OverflowJson!.Length <= ContactAttributeStorage.MaxOverflowJsonLength);
        Assert.True(placement.DroppedAttributeCount > 0);
        Assert.Equal(20, placement.OverflowAttributeCount + placement.DroppedAttributeCount);
        Assert.Equal(placement.OverflowAttributeCount, ParseOverflow(placement.OverflowJson).Count);
    }

    [Fact]
    public void Place_ValueOverCap_IsTruncatedAndCounted()
    {
        var placement = ContactAttributeStorage.Place(
            null,
            [new KeyValuePair<string, string>("notes", new string('y', 40000))]);

        Assert.Equal(ContactAttributeStorage.MaxValueLength, placement.Columns["Attr_notes"].Length);
        Assert.Equal(1, placement.TruncatedValueCount);
    }

    [Fact]
    public void Place_SanitizedNameCollision_LastWins()
    {
        var placement = ContactAttributeStorage.Place(
            null,
            [
                new KeyValuePair<string, string>("first_name", "one"),
                new KeyValuePair<string, string>("first-name", "two")
            ]);

        Assert.Equal("two", placement.Columns["Attr_firstname"]);
        Assert.Single(placement.Columns);
    }

    [Fact]
    public void SanitizePropertyName_KeepsExistingBehavior()
    {
        Assert.Equal("firstname", ContactAttributeStorage.SanitizePropertyName("first_name"));
        Assert.Equal("Value", ContactAttributeStorage.SanitizePropertyName("___"));
        Assert.Equal(48, ContactAttributeStorage.SanitizePropertyName(new string('a', 80)).Length);
        Assert.Equal("Attr_leadStatus", ContactAttributeStorage.PropertyName("leadStatus"));
    }

    [Fact]
    public void ReadAll_MergesColumnsAndOverflow_ColumnWins()
    {
        var entity = new TableEntity("tenant", "row")
        {
            ["Attr_a"] = "column",
            [ContactAttributeStorage.OverflowPropertyName] = "{\"a\":\"overflow\",\"b\":\"only\"}"
        };

        var all = ContactAttributeStorage.ReadAll(entity);

        Assert.Equal("column", all["a"]);
        Assert.Equal("only", all["b"]);
        Assert.Equal("only", ContactAttributeStorage.ReadAttribute(entity, "b"));
        Assert.Equal("column", ContactAttributeStorage.ReadAttribute(entity, "a"));
        Assert.Null(ContactAttributeStorage.ReadAttribute(entity, "missing"));
        Assert.Null(ContactAttributeStorage.ReadAttribute(null, "a"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public void ReadAll_InvalidOrEmptyOverflow_IsIgnored(string overflow)
    {
        var entity = new TableEntity("tenant", "row")
        {
            ["Attr_a"] = "column",
            [ContactAttributeStorage.OverflowPropertyName] = overflow
        };

        var all = ContactAttributeStorage.ReadAll(entity);

        Assert.Single(all);
        Assert.Equal("column", all["a"]);
    }

    [Fact]
    public void Place_InvalidExistingOverflow_IsReplacedOnNextOverflowWrite()
    {
        var existing = ToEntity(ContactAttributeStorage.Place(null, Pairs(Limit)));
        existing[ContactAttributeStorage.OverflowPropertyName] = "not json";

        var placement = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("brandnew", "x")]);

        Assert.Equal("x", ParseOverflow(placement.OverflowJson)["brandnew"]);
    }

    [Fact]
    public void Place_NonAsciiValues_AreNotEscapedBeyondJson()
    {
        var existing = ToEntity(ContactAttributeStorage.Place(null, Pairs(Limit)));

        var placement = ContactAttributeStorage.Place(
            existing,
            [new KeyValuePair<string, string>("greeting", "Hola, ¿cómo está?")]);

        Assert.Equal("Hola, ¿cómo está?", ParseOverflow(placement.OverflowJson)["greeting"]);
        Assert.Contains("¿cómo", placement.OverflowJson);
    }

    private static List<KeyValuePair<string, string>> Pairs(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new KeyValuePair<string, string>($"extra{i:000}", $"v{i}"))
            .ToList();

    private static TableEntity ToEntity(ContactAttributePlacement placement)
    {
        var entity = new TableEntity("tenant", "row");
        foreach (var column in placement.Columns)
        {
            entity[column.Key] = column.Value;
        }

        if (placement.OverflowJson is not null)
        {
            entity[ContactAttributeStorage.OverflowPropertyName] = placement.OverflowJson;
        }

        return entity;
    }

    private static Dictionary<string, string> ParseOverflow(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;
}
