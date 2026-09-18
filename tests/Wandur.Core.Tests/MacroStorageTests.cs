using Microsoft.Data.Sqlite;
using Wandur.Core.Scripting;
using Wandur.Core.Storage;

namespace Wandur.Core.Tests;

public sealed class MacroStorageTests
{
    [Fact]
    public void VersionOneDatabaseUpgradesAndPreservesScriptsAlongsideMacroDefinitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wandur-macro-db-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "wandur.db");
        try
        {
            var existing = new WorldScriptDefinition(Guid.NewGuid(), "Existing", "mud.echo('kept');", true);
            using (var raw = new SqliteConnection("Data Source=" + path))
            {
                raw.Open(); using var sql = raw.CreateCommand();
                sql.CommandText = """
                    CREATE TABLE worlds(id TEXT PRIMARY KEY NOT NULL);
                    INSERT INTO worlds(id) VALUES ('world');
                    CREATE TABLE endpoints(endpoint_key TEXT PRIMARY KEY NOT NULL,world_id TEXT NOT NULL REFERENCES worlds(id));
                    INSERT INTO endpoints(endpoint_key, world_id) VALUES ('mud.example:4000', 'world');
                    CREATE TABLE scripts(world_id TEXT NOT NULL REFERENCES worlds(id),id TEXT NOT NULL,name TEXT NOT NULL,source TEXT NOT NULL,enabled INTEGER NOT NULL,PRIMARY KEY(world_id,id));
                    INSERT INTO scripts VALUES ('world', $id, $name, $source, 1);
                    PRAGMA user_version=1;
                    """;
                sql.Parameters.AddWithValue("$id", existing.Id.ToString("D")); sql.Parameters.AddWithValue("$name", existing.Name); sql.Parameters.AddWithValue("$source", existing.Source);
                sql.ExecuteNonQuery();
            }
            var store = new SqliteWorldScriptLibraryStore(new ClientDatabase(path), Path.Combine(directory, "scripts"));
            Assert.Equal(existing, Assert.Single(store.Load("mud.example:4000:False")));
            var macro = new MacroDefinition(MacroKind.Trigger, "[Hungry]", "eat bread", IgnoreCase: true);
            var definition = new WorldScriptDefinition(Guid.NewGuid(), "Hunger", MacroCompiler.Compile(macro), Macro: macro);
            store.Upsert("mud.example:4000:False", definition);
            var reopened = new SqliteWorldScriptLibraryStore(new ClientDatabase(path), Path.Combine(directory, "scripts"));
            Assert.Equal(new[] { existing, definition }, reopened.Load("mud.example:4000:False"));
            Assert.Throws<ArgumentException>(() => reopened.Upsert("mud.example:4000:False", definition with { Source = "mud.send('wrong');" }));
            Assert.Equal(definition, reopened.Load("mud.example:4000:False")[1]);
            Assert.DoesNotContain(reopened.Load("other.example:4000:False"), s => s.Id == definition.Id);
            reopened.Delete("mud.example:4000:False", definition.Id);
            Assert.Equal(existing, Assert.Single(reopened.Load("mud.example:4000:False")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
