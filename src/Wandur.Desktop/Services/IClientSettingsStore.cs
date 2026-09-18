using Wandur.Core.Settings;

namespace Wandur.Desktop.Services;

public interface IClientSettingsStore
{
    ClientSettings Settings { get; }
    void SaveSettings(ClientSettings settings);
}
