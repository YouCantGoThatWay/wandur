using Wandur.Core.Scripting;

namespace Wandur.Core.Tests;

public sealed class WorldScriptLibraryStoreTests
{
    [Fact]
    public void CollectionLimitAndCorruptFilesNeverSilentlyReplaceSavedScripts()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var store = new WorldScriptLibraryStore(directory, new WorldScriptStore(directory));
            store.Load("world");
            for (var i = 1; i < WorldScriptLibraryStore.MaximumScripts; i++)
                store.Upsert("world", new(Guid.NewGuid(), "Script " + i, ""));
            Assert.Throws<ArgumentException>(() => store.Upsert("world", new(Guid.NewGuid(), "One too many", "")));
            Assert.Equal(WorldScriptLibraryStore.MaximumScripts, store.Load("world").Count);
            var file = Directory.GetFiles(directory, "*.scripts.json").Single();
            File.WriteAllText(file, "invalid json");
            Assert.Throws<IOException>(() => store.Load("world"));
            Assert.Throws<IOException>(() => store.Upsert("world", new(Guid.NewGuid(), "Must not replace", "")));
            Assert.Equal("invalid json", File.ReadAllText(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void FragmentedPrivateGmcpRetainsProvenance()
    {
        var parser = new Wandur.Core.Protocol.TelnetParser();
        parser.Feed(new byte[] { 255, 251, 201, 255, 251, 1, 255, 250, 201 });
        Assert.Empty(parser.Feed(System.Text.Encoding.UTF8.GetBytes("Char.Secret \"hidden\"")).DataMessages);
        var packet = parser.Feed(new byte[] { 255, 240, 255, 252, 1 });
        Assert.True(Assert.Single(packet.DataMessages).MayContainPrivateText);
        var next = parser.Feed(new byte[] { 255, 250, 201, (byte)'x', 255, 240 });
        Assert.False(Assert.Single(next.DataMessages).MayContainPrivateText);
    }

    [Fact]
    public void MigrationPreservesLegacySourceAndDisablesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var legacy = new WorldScriptStore(directory);
            legacy.Save("world", "mud.echo('legacy');");
            var store = new WorldScriptLibraryStore(directory, legacy);
            var script = Assert.Single(store.Load("world"));
            Assert.False(script.Enabled);
            Assert.Equal("mud.echo('legacy');", script.Source);
            Assert.Equal(script.Id, Assert.Single(new WorldScriptLibraryStore(directory, legacy).Load("world")).Id);
            Assert.Equal(script.Source, legacy.Load("world"));
            store.Delete("world", script.Id);
            Assert.Empty(store.Load("world"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PerScriptWritesMergeAcrossStoreInstancesAndValidateBeforeReplacing()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var first = new WorldScriptLibraryStore(directory, new WorldScriptStore(directory));
            var second = new WorldScriptLibraryStore(directory, new WorldScriptStore(directory));
            var original = Assert.Single(first.Load("../../world"));
            var extra = new WorldScriptDefinition(Guid.NewGuid(), "Extra", "hello", true);
            second.Upsert("../../world", extra);
            first.Upsert("../../world", original with { Source = "changed" });
            var scripts = second.Load("../../world");
            Assert.Equal(2, scripts.Count);
            Assert.Contains(scripts, s => s == extra);
            Assert.Contains(scripts, s => s.Id == original.Id && s.Source == "changed");
            Assert.Throws<ArgumentException>(() => first.Upsert("../../world", extra with { Source = new string('x', WorldScriptStore.MaximumBytes + 1) }));
            Assert.Equal(extra, second.Load("../../world").Single(s => s.Id == extra.Id));
            Assert.DoesNotContain(Directory.GetFiles(directory), p => p.EndsWith(".tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
