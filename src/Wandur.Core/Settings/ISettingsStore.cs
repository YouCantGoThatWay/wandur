namespace Wandur.Core.Settings;

public interface ISettingsStore
{
    string FilePath { get; }
    SettingsLoadResult Load();
    void Save(ClientSettings settings);
}
