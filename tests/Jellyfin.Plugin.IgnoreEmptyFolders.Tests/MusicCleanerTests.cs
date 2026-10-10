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
                    compilation, [creditedArtist.Id], []),
                new AlbumEntry(
                    secondCompilation, [], [albumArtist.Id]),
                new AlbumEntry(physicalAlbum, [], [])
            ]);

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
        library.Verify(
            m => m.GetCount(It.Is<InternalItemsQuery>(q =>
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
        var missingAlbum = NewAlbum(Guid.NewGuid());
        var library = CreateLibrary(
            [creditedArtist, emptyArtist],
            [
                new AlbumEntry(
                    realAlbum, [creditedArtist.Id], []),
                new AlbumEntry(
                    virtualAlbum, [emptyArtist.Id], []),
                new AlbumEntry(
                    missingAlbum, [], [emptyArtist.Id],
                    IsMissing: true)
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

                throw new InvalidOperationException(
                    "Unexpected music item list query.");
            });
        library
            .Setup(m => m.GetCount(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (!query.IncludeItemTypes.SequenceEqual(
                    [BaseItemKind.MusicAlbum]))
                {
                    throw new InvalidOperationException(
                        "Unexpected album count query.");
                }

                return albums.Count(entry =>
                    (!entry.Album.IsVirtualItem ||
                        query.IsVirtualItem != false) &&
                    (!entry.IsMissing || query.IsMissing != false) &&
                    (query.ParentId != Guid.Empty
                        ? query.ParentId == entry.Album.ParentId
                        : query.ArtistIds?.Any(id =>
                            entry.ArtistIds.Contains(id) ||
                            entry.AlbumArtistIds.Contains(id)) == true));
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
        bool IsMissing = false);
}
