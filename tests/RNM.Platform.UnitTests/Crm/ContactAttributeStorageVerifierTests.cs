using Azure.Data.Tables;
using RNM.Platform.Infrastructure.Crm;
using Xunit;

namespace RNM.Platform.UnitTests.Crm;

public sealed class ContactAttributeStorageVerifierTests
{
    [Fact]
    public void Place_EscapeHeavyOverflowValues_NeverExceedJsonCap()
    {
        var limit = ContactAttributeStorage.UnknownAttributeColumnLimit;
        var attributes = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < limit + 40; i++)
        {
            // quotes, backslashes, control chars and emoji all expand when JSON-encoded.
            attributes.Add(new($"k{i}", new string('"', 400) + "\n\\😀"));
        }

        var placement = ContactAttributeStorage.Place(null, attributes);

        Assert.NotNull(placement.OverflowJson);
        Assert.True(placement.OverflowJson!.Length <= ContactAttributeStorage.MaxOverflowJsonLength);
        Assert.True(placement.DroppedAttributeCount > 0);
        var entity = new TableEntity("t", "r") { [ContactAttributeStorage.OverflowPropertyName] = placement.OverflowJson };
        var all = ContactAttributeStorage.ReadAll(entity);
        Assert.Equal(placement.OverflowAttributeCount, all.Count);
    }

    [Fact]
    public void Place_ReplayingSameAttributesOnOverflowedRow_DoesNotRewriteOverflowOrCountNew()
    {
        var limit = ContactAttributeStorage.UnknownAttributeColumnLimit;
        var attributes = Enumerable.Range(0, limit + 5)
            .Select(i => new KeyValuePair<string, string>($"k{i}", "v"))
            .ToList();
        var first = ContactAttributeStorage.Place(null, attributes);
        var entity = new TableEntity("t", "r");
        foreach (var column in first.Columns)
        {
            entity[column.Key] = column.Value;
        }

        entity[ContactAttributeStorage.OverflowPropertyName] = first.OverflowJson;

        var second = ContactAttributeStorage.Place(entity, attributes);

        Assert.Equal(first.OverflowJson, second.OverflowJson);
        Assert.Equal(0, second.NewOverflowAttributeCount);
        Assert.Equal(5, second.OverflowAttributeCount);
        Assert.Equal(limit, second.Columns.Count);
    }
}
