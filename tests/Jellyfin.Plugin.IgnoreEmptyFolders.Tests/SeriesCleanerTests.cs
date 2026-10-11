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

[Collection("BaseItem.LibraryManager")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySeriesInHideModeAlsoTagsItsFilesystemSeasons(
        bool alreadyTagged)
    {
        const string hiddenTag = "plugin-ignore-empty-items-hidden";
        var series = NewSeries("Empty series");
        if (alreadyTagged)
        {
            series.Tags = [hiddenTag];
        }

        var firstSeason = NewSeason(
            series, "First", "/library/empty-series/season-1"
        );
        var secondSeason = NewSeason(
            series, "Second", "/library/empty-series/season-2"
        );
        var library = CreateLibrary(
            [series], [], [firstSeason, secondSeason]
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var cleaner = CreateCleaner(library);
        List<double> reports = [];
        var progress = new Mock<IProgress<double>>();
        progress.Setup(p => p.Report(It.IsAny<double>()))
            .Callback<double>(reports.Add);

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        try
        {
            var firstChangedCount = await cleaner.CleanAsync(
                config, progress.Object, CancellationToken.None
            );

            Assert.Equal(alreadyTagged ? 2 : 3, firstChangedCount);
            Assert.Contains(hiddenTag, series.Tags);
            Assert.Contains(hiddenTag, firstSeason.Tags);
            Assert.Contains(hiddenTag, secondSeason.Tags);
            Assert.Collection(
                reports, value => Assert.Equal(100.0, value));

            reports.Clear();
            var secondChangedCount = await cleaner.CleanAsync(
                config, progress.Object, CancellationToken.None
            );

            Assert.Equal(0, secondChangedCount);
            Assert.Collection(
                reports, value => Assert.Equal(100.0, value));
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptySeriesAdvancesProgressBeforeNextSeries(
        bool hideInsteadOfDelete)
    {
        var firstEmpty = NewSeries("First empty");
        var populated = NewSeries("Populated");
        var lastEmpty = NewSeries("Last empty");
        var episode = NewEpisode(
            populated, null, "Episode", isVirtual: false
        );
        var library = CreateLibrary(
            [firstEmpty, populated, lastEmpty], [episode], []
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = hideInsteadOfDelete;
        List<double> reports = [];
        var progress = new Mock<IProgress<double>>();
        progress.Setup(p => p.Report(It.IsAny<double>()))
            .Callback<double>(reports.Add);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, progress.Object, CancellationToken.None
        );

        Assert.Equal(2, changedCount);
        Assert.Collection(
            reports,
            value => Assert.Equal(100.0 / 3, value, 10),
            value => Assert.Equal(200.0 / 3, value, 10),
            value => Assert.Equal(100.0, value));
        if (hideInsteadOfDelete)
        {
            Assert.Contains(
                "plugin-ignore-empty-items-hidden", firstEmpty.Tags);
            Assert.Contains(
                "plugin-ignore-empty-items-hidden", lastEmpty.Tags);
            Assert.DoesNotContain(
                "plugin-ignore-empty-items-hidden", populated.Tags);
        }
        else
        {
            library.Verify(
                m => m.DeleteItem(
                    firstEmpty, It.IsAny<DeleteOptions>()),
                Times.Once);
            library.Verify(
                m => m.DeleteItem(
                    lastEmpty, It.IsAny<DeleteOptions>()),
                Times.Once);
            library.Verify(
                m => m.DeleteItem(
                    populated, It.IsAny<DeleteOptions>()),
                Times.Never);
        }
    }

    [Fact]
    public async Task NonSeriesEntryAlsoAdvancesProgress()
    {
        var empty = NewSeries("Empty");
        var library = CreateLibrary([empty], [], []);
        library.Setup(m => m.GetItemList(
            It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 &&
                q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([new Season(), empty]);
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        List<double> reports = [];
        var progress = new Mock<IProgress<double>>();
        progress.Setup(p => p.Report(It.IsAny<double>()))
            .Callback<double>(reports.Add);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, progress.Object, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        Assert.Collection(
            reports,
            value => Assert.Equal(50.0, value),
            value => Assert.Equal(100.0, value));
        Assert.Contains(
            "plugin-ignore-empty-items-hidden", empty.Tags);
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
            isVirtual: false, isMissing: true
        );
        Assert.False(missingEpisode.IsVirtualItem);
        Assert.Null(missingEpisode.Path);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathlessChannelEpisodeProtectsItsAncestors(
        bool cleanShows)
    {
        var channelSeries = NewSeries("Channel series");
        var localSeries = NewSeries("Local series");
        var channelSeason = NewSeason(
            channelSeries, "Channel season", "/library/channel/season"
        );
        var localSeason = NewSeason(
            localSeries, "Local season", "/library/local/season"
        );
        var channelEpisode = NewEpisode(
            channelSeries, channelSeason, "Channel episode",
            isVirtual: false, isMissing: true
        );
        channelEpisode.ChannelId = Guid.NewGuid();
        var localEpisode = NewEpisode(
            localSeries, localSeason, "Local episode",
            isVirtual: false, isMissing: true
        );
        var library = CreateLibrary(
            [channelSeries, localSeries],
            [channelEpisode, localEpisode],
            [channelSeason, localSeason]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = cleanShows;
        config.DeleteEmptySeasons = !cleanShows;
        config.HideInsteadOfDelete = false;
        Assert.False(channelEpisode.IsVirtualItem);
        Assert.Null(channelEpisode.Path);
        Assert.False(localEpisode.IsVirtualItem);
        Assert.Null(localEpisode.Path);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        var protectedItem = cleanShows
            ? (BaseItem)channelSeries : channelSeason;
        var emptyItem = cleanShows
            ? (BaseItem)localSeries : localSeason;
        library.Verify(
            m => m.DeleteItem(
                protectedItem, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                emptyItem,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task EpisodesWithSamePresentationKeyKeepBothSeries()
    {
        var first = NewSeries("First series");
        var second = NewSeries("Second series");
        var firstEpisode = NewEpisode(
            first, null, "First episode", isVirtual: false
        );
        var secondEpisode = NewEpisode(
            second, null, "Second episode", isVirtual: false
        );
        firstEpisode.PresentationUniqueKey = "shared-episode";
        secondEpisode.PresentationUniqueKey = "shared-episode";
        var library = CreateLibrary(
            [first, second], [firstEpisode, secondEpisode], [],
            simulateGrouping: true
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task AlternateEpisodeKeepsPhysicalSeriesAndSeason()
    {
        var series = NewSeries("Physical series");
        var season = NewSeason(
            series, "Season", "/library/physical-series/season"
        );
        var alternate = NewEpisode(
            series, season, "Alternate", isVirtual: false
        );
        alternate.SetPrimaryVersionId(Guid.NewGuid());
        alternate.SeriesId = Guid.NewGuid();
        alternate.SeasonId = Guid.NewGuid();
        var library = CreateLibrary(
            [series], [alternate], [season],
            simulateOwnedExclusion: true
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
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

    [Fact]
    public async Task PathlessEpisodeDoesNotPreserveFilesystemSeason()
    {
        var series = NewSeries("Series");
        var empty = NewSeason(
            series, "Pathless", "/library/series/season-1"
        );
        var populated = NewSeason(
            series, "Populated", "/library/series/season-2"
        );
        var missingEpisode = NewEpisode(
            series, empty, "Missing episode",
            isVirtual: false, isMissing: true
        );
        var realEpisode = NewEpisode(
            series, populated, "Real episode", isVirtual: false
        );
        Assert.False(missingEpisode.IsVirtualItem);
        Assert.Null(missingEpisode.Path);
        var library = CreateLibrary(
            [series], [missingEpisode, realEpisode],
            [empty, populated]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                empty,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                populated, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task PhysicalEpisodeRestoresSeriesDespiteStaleId()
    {
        var physicalSeries = NewSeries("Physical series");
        physicalSeries.Tags =
            ["plugin-ignore-empty-items-hidden"];
        var metadataSeries = NewSeries("Metadata series");
        var episode = NewEpisode(
            physicalSeries, null, "Episode", isVirtual: false
        );
        episode.SeriesId = metadataSeries.Id;
        var library = CreateLibrary(
            [physicalSeries, metadataSeries], [episode], []
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = true;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(2, changedCount);
        Assert.DoesNotContain(
            "plugin-ignore-empty-items-hidden", physicalSeries.Tags
        );
        Assert.Contains(
            "plugin-ignore-empty-items-hidden", metadataSeries.Tags
        );
        library.Verify(
            m => m.UpdateItemAsync(
                physicalSeries,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NestedRealEpisodeRestoresFilesystemSeason()
    {
        var series = NewSeries("Series");
        var populated = NewSeason(
            series, "Populated", "/library/series/season-1"
        );
        populated.Tags = ["plugin-ignore-empty-items-hidden"];
        var empty = NewSeason(
            series, "Empty", "/library/series/season-2"
        );
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = populated.Id,
            Path = "/library/series/season-1/extras"
        };
        var episode = NewEpisode(
            series, populated, "Episode", isVirtual: false
        );
        episode.ParentId = folder.Id;
        episode.SeasonId = Guid.Empty;
        var library = CreateLibrary(
            [series], [episode], [populated, empty],
            [series, populated, empty, folder, episode]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = true;

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        int changedCount;
        try
        {
            changedCount = await CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None
            );
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }

        Assert.Equal(2, changedCount);
        Assert.DoesNotContain(config.HideTag, populated.Tags);
        Assert.Contains(config.HideTag, empty.Tags);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 1
        );
    }

    [Fact]
    public async Task NestedRealEpisodePreservesSeasonInDeleteMode()
    {
        var series = NewSeries("Series");
        var populated = NewSeason(
            series, "Populated", "/library/series/season-1"
        );
        var empty = NewSeason(
            series, "Empty", "/library/series/season-2"
        );
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = populated.Id,
            Path = "/library/series/season-1/extras"
        };
        var episode = NewEpisode(
            series, populated, "Episode", isVirtual: false
        );
        episode.ParentId = folder.Id;
        episode.SeasonId = empty.Id;
        var library = CreateLibrary(
            [series], [episode], [populated, empty],
            [series, populated, empty, folder, episode]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                populated, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                empty,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        AssertNoScalarCounts(library);
        AssertBulkLookupsOnce(
            library, episodeLookup: 1, seasonLookup: 1
        );
    }

    [Fact]
    public async Task
        NestedOwnedEpisodesUseOnlyRelevantAncestorLookups()
    {
        var physicalSeries = NewSeries("Physical series");
        var metadataSeries = NewSeries("Metadata series");
        var libraryRoot = new Folder { Id = Guid.NewGuid() };
        physicalSeries.ParentId = libraryRoot.Id;
        metadataSeries.ParentId = libraryRoot.Id;
        var seriesFolder = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = physicalSeries.Id
        };
        var populated = NewSeason(
            physicalSeries, "Populated", "/library/physical/populated"
        );
        populated.ParentId = seriesFolder.Id;
        populated.SeriesId = metadataSeries.Id;
        var empty = NewSeason(
            physicalSeries, "Empty", "/library/physical/empty"
        );
        var episodeFolder = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = populated.Id
        };
        var unusedFolder = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = metadataSeries.Id
        };
        var first = NewEpisode(
            physicalSeries, populated, "First", isVirtual: false
        );
        first.SetPrimaryVersionId(Guid.NewGuid());
        first.ParentId = episodeFolder.Id;
        first.SeriesId = metadataSeries.Id;
        first.SeasonId = empty.Id;
        var second = NewEpisode(
            physicalSeries, populated, "Second", isVirtual: false
        );
        second.SetPrimaryVersionId(Guid.NewGuid());
        second.ParentId = episodeFolder.Id;
        second.SeriesId = metadataSeries.Id;
        second.SeasonId = empty.Id;
        var library = CreateLibrary(
            [physicalSeries, metadataSeries], [first, second],
            [populated, empty],
            [
                libraryRoot, physicalSeries, metadataSeries,
                seriesFolder, populated, empty,
                episodeFolder, unusedFolder, first, second
            ],
            simulateOwnedExclusion: true
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(2, changedCount);
        library.Verify(
            m => m.DeleteItem(
                physicalSeries, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(populated, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                metadataSeries, It.IsAny<DeleteOptions>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(empty, It.IsAny<DeleteOptions>()),
            Times.Once);
        library.Verify(
            m => m.GetItemById(seriesFolder.Id), Times.Once);
        library.Verify(
            m => m.GetItemById(episodeFolder.Id), Times.Once);
        library.Verify(
            m => m.GetItemById(It.Is<Guid>(id =>
                id != seriesFolder.Id && id != episodeFolder.Id)),
            Times.Never);
    }

    [Fact]
    public async Task
        UnlistedOwnedSeasonDoesNotAssignEpisodeToStaleSeasonId()
    {
        var series = NewSeries("Series");
        var ownedSeason = NewSeason(
            series, "Owned season", "/library/series/owned"
        );
        var empty = NewSeason(
            series, "Empty season", "/library/series/empty"
        );
        var episode = NewEpisode(
            series, ownedSeason, "Alternate", isVirtual: false
        );
        episode.SetPrimaryVersionId(Guid.NewGuid());
        episode.SeasonId = empty.Id;
        var library = CreateLibrary(
            [series], [episode], [empty],
            [series, ownedSeason, empty, episode],
            simulateOwnedExclusion: true
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(series, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(empty, It.IsAny<DeleteOptions>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(ownedSeason, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.GetItemById(ownedSeason.Id), Times.Once);
    }

    [Fact]
    public async Task
        UnlistedPhysicalSeriesDoesNotProtectStaleMetadataSeries()
    {
        var unlistedSeries = NewSeries("Unlisted physical series");
        var emptyCandidate = NewSeries("Stale metadata series");
        var episode = NewEpisode(
            unlistedSeries, null, "Alternate", isVirtual: false
        );
        episode.SetPrimaryVersionId(Guid.NewGuid());
        episode.SeriesId = emptyCandidate.Id;
        var library = CreateLibrary(
            [emptyCandidate], [episode], [],
            [emptyCandidate, unlistedSeries, episode],
            simulateOwnedExclusion: true
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                emptyCandidate, It.IsAny<DeleteOptions>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                unlistedSeries, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.GetItemById(unlistedSeries.Id), Times.Once);
    }

    [Fact]
    public async Task UnresolvedEpisodeProtectsSeasonIdCandidate()
    {
        var series = NewSeries("Series");
        var candidate = NewSeason(
            series, "Candidate", "/library/series/season-1"
        );
        candidate.Tags = ["plugin-ignore-empty-items-hidden"];
        var empty = NewSeason(
            series, "Empty", "/library/series/season-2"
        );
        var episode = NewEpisode(
            series, candidate, "Episode", isVirtual: false
        );
        episode.ParentId = Guid.NewGuid();
        var library = CreateLibrary(
            [series], [episode], [candidate, empty]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = true;

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        int changedCount;
        try
        {
            changedCount = await CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None
            );
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }

        Assert.Equal(1, changedCount);
        Assert.Contains(config.HideTag, candidate.Tags);
        Assert.Contains(config.HideTag, empty.Tags);
    }

    [Fact]
    public async Task CyclicEpisodeParentProtectsSeasonIdCandidate()
    {
        var series = NewSeries("Series");
        var candidate = NewSeason(
            series, "Candidate", "/library/series/season-1"
        );
        var empty = NewSeason(
            series, "Empty", "/library/series/season-2"
        );
        var first = new Folder { Id = Guid.NewGuid() };
        var second = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = first.Id
        };
        first.ParentId = second.Id;
        var episode = NewEpisode(
            series, candidate, "Episode", isVirtual: false
        );
        episode.ParentId = first.Id;
        var library = CreateLibrary(
            [series], [episode], [candidate, empty],
            [series, candidate, empty, first, second, episode]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                candidate, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                empty,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectEpisodeDoesNotProtectEmptySeason(
        bool hasStaleSeasonId)
    {
        var series = NewSeries("Series");
        var empty = NewSeason(
            series, "Empty", "/library/series/season-1"
        );
        var episode = NewEpisode(
            series, null, "Episode", isVirtual: false
        );
        if (hasStaleSeasonId)
        {
            episode.SeasonId = empty.Id;
        }

        var library = CreateLibrary(
            [series], [episode], [empty]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                empty,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task UnknownEpisodeSeasonProtectsEverySeason()
    {
        var series = NewSeries("Series");
        var first = NewSeason(
            series, "First", "/library/series/season-1"
        );
        var second = NewSeason(
            series, "Second", "/library/series/season-2"
        );
        var episode = NewEpisode(
            series, null, "Episode", isVirtual: false
        );
        episode.ParentId = Guid.NewGuid();
        var library = CreateLibrary(
            [series], [episode], [first, second]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<Season>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task StaleSeasonSeriesIdRestoresPhysicalSeason()
    {
        var physicalSeries = NewSeries("Physical series");
        var metadataSeries = NewSeries("Metadata series");
        var season = NewSeason(
            physicalSeries, "Season", "/library/physical/season"
        );
        season.SeriesId = metadataSeries.Id;
        season.Tags = ["plugin-ignore-empty-items-hidden"];
        var episode = NewEpisode(
            physicalSeries, season, "Episode", isVirtual: false
        );
        var library = CreateLibrary(
            [physicalSeries, metadataSeries], [episode], [season]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = true;

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        int changedCount;
        try
        {
            changedCount = await CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None
            );
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }

        Assert.Equal(1, changedCount);
        Assert.DoesNotContain(
            "plugin-ignore-empty-items-hidden", season.Tags
        );
        library.Verify(
            m => m.UpdateItemAsync(
                season,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(season, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task StaleSeasonSeriesIdCleansPhysicalEmptySeason()
    {
        var physicalSeries = NewSeries("Physical series");
        var metadataSeries = NewSeries("Metadata series");
        var season = NewSeason(
            physicalSeries, "Empty season",
            "/library/physical/empty-season"
        );
        season.SeriesId = metadataSeries.Id;
        var library = CreateLibrary(
            [physicalSeries, metadataSeries], [], [season]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                season,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task UnresolvedSeasonIsNotAssignedByMetadata()
    {
        var series = NewSeries("Metadata series");
        var season = NewSeason(
            series, "Unresolved", "/library/unresolved"
        );
        season.ParentId = Guid.NewGuid();
        var library = CreateLibrary([series], [], [season]);
        var config = CreateConfig();
        config.DeleteEmptyShows = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(season, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task MissingEpisodeParentProtectsMetadataCandidate()
    {
        var candidate = NewSeries("Candidate series");
        var emptySeries = NewSeries("Empty series");
        var season = NewSeason(
            candidate, "Missing graph node",
            "/library/candidate/season"
        );
        var episode = NewEpisode(
            candidate, season, "Episode", isVirtual: false
        );
        var library = CreateLibrary(
            [candidate, emptySeries], [episode], [season],
            [candidate, emptySeries, episode]
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(candidate, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                emptySeries,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task UncertainSeriesKeepsExistingHideTag()
    {
        var candidate = NewSeries("Candidate series");
        candidate.Tags = ["plugin-ignore-empty-items-hidden"];
        var emptySeries = NewSeries("Empty series");
        var episode = NewEpisode(
            candidate, null, "Episode", isVirtual: false
        );
        episode.ParentId = Guid.NewGuid();
        var library = CreateLibrary(
            [candidate, emptySeries], [episode], []
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = true;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        Assert.Contains(
            "plugin-ignore-empty-items-hidden", candidate.Tags
        );
        library.Verify(
            m => m.UpdateItemAsync(
                candidate,
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Contains(
            "plugin-ignore-empty-items-hidden", emptySeries.Tags
        );
    }

    [Fact]
    public async Task UnknownMetadataCandidateProtectsEverySeries()
    {
        var physicalSeries = NewSeries("Physical series");
        var otherSeries = NewSeries("Other series");
        var season = NewSeason(
            physicalSeries, "Missing graph node",
            "/library/physical/season"
        );
        var episode = NewEpisode(
            physicalSeries, season, "Episode", isVirtual: false
        );
        episode.SeriesId = Guid.NewGuid();
        var library = CreateLibrary(
            [physicalSeries, otherSeries], [episode], [season],
            [physicalSeries, otherSeries, episode]
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UnknownEpisodeOwnerProtectsEverySeries()
    {
        var first = NewSeries("First");
        var second = NewSeries("Second");
        var episode = NewEpisode(
            first, null, "Episode", isVirtual: false
        );
        episode.ParentId = Guid.NewGuid();
        episode.SeriesId = Guid.Empty;
        var library = CreateLibrary(
            [first, second], [episode], []
        );
        var config = CreateConfig();
        config.DeleteEmptySeasons = false;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PathlessUnknownEpisodeDoesNotProtectCandidates(
        bool cleanShows)
    {
        var first = NewSeries("First");
        var second = NewSeries("Second");
        var firstSeason = NewSeason(
            first, "First season", "/library/first/season"
        );
        var secondSeason = NewSeason(
            second, "Second season", "/library/second/season"
        );
        var episode = NewEpisode(
            first, firstSeason, "Missing episode",
            isVirtual: false, isMissing: true
        );
        episode.ParentId = Guid.NewGuid();
        episode.SeriesId = Guid.Empty;
        episode.SeasonId = Guid.Empty;
        Assert.False(episode.IsVirtualItem);
        Assert.Null(episode.Path);
        var library = CreateLibrary(
            [first, second], [episode],
            [firstSeason, secondSeason]
        );
        var config = CreateConfig();
        config.DeleteEmptyShows = cleanShows;
        config.DeleteEmptySeasons = !cleanShows;
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(2, changedCount);
        var firstCandidate = cleanShows
            ? (BaseItem)first : firstSeason;
        var secondCandidate = cleanShows
            ? (BaseItem)second : secondSeason;
        library.Verify(
            m => m.DeleteItem(
                firstCandidate,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                secondCandidate,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
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
        IReadOnlyCollection<Season> seasons,
        IReadOnlyCollection<BaseItem>? graphItems = null,
        bool simulateGrouping = false,
        bool simulateOwnedExclusion = false)
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
                    throw new InvalidOperationException(
                        "SeriesCleaner must not hydrate " +
                        "unrestricted library items."
                    );
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
                    var physicalEpisodes = episodes
                        .Where(e => !e.IsVirtualItem &&
                            (!simulateOwnedExclusion ||
                                query.IncludeOwnedItems ||
                                e.PrimaryVersionId is null));
                    if (simulateGrouping &&
                        query.GroupByPresentationUniqueKey)
                    {
                        physicalEpisodes = physicalEpisodes
                            .GroupBy(e => e.GetPresentationUniqueKey())
                            .Select(group => group.First());
                    }

                    return physicalEpisodes.Cast<BaseItem>().ToList();
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
                (graphItems ??
                    series.Cast<BaseItem>()
                        .Concat(episodes)
                        .Concat(seasons)
                        .ToList())
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
