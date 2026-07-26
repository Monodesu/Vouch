using Vouch.Core.Storage;

namespace Vouch.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void SaveTo_ThenLoadFrom_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sda_settings_{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings
            {
                Theme = "Light",
                MinimizeToTray = false,
                StartMinimized = true,
                PeriodicChecking = false,
                PeriodicIntervalSeconds = 120,
                ClipboardClearSeconds = 0,
                ApiKey = "ABC123",
            }.SaveTo(path);

            var s = AppSettings.LoadFrom(path);
            Assert.Equal("Light", s.Theme);
            Assert.False(s.MinimizeToTray);
            Assert.True(s.StartMinimized);
            Assert.False(s.PeriodicChecking);
            Assert.Equal(120, s.PeriodicIntervalSeconds);
            Assert.Equal(0, s.ClipboardClearSeconds);
            Assert.Equal("ABC123", s.ApiKey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFrom_MissingOrCorrupt_ReturnsDefaults()
    {
        var missing = AppSettings.LoadFrom(Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json"));
        Assert.Equal("Dark", missing.Theme);
        Assert.True(missing.MinimizeToTray);

        var path = Path.Combine(Path.GetTempPath(), $"sda_settings_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json {{{");
            Assert.Equal("Dark", AppSettings.LoadFrom(path).Theme);
        }
        finally { File.Delete(path); }
    }
}
