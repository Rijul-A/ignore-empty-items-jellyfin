using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tests;

[Collection("BaseItem.LibraryManager")]
public sealed class MusicCleanerTests
{
    [Fact]
    public async Task CreditedAndPhysicalAlbumsRestoreHiddenArtists()
    {
        var config = CreateConfig();
        var creditedArtist = NewArtist("Compilation performer");
        var albumArtist = NewArtist("Compilation album artist");
        var physicalArtist = NewArtist("Physical artist");
        var emptyArtist = NewArtist("Empty artist");
        var artists = new[]
        {
            creditedArtist, albumArtist, physicalArtist, emptyArtist
        };
        creditedArtist.Tags = [config.HideTag];
        albumArtist.Tags = [config.HideTag];
        physicalArtist.Tags = [config.HideTag];

        var compilation = NewAlbum(Guid.NewGuid());
        var secondCompilation = NewAlbum(Guid.NewGuid());
        var physicalAlbum = NewAlbum(physicalArtist.Id);
        var library = CreateLibrary(
            artists,
            [
                new AlbumEntry(
                    compilation, [creditedArtist.Id], [],
                    [NewAudio(compilation, "/music/compilation.mp3")]),
                new AlbumEntry(
                    secondCompilation, [], [albumArtist.Id],
                    [NewAudio(secondCompilation, "/music/second.mp3")]),
                new AlbumEntry(
                    physicalAlbum, [], [],
                    [NewAudio(physicalAlbum, "/music/physical.mp3")])
            ]);
        config.DeleteEmptyMusicAlbums = true;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(4, changedCount);
        Assert.DoesNotContain(config.HideTag, creditedArtist.Tags);
        Assert.DoesNotContain(config.HideTag, albumArtist.Tags);
        Assert.DoesNotContain(config.HideTag, physicalArtist.Tags);
        Assert.Contains(config.HideTag, emptyArtist.Tags);
        foreach (var artist in artists)
        {
            library.Verify(
                m => m.UpdateItemAsync(
                    artist,
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataEdit,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        foreach (var album in new[]
            { compilation, secondCompilation, physicalAlbum })
        {
            library.Verify(
                m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                    q.IncludeItemTypes.SequenceEqual(
                        new[] { BaseItemKind.Audio }) &&
                    q.ParentId == album.Id)),
                Times.Once);
        }
        library.Verify(
            m => m.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ArtistIds != null &&
                q.ArtistIds.Contains(physicalArtist.Id))),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task OnlyRealCreditedAlbumsProtectFromDeletion()
    {
        var creditedArtist = NewArtist("Album performer");
        var emptyArtist = NewArtist("Virtual albums only");
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;
        var realAlbum = NewAlbum(Guid.NewGuid());
        var virtualAlbum = NewAlbum(
            Guid.NewGuid(), isVirtual: true);
        var missingAlbum = NewAlbum(
            Guid.NewGuid(), isVirtual: true);
        var library = CreateLibrary(
            [creditedArtist, emptyArtist],
            [
                new AlbumEntry(
                    realAlbum, [creditedArtist.Id], [],
                    [NewAudio(realAlbum, "/music/song.mp3")]),
                new AlbumEntry(
                    virtualAlbum, [emptyArtist.Id], [],
                    [NewAudio(virtualAlbum, "/music/virtual.mp3")]),
                new AlbumEntry(
                    missingAlbum, [], [emptyArtist.Id],
                    [NewAudio(missingAlbum, "/music/missing.mp3")])
            ]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(1, changedCount);
        library.Verify(
            m => m.DeleteItem(
                emptyArtist,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                creditedArtist, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task AlbumArtistOnlyAlbumProtectsFromDeletion()
    {
        var artist = NewArtist("Compilation album artist");
        var compilation = NewAlbum(Guid.NewGuid());
        var library = CreateLibrary(
            [artist],
            [new AlbumEntry(
                compilation, [], [artist.Id],
                [NewAudio(compilation, "/music/song.mp3")])]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                artist, It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task TracklessCreditedCompilationDoesNotProtectArtist(
        bool hideInsteadOfDelete, bool albumArtistOnly)
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = hideInsteadOfDelete;
        config.DeleteEmptyMusicAlbums = true;
        var artist = NewArtist("Compilation performer");
        var compilation = NewAlbum(Guid.NewGuid());
        if (hideInsteadOfDelete)
        {
            compilation.Tags = [config.HideTag];
        }

        var library = CreateLibrary(
            [artist],
            [new AlbumEntry(
                compilation,
                albumArtistOnly ? [] : [artist.Id],
                albumArtistOnly ? [artist.Id] : [],
                [])]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(hideInsteadOfDelete ? 1 : 2, changedCount);
        if (hideInsteadOfDelete)
        {
            Assert.Contains(config.HideTag, artist.Tags);
            Assert.Contains(config.HideTag, compilation.Tags);
            library.Verify(
                m => m.DeleteItem(
                    It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
                Times.Never);
        }
        else
        {
            library.Verify(
                m => m.DeleteItem(
                    artist, It.IsAny<DeleteOptions>()),
                Times.Once);
            library.Verify(
                m => m.DeleteItem(
                    compilation, It.IsAny<DeleteOptions>()),
                Times.Once);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingTrackDoesNotProtectAlbumArtist(
        bool hideInsteadOfDelete)
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = hideInsteadOfDelete;
        config.DeleteEmptyMusicAlbums = true;
        var artist = NewArtist("Compilation album artist");
        var compilation = NewAlbum(Guid.NewGuid());
        var missingTrack = NewAudio(
            compilation, "/music/compilation/missing.mp3",
            isVirtual: true);
        var library = CreateLibrary(
            [artist],
            [new AlbumEntry(
                compilation, [], [artist.Id], [missingTrack])]);

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        int changedCount;
        try
        {
            changedCount = await CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None);
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }

        Assert.Equal(2, changedCount);
        if (hideInsteadOfDelete)
        {
            Assert.Contains(config.HideTag, artist.Tags);
            Assert.Contains(config.HideTag, compilation.Tags);
            library.Verify(
                m => m.DeleteItem(
                    It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
                Times.Never);
        }
        else
        {
            library.Verify(
                m => m.DeleteItem(
                    artist, It.IsAny<DeleteOptions>()),
                Times.Once);
            library.Verify(
                m => m.DeleteItem(
                    compilation, It.IsAny<DeleteOptions>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task AlbumEligibilityUsesRealAudioPathOrChannel()
    {
        var config = CreateConfig();
        config.DeleteEmptyMusicAlbums = true;
        var pathlessArtist = NewArtist("Pathless artist");
        var channelArtist = NewArtist("Channel artist");
        channelArtist.Tags = [config.HideTag];
        var pathlessAlbum = NewAlbum(pathlessArtist.Id);
        var virtualAlbum = NewAlbum(Guid.NewGuid());
        var channelAlbum = NewAlbum(channelArtist.Id);
        channelAlbum.Tags = [config.HideTag];
        var channel = NewAudio(channelAlbum, null);
        channel.ChannelId = Guid.NewGuid();
        var library = CreateLibrary(
            [pathlessArtist, channelArtist],
            [
                new AlbumEntry(
                    pathlessAlbum, [], [],
                    [NewAudio(pathlessAlbum, null)]),
                new AlbumEntry(
                    virtualAlbum, [], [],
                    [NewAudio(
                        virtualAlbum, "/music/virtual.mp3",
                        isVirtual: true)]),
                new AlbumEntry(channelAlbum, [], [], [channel])
            ]);

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        int changedCount;
        try
        {
            changedCount = await CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None);
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }

        Assert.Equal(5, changedCount);
        Assert.Contains(config.HideTag, pathlessArtist.Tags);
        Assert.DoesNotContain(config.HideTag, channelArtist.Tags);
        Assert.Contains(config.HideTag, pathlessAlbum.Tags);
        Assert.Contains(config.HideTag, virtualAlbum.Tags);
        Assert.DoesNotContain(config.HideTag, channelAlbum.Tags);
    }

    private static MusicArtist NewArtist(string name)
    {
        return new MusicArtist
        {
            Id = Guid.NewGuid(),
            Name = name
        };
    }

    private static MusicAlbum NewAlbum(
        Guid parentId, bool isVirtual = false)
    {
        return new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Name = "Compilation",
            ParentId = parentId,
            Path = "/music/compilations/album",
            IsVirtualItem = isVirtual
        };
    }

    private static Audio NewAudio(
        MusicAlbum album, string? path, bool isVirtual = false)
    {
        return new Audio
        {
            Id = Guid.NewGuid(),
            ParentId = album.Id,
            Path = path,
            IsVirtualItem = isVirtual
        };
    }

    private static PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration
        {
            DeleteEmptyMusicArtists = true,
            DeleteEmptyMusicAlbums = false,
            LogDeletions = false
        };
    }

    private static MusicCleaner CreateCleaner(
        Mock<ILibraryManager> library)
    {
        return new MusicCleaner(
            library.Object, NullLogger<MusicCleaner>.Instance);
    }

    private static Mock<ILibraryManager> CreateLibrary(
        IReadOnlyCollection<MusicArtist> artists,
        IReadOnlyCollection<AlbumEntry> albums)
    {
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.IncludeItemTypes.SequenceEqual(
                    [BaseItemKind.MusicArtist]))
                {
                    return artists.Cast<BaseItem>().ToList();
                }

                if (query.IncludeItemTypes.SequenceEqual(
                    [BaseItemKind.MusicAlbum]))
                {
                    return albums
                        .Where(entry =>
                            ((query.IsVirtualItem ?? query.IsMissing)
                                is not bool isVirtual ||
                                entry.Album.IsVirtualItem ==
                                    isVirtual) &&
                            (query.ParentId != Guid.Empty
                                ? query.ParentId == entry.Album.ParentId
                                : (query.ArtistIds?.Length is not > 0 ||
                                    query.ArtistIds.Any(id =>
                                        entry.ArtistIds.Contains(id) ||
                                        entry.AlbumArtistIds
                                            .Contains(id))) &&
                                  (query.AlbumArtistIds?.Length
                                      is not > 0 ||
                                    query.AlbumArtistIds.Any(id =>
                                        entry.AlbumArtistIds
                                            .Contains(id)))))
                        .Select(entry => (BaseItem)entry.Album)
                        .ToList();
                }

                if (query.IncludeItemTypes.SequenceEqual(
                    [BaseItemKind.Audio]))
                {
                    return albums
                        .Where(entry =>
                            entry.Album.Id == query.ParentId)
                        .SelectMany(entry => entry.Tracks.Where(track =>
                            (query.IsVirtualItem ?? query.IsMissing)
                                is not bool isVirtual ||
                            track.IsVirtualItem == isVirtual))
                        .Cast<BaseItem>()
                        .ToList();
                }

                throw new InvalidOperationException(
                    "Unexpected music item list query.");
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return library;
    }

    private sealed record AlbumEntry(
        MusicAlbum Album,
        Guid[] ArtistIds,
        Guid[] AlbumArtistIds,
        Audio[] Tracks);
}
