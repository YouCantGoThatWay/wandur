using System.Text;
using Wandur.Core.Protocol;

namespace Wandur.Core.Tests;

public sealed class ProtocolSchemaInventoryTests
{
    private static void Observe(ProtocolSchemaInventory inventory, string text, byte option = 201) =>
        inventory.Observe(option, ProtocolDiagnosticFormatter.Format(option, Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void FingerprintIgnoresValuesOrderingAndPartialUpdates()
    {
        var first = new ProtocolSchemaInventory(); var second = new ProtocolSchemaInventory();
        Observe(first, "Char.Vitals {\"hp\":100,\"maxhp\":120}");
        Observe(second, "Char.Vitals {\"maxhp\":900,\"hp\":10}");
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        var hash = first.Fingerprint;
        Observe(first, "Char.Vitals {\"hp\":2}");
        Assert.Equal(hash, first.Fingerprint);
        Assert.DoesNotContain("120", first.FieldsText);
        Observe(first, "Char.Vitals {\"hp\":\"2\"}");
        Assert.NotEqual(hash, first.Fingerprint);
        Assert.Contains("string", first.Snapshot);
    }

    [Fact]
    public void ArrayLengthDoesNotChangeShapeAndPathsCannotCollide()
    {
        var inventory = new ProtocolSchemaInventory();
        Observe(inventory, "Char.Items.List {\"items\":[{\"id\":1}],\"a/b\":true,\"a\":{\"b\":false},\"*\":0}");
        var hash = inventory.Fingerprint;
        Observe(inventory, "Char.Items.List {\"items\":[{\"id\":2},{\"id\":3}]}");
        Observe(inventory, "Char.Items.List {\"items\":[]}");
        Assert.Equal(hash, inventory.Fingerprint);
        Assert.Contains("/items/*/id", inventory.Snapshot);
        Assert.Contains("/a~1b", inventory.Snapshot);
        Assert.Contains("/a/b", inventory.Snapshot);
        Assert.Contains("/~2", inventory.Snapshot);
    }

    [Fact]
    public void MsdpFieldsAccumulateAcrossMessagesWithoutStoringValues()
    {
        var inventory = new ProtocolSchemaInventory();
        Observe(inventory, "\u0001HEALTH\u000285\u0001HEALTH_MAX\u0002100", 69);
        var hash = inventory.Fingerprint;
        Observe(inventory, "\u0001HEALTH\u000210", 69);
        Assert.Equal(hash, inventory.Fingerprint);
        Assert.Contains("/HEALTH_MAX", inventory.Snapshot);
        Assert.DoesNotContain("85", inventory.FieldsText);
        inventory.Clear(); Assert.Equal(0, inventory.FieldCount);
    }

    [Fact]
    public void MalformedDataCannotPolluteInventoryAndUniqueFieldsAreBounded()
    {
        var inventory = new ProtocolSchemaInventory();
        Observe(inventory, "Broken {oops");
        inventory.Observe(201, new("Clipped", "{}", false, true));
        Observe(inventory, new string('A', 129) + " {}");
        Assert.Equal(0, inventory.FieldCount);
        for (int i = 0; i < ProtocolSchemaInventory.MaximumFields + 100; i++) Observe(inventory, $"Custom {{\"field{i}\":1}}");
        Assert.Equal(ProtocolSchemaInventory.MaximumFields, inventory.FieldCount);
        Assert.True(inventory.Limited);
        Assert.Contains("\"limited\": true", inventory.Snapshot);
        inventory.Clear(); Assert.False(inventory.Limited);
    }
}
