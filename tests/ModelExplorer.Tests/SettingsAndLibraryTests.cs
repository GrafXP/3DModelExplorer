using ModelExplorer.Core;
using ModelExplorer.Core.Library;

namespace ModelExplorer.Tests;

public class SettingsTests
{
    [Fact]
    public void WhatIsSavedIsLoadedBack()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", "settings.json");
        var settings = new Settings
        {
            WindowX = -1200,
            WindowWidth = 1600,
            Maximized = true,
            ShowLibrary = false,
            GridView = false,
            ThumbnailSize = 200,
            SyncCameras = false,
            CompareMode = "Overlay",
            Tolerance = 0.25f,
            BuildPlate = "Prusa MINI",
            ModelColor = "Coral",
            AmbientIntensity = 0.7f,
            RotateLight = true,
        };

        settings.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(-1200, loaded.WindowX);
        Assert.Equal(1600, loaded.WindowWidth);
        Assert.True(loaded.Maximized);
        Assert.False(loaded.ShowLibrary);
        Assert.False(loaded.GridView);
        Assert.Equal(200, loaded.ThumbnailSize);
        Assert.False(loaded.SyncCameras);
        Assert.Equal("Overlay", loaded.CompareMode);
        Assert.Equal(0.25f, loaded.Tolerance);
        Assert.Equal("Prusa MINI", loaded.BuildPlate);
        Assert.Equal("Coral", loaded.ModelColor);
        Assert.Equal(0.7f, loaded.AmbientIntensity);
        Assert.True(loaded.RotateLight);
    }

    [Fact]
    public void AMissingOrDamagedFileGivesTheDefaults()
    {
        using var directory = new TempDirectory();
        Assert.True(Settings.Load(Path.Combine(directory.Path, "missing.json")).SyncCameras);

        // What was read before the damage stands; the rest keeps its defaults.
        var damaged = directory.Write("damaged.json", """{ "ThumbnailSize": 96, "GridView": "yes", "SyncCameras": fal""");
        var loaded = Settings.Load(damaged);
        Assert.Equal(96, loaded.ThumbnailSize);
        Assert.True(loaded.GridView);
        Assert.True(loaded.SyncCameras);
    }
}

public class ModelLibraryTests
{
    [Fact]
    public void AnAddedFolderIsScannedSearchedAndFiltered() => UiThread.Run(async () =>
    {
        using var data = new TempDirectory();
        using var models = new TempDirectory();
        IndexingFixtures.CreateSampleTree(models.Path);
        using var library = new ModelLibrary(IndexingFixtures.Extensions, Path.Combine(data.Path, "index.db"));

        await library.InitializeAsync();
        Assert.True(library.IsReady);
        Assert.False(library.HasRoots);

        await library.AddRootsAsync([models.Path]);
        await UiThread.Until(() => library.Models.Count == IndexingFixtures.SampleModelCount);
        Assert.Equal(IndexingFixtures.SampleModelCount, library.TotalModelCount);
        Assert.StartsWith("Indexed 4 files", library.ScanStatus);

        var version = library.ResultsVersion;
        library.SearchText = "pin";
        await UiThread.Until(() => library.ResultsVersion != version);
        Assert.Equal(["pin.stl"], library.Models.Select(m => m.Name));

        // The folder tree is the roots list; a subtree restricts the results.
        library.SearchText = string.Empty;
        var parts = Assert.Single(library.FolderTree).Children.Single(node => node.Name == "parts");
        library.SelectedFolder = parts;
        await UiThread.Until(() => library.Models.Count == 2);
        Assert.Equal(["bracket.stl", "pin.stl"], library.Models.Select(m => m.Name).Order());

        library.ClearFolderFilter();
        await UiThread.Until(() => library.Models.Count == IndexingFixtures.SampleModelCount);
    });

    [Fact]
    public void ReopeningTheIndexShowsTheLibraryWithoutAScan() => UiThread.Run(async () =>
    {
        using var data = new TempDirectory();
        using var models = new TempDirectory();
        IndexingFixtures.CreateSampleTree(models.Path);
        var index = Path.Combine(data.Path, "index.db");
        using (var first = new ModelLibrary(IndexingFixtures.Extensions, index))
        {
            await first.InitializeAsync();
            await first.AddRootsAsync([models.Path]);
        }

        using var second = new ModelLibrary(IndexingFixtures.Extensions, index);
        await second.InitializeAsync();
        await UiThread.Until(() => second.Models.Count == IndexingFixtures.SampleModelCount);
        Assert.Equal(string.Empty, second.ScanStatus);
        Assert.True(Assert.Single(second.FolderTree).IsExpanded);
    });
}
