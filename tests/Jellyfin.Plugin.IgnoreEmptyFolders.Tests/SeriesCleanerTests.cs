using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tests;

public sealed class SeriesCleanerTests
{
    [Fact]
    public async Task EmptySeriesIsTaggedInHideMode()
    {
        var series = NewSeries("Empty series");
        var library = CreateLibrary([series], [], []);

        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        var changedCount = await CreateCleaner(library).CleanAsync(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, changedCount);
        Assert.Contains(
            "plugin-ignore-empty-items-hidden",
            series.Tags
        );
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 0
        );
        library.Verify(
            m => m.UpdateItemAsync(
                series,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(series, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task
        EmptySeriesWithOnlyVirtualAndMissingEpisodesIsDeleted()
    {
        var series = NewSeries("Empty series");
        var virtualEpisode = NewEpisode(
            series, null, "Virtual episode", isVirtual: true
        );
        var missingEpisode = NewEpisode(
            series, null, "Missing episode",
            isVirtual: true, isMissing: true
        );
        var library = CreateLibrary(
            [series], [virtualEpisode, missingEpisode], []
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 0
        );
        library.Verify(
            m => m.DeleteItem(
                series,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task SeriesWithRealEpisodeIsPreserved()
    {
        var emptySeries = NewSeries("Empty series");
        var populatedSeries = NewSeries("Populated series");
        var virtualEpisode = NewEpisode(
            emptySeries, null, "Virtual episode", isVirtual: true
        );
        var missingEpisode = NewEpisode(
            emptySeries, null, "Missing episode",
            isVirtual: true, isMissing: true
        );
        var realEpisode = NewEpisode(
            populatedSeries, null, "Real episode",
            isVirtual: false
        );
        var library = CreateLibrary(
            [emptySeries, populatedSeries],
            [virtualEpisode, missingEpisode, realEpisode],
            []);
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 0
        );
        library.Verify(
            m => m.DeleteItem(
                emptySeries,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                populatedSeries, It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task
        SeriesWithEmptyEpisodeSeriesIdAndValidParentChainIsPreserved()
    {
        var series = NewSeries("Series");
        var season = NewSeason(
            series, "Season", "/library/series/season-1"
        );
        var episode = NewEpisode(
            series, season, "Episode", isVirtual: false
        );
        episode.SeriesId = Guid.Empty;
        var library = CreateLibrary([series], [episode], [season]);
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        AssertBulkGraphLookupOnce(library);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 0
        );
        library.Verify(
            m => m.DeleteItem(series, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task
        EpisodeSeriesIdConflictUsesPhysicalParentSeries()
    {
        var physicalSeries = NewSeries("Physical series");
        var conflictingSeries = NewSeries("Conflicting series");
        var season = NewSeason(
            physicalSeries, "Season",
            "/library/physical-series/season-1"
        );
        var episode = NewEpisode(
            physicalSeries, season, "Episode", isVirtual: false
        );
        episode.SeriesId = conflictingSeries.Id;
        var library = CreateLibrary(
            [physicalSeries, conflictingSeries], [episode], [season]
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        AssertBulkGraphLookupOnce(library);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 0
        );
        library.Verify(
            m => m.DeleteItem(
                physicalSeries, It.IsAny<DeleteOptions>()
            ),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                conflictingSeries,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)
            ),
            Times.Once);
    }

    [Fact]
    public async Task CleansOnlyEmptyFilesystemSeasons()
    {
        var series = NewSeries("Series");
        var emptyFilesystemSeason = NewSeason(
            series, "Empty filesystem season",
            "/library/series/season-1"
        );
        var populatedFilesystemSeason = NewSeason(
            series, "Populated filesystem season",
            "/library/series/season-2"
        );
        var virtualFilesystemSeason = NewSeason(
            series, "Virtual-only filesystem season",
            "/library/series/season-3"
        );
        var remoteSeason = NewSeason(
            series, "Remote season",
            "https://example.test/season-4"
        );
        remoteSeason.SeriesId = Guid.Empty;
        var virtualEpisode = NewEpisode(
            series, virtualFilesystemSeason,
            "Virtual episode", isVirtual: true
        );
        var missingEpisode = NewEpisode(
            series, remoteSeason, "Missing episode",
            isVirtual: true, isMissing: true
        );
        var realEpisode = NewEpisode(
            series, populatedFilesystemSeason,
            "Real episode", isVirtual: false
        );
        var library = CreateLibrary(
            [series],
            [virtualEpisode, missingEpisode, realEpisode],
            [
                emptyFilesystemSeason,
                populatedFilesystemSeason,
                virtualFilesystemSeason,
                remoteSeason
            ]);
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(2, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 1
        );
        library.Verify(
            m => m.DeleteItem(
                emptyFilesystemSeason,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                virtualFilesystemSeason,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(remoteSeason, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                populatedFilesystemSeason, It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    private static PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration
        {
            DeleteEmptyShows = true,
            DeleteEmptySeasons = true,
            LogDeletions = false
        };
    }

    private static Mock<ILibraryManager> CreateLibrary(
        IReadOnlyCollection<Series> series,
        IReadOnlyCollection<Episode> episodes,
        IReadOnlyCollection<Season> seasons)
    {
        var fileSystem = new Mock<MediaBrowser.Model.IO.IFileSystem>();
        fileSystem
            .Setup(m => m.IsPathFile(It.IsAny<string>()))
            .Returns(
                (string path) =>
                    !path.Contains("://", StringComparison.Ordinal)
            );
        BaseItem.FileSystem = fileSystem.Object;

        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.IncludeItemTypes.Length == 0)
                {
                    Assert.Null(query.IsVirtualItem);
                    Assert.Null(query.IsMissing);
                    return series.Cast<BaseItem>()
                        .Concat(episodes)
                        .Concat(seasons)
                        .ToList();
                }

                if (
                    query.IncludeItemTypes.Contains(
                        BaseItemKind.Series
                    )
                )
                {
                    return series.Cast<BaseItem>().ToList();
                }

                if (
                    query.IncludeItemTypes.Contains(
                        BaseItemKind.Episode
                    )
                )
                {
                    Assert.Equal(false, query.IsVirtualItem);
                    Assert.Equal(false, query.IsMissing);
                    return episodes
                        .Where(e => !e.IsVirtualItem)
                        .Cast<BaseItem>()
                        .ToList();
                }

                if (
                    query.IncludeItemTypes.Contains(
                        BaseItemKind.Season
                    )
                )
                {
                    return seasons.Cast<BaseItem>().ToList();
                }

                throw new InvalidOperationException(
                    "Unexpected GetItemList query " +
                    "in SeriesCleanerTests."
                );
            });
        library
            .Setup(m => m.GetCount(
                It.IsAny<InternalItemsQuery>()
            ))
            .Returns((InternalItemsQuery _) =>
                throw new InvalidOperationException(
                    "SeriesCleaner must not call GetCount."
                )
            );
        library
            .Setup(m => m.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) =>
                series.Cast<BaseItem>()
                    .Concat(episodes)
                    .Concat(seasons)
                    .FirstOrDefault(item => item.Id == id));
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return library;
    }

    private static Series NewSeries(string name)
    {
        return new Series
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }

    private static Season NewSeason(
        Series series, string name, string path
    )
    {
        return new Season
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentId = series.Id,
            SeriesId = series.Id,
            Path = path
        };
    }

    private static Episode NewEpisode(
        Series series,
        Season? season,
        string name,
        bool isVirtual,
        bool isMissing = false)
    {
        return new Episode
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentId = season?.Id ?? series.Id,
            SeasonId = season?.Id ?? Guid.Empty,
            SeriesId = series.Id,
            IsVirtualItem = isVirtual,
            Path = isMissing
                ? null
                : isVirtual
                    ? "https://example.test/virtual-episode"
                    : "/library/series/episode.mkv"
        };
    }

    private static void AssertNoScalarCounts(
        Mock<ILibraryManager> library
    )
    {
        library.Verify(
            m => m.GetCount(
                It.IsAny<InternalItemsQuery>()
            ),
            Times.Never
        );
    }

    private static void AssertBulkGraphLookupOnce(
        Mock<ILibraryManager> library
    )
    {
        library.Verify(
            m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 0
            )),
            Times.Once);
    }

    private static void AssertBulkLookupsOnce(
        Mock<ILibraryManager> library,
        int episodeLookup,
        int seasonLookup)
    {
        library.Verify(
            m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Episode)),
            Times.Exactly(episodeLookup));
        library.Verify(
            m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Season)),
            Times.Exactly(seasonLookup));
    }

    private static SeriesCleaner CreateCleaner(
        Mock<ILibraryManager> library
    )
    {
        return new SeriesCleaner(
            library.Object, NullLogger<SeriesCleaner>.Instance
        );
    }
}
