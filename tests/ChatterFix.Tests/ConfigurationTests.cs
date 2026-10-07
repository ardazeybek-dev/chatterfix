using ChatterFix.Core;
using ChatterFix.Core.Configuration;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Tests;

public class ConfigurationTests
{
    private static AppConfiguration WithProfiles(params FilterProfile[] profiles)
        => new() { Profiles = [.. profiles] };

    [Fact]
    public void TheProfileMatchingTheForegroundApplication_Wins()
    {
        var config = WithProfiles(
            new FilterProfile { Name = "Desktop", ProcessNames = [], ChatterThresholdMs = 40 },
            new FilterProfile { Name = "Game", ProcessNames = ["javaw"], ChatterThresholdMs = 12 });

        Assert.Equal("Game", config.ResolveProfile("javaw").Name);
    }

    [Fact]
    public void ProcessNames_AreMatchedRegardlessOfCase()
    {
        var config = WithProfiles(
            new FilterProfile { Name = "Desktop", ProcessNames = [] },
            new FilterProfile { Name = "Game", ProcessNames = ["JavaW"] });

        Assert.Equal("Game", config.ResolveProfile("javaw").Name);
        Assert.Equal("Game", config.ResolveProfile("JAVAW").Name);
    }

    [Fact]
    public void AnUnknownApplication_FallsBackToTheProfileWithNoNames()
    {
        var config = WithProfiles(
            new FilterProfile { Name = "Game", ProcessNames = ["javaw"] },
            new FilterProfile { Name = "Desktop", ProcessNames = [] });

        Assert.Equal("Desktop", config.ResolveProfile("chrome").Name);
    }

    [Fact]
    public void AnUnknownForegroundProcess_StillResolvesToAProfile()
    {
        // The watcher returns null for elevated windows, and filtering must carry on.
        var config = new AppConfiguration();

        var profile = config.ResolveProfile(null);

        Assert.NotNull(profile);
        Assert.True(profile.IsFallback);
    }

    [Fact]
    public void AConfigWithoutAFallbackProfile_GetsOne()
    {
        // Otherwise every application outside the named ones would go unfiltered.
        var config = WithProfiles(new FilterProfile { Name = "Game", ProcessNames = ["javaw"] });

        config.Sanitised();

        Assert.Contains(config.Profiles, p => p.IsFallback);
    }

    [Fact]
    public void HandEditedThresholds_AreClampedIntoASafeRange()
    {
        // A 300 ms threshold would swallow ordinary double clicks.
        var config = WithProfiles(new FilterProfile
        {
            Name = "Broken",
            ProcessNames = [],
            ChatterThresholdMs = 300,
            ReleaseDelayMs = 5000,
        });

        config.Sanitised();

        Assert.InRange(config.Profiles[0].ChatterThresholdMs, 1, 100);
        Assert.InRange(config.Profiles[0].ReleaseDelayMs, 0, 50);
    }

    [Fact]
    public void AnEmptyProfileList_IsReplacedWithTheDefaults()
    {
        var config = new AppConfiguration { Profiles = [] };

        config.Sanitised();

        Assert.NotEmpty(config.Profiles);
        Assert.Contains(config.Profiles, p => p.IsFallback);
    }

    [Fact]
    public void AProfile_TurnsIntoFilterSettingsForEveryButton()
    {
        var profile = new FilterProfile
        {
            ChatterThresholdMs = 18,
            ReleaseDelayMs = 9,
            ButtonsEnabled = [true, false, true, true, true],
        };

        var settings = profile.ToFilterSettings(enabled: true);

        Assert.Equal(FilterMode.Protect, settings.Mode);
        Assert.True(settings.Enabled);
        Assert.Equal(18, settings.Buttons[(int)MouseButton.Left].ChatterThresholdMs);
        Assert.Equal(9, settings.Buttons[(int)MouseButton.Left].ReleaseDelayMs);
        Assert.False(settings.Buttons[(int)MouseButton.Right].Enabled);
    }

    [Fact]
    public void SavingAndLoading_KeepsEverything()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chatterfix-test-{Guid.NewGuid():N}.json");

        try
        {
            var original = new AppConfiguration
            {
                Enabled = false,
                NotifyOnFirstBlock = false,
                Profiles =
                [
                    new FilterProfile { Name = "Desktop", ProcessNames = [], ChatterThresholdMs = 40 },
                    new FilterProfile { Name = "Game", ProcessNames = ["javaw", "Minecraft"], ChatterThresholdMs = 12 },
                ],
            };

            original.Save(path);
            var loaded = AppConfiguration.Load(path);

            Assert.False(loaded.Enabled);
            Assert.False(loaded.NotifyOnFirstBlock);
            Assert.Equal(2, loaded.Profiles.Count);
            Assert.Equal(12, loaded.ResolveProfile("Minecraft").ChatterThresholdMs);
            Assert.Equal(40, loaded.ResolveProfile("explorer").ChatterThresholdMs);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableConfig_FallsBackToDefaultsInsteadOfFailing()
    {
        // A broken file must never stop the filter from protecting the mouse.
        string path = Path.Combine(Path.GetTempPath(), $"chatterfix-test-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "{ this is not valid json");

            var config = AppConfiguration.Load(path);

            Assert.True(config.Enabled);
            Assert.NotEmpty(config.Profiles);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AMissingConfig_LoadsTheDefaults()
    {
        var config = AppConfiguration.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.True(config.Enabled);
        Assert.Contains(config.Profiles, p => p.IsFallback);
    }

    [Fact]
    public void TheShippedDefaults_ProtectFastClickingFromBeingFiltered()
    {
        // Deliberate fast clicking reaches about 30 per second, leaving roughly 15 ms
        // between a release and the next press. The gaming threshold must stay below that.
        var config = new AppConfiguration();

        var game = config.ResolveProfile("javaw");
        var desktop = config.ResolveProfile("explorer");

        Assert.True(game.ChatterThresholdMs < 15, "the fast-clicking threshold would eat real clicks");
        Assert.True(desktop.ChatterThresholdMs > game.ChatterThresholdMs);
    }

    [Fact]
    public void AnUnversionedConfig_GetsADesktopReleaseWindowSpanningTheThreshold()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v1-{Guid.NewGuid():N}.json");
        try
        {
            WithProfiles(
                    new FilterProfile { Name = "Desktop", ProcessNames = [], ChatterThresholdMs = 35, ReleaseDelayMs = 12 },
                    new FilterProfile { Name = "Game", ProcessNames = ["javaw"], ChatterThresholdMs = 12, ReleaseDelayMs = 8 })
                .Save(path);

            var loaded = AppConfiguration.Load(path);

            Assert.Equal(35, loaded.ResolveProfile("explorer").ReleaseDelayMs);
            Assert.Equal(8, loaded.ResolveProfile("javaw").ReleaseDelayMs);
            Assert.Equal(AppConfiguration.CurrentVersion, loaded.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACurrentConfig_KeepsAShorterReleaseWindowItWasGiven()
    {
        string path = Path.Combine(Path.GetTempPath(), $"v2-{Guid.NewGuid():N}.json");
        try
        {
            var config = WithProfiles(
                new FilterProfile { Name = "Desktop", ProcessNames = [], ChatterThresholdMs = 35, ReleaseDelayMs = 12 });
            config.Version = AppConfiguration.CurrentVersion;
            config.Save(path);

            Assert.Equal(12, AppConfiguration.Load(path).ResolveProfile("explorer").ReleaseDelayMs);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
