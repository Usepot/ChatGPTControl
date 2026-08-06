using PortableCodex.Native.Models;
using PortableCodex.Native.Services;

namespace PortableCodex.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), "portable-codex-tests", Guid.NewGuid().ToString("N"), "state.json");

    [Fact]
    public void SettingsRoundTripUsesPortableStatePathAndPreservesSkillConfiguration()
    {
        var credentials = new CredentialService();
        var store = new SettingsStore(credentials, _statePath);
        var state = credentials.CreateDefaultState();
        state.Settings.TrustedWorkspaces.Add(Path.GetTempPath());
        state.Settings.SkillRoots.Add(Path.Combine(Path.GetTempPath(), "skills"));
        state.Settings.ImportCodexCliSkills = true;
        state.Settings.RequireApprovalForWrites = true;

        store.Save(state);
        var loaded = store.Load();

        Assert.Contains(Path.GetTempPath(), loaded.Settings.TrustedWorkspaces);
        Assert.Contains(Path.Combine(Path.GetTempPath(), "skills"), loaded.Settings.SkillRoots);
        Assert.True(loaded.Settings.ImportCodexCliSkills);
        Assert.True(loaded.Settings.RequireApprovalForWrites);
        Assert.NotEmpty(loaded.Settings.DeviceId);
        Assert.NotEmpty(loaded.Settings.DeviceToken);
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
