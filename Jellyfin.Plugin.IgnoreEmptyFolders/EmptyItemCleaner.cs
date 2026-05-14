using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class EmptyItemCleaner
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public EmptyItemCleaner(
        ILibraryManager libraryManager,
        ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public int CleanLibrary(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var deleteEmptyShows = config?.DeleteEmptyShows ?? true;
        var deleteEmptySeasons = config?.DeleteEmptySeasons ?? true;
        var deleteEmptyMovies = config?.DeleteEmptyMovies ?? true;
        var deleteEmptyMusicArtists =
            config?.DeleteEmptyMusicArtists ?? true;
        var deleteEmptyMusicAlbums =
            config?.DeleteEmptyMusicAlbums ?? true;
        var deleteEmptyCollections =
            config?.DeleteEmptyCollections ?? true;
        var deleteEmptyFolders = config?.DeleteEmptyFolders ?? true;
        var deleteEmptyPlaylists =
            config?.DeleteEmptyPlaylists ?? true;
        var logDeletions = config?.LogDeletions ?? true;

        if (!deleteEmptyShows && !deleteEmptySeasons &&
            !deleteEmptyMovies && !deleteEmptyMusicArtists &&
            !deleteEmptyMusicAlbums && !deleteEmptyCollections &&
            !deleteEmptyFolders && !deleteEmptyPlaylists)
        {
            _logger.LogInformation(
                "Ignore Empty Folders: All options are " +
                "disabled, skipping");
            return 0;
        }

        var removedCount = 0;

        // Progress split:
        // 30% Series/Seasons
        // 20% Movies
        // 20% Music
        // 30% Containers
        if (deleteEmptyShows || deleteEmptySeasons)
        {
            removedCount += CleanSeriesAndSeasons(
                deleteEmptyShows,
                deleteEmptySeasons,
                logDeletions,
                progress,
                cancellationToken);
        }

        if (deleteEmptyMovies)
        {
            removedCount += CleanMovies(
                logDeletions,
                progress,
                cancellationToken);
        }

        if (deleteEmptyMusicArtists || deleteEmptyMusicAlbums)
        {
            removedCount += CleanMusic(
                deleteEmptyMusicArtists,
                deleteEmptyMusicAlbums,
                logDeletions,
                progress,
                cancellationToken);
        }

        if (deleteEmptyCollections ||
            deleteEmptyFolders ||
            deleteEmptyPlaylists)
        {
            removedCount += CleanContainers(
                deleteEmptyCollections,
                deleteEmptyFolders,
                deleteEmptyPlaylists,
                logDeletions,
                progress,
                cancellationToken);
        }

        _logger.LogInformation(
            "Ignore Empty Folders: Total removed {Count} empty items",
            removedCount);
        return removedCount;
    }

    private int CleanSeriesAndSeasons(
        bool deleteEmptyShows,
        bool deleteEmptySeasons,
        bool logDeletions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var seriesList = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            });

        _logger.LogInformation(
            "Ignore Empty Folders: Checking {Count} series",
            seriesList.Count);

        var removedCount = 0;
        var total = seriesList.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seriesList[i] is not Series series)
            {
                continue;
            }

            var seriesKey = series.GetPresentationUniqueKey();

            if (deleteEmptyShows)
            {
                var episodeCount = _libraryManager.GetCount(
                    new InternalItemsQuery
                    {
                        AncestorWithPresentationUniqueKey = null,
                        SeriesPresentationUniqueKey = seriesKey,
                        IncludeItemTypes = new[]
                        {
                            BaseItemKind.Episode
                        },
                        IsVirtualItem = false,
                        IsMissing = false,
                        Limit = 0,
                        DtoOptions = new DtoOptions(false)
                        {
                            EnableImages = false
                        }
                    });

                if (episodeCount == 0)
                {
                    if (logDeletions)
                    {
                        _logger.LogInformation(
                            "Ignore Empty Folders: Removing series " +
                            "\"{Name}\" - no files",
                            series.Name);
                    }

                    try
                    {
                        _libraryManager.DeleteItem(
                            series,
                            new DeleteOptions
                            {
                                DeleteFileLocation = false
                            });
                        removedCount++;
                        progress?.Report((double)(i + 1) / total * 30);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Ignore Empty Folders: Failed to remove " +
                            "series \"{Name}\"",
                            series.Name);
                    }
                }
            }

            if (deleteEmptySeasons)
            {
                var seasonList = _libraryManager.GetItemList(
                    new InternalItemsQuery
                    {
                        AncestorWithPresentationUniqueKey = null,
                        SeriesPresentationUniqueKey = seriesKey,
                        IncludeItemTypes = new[]
                        {
                            BaseItemKind.Season
                        },
                        DtoOptions = new DtoOptions(false)
                        {
                            EnableImages = false
                        }
                    });

                foreach (var item in seasonList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (item is not Season season)
                    {
                        continue;
                    }

                    var seasonEpisodeCount = _libraryManager.GetCount(
                        new InternalItemsQuery
                        {
                            AncestorWithPresentationUniqueKey = null,
                            SeriesPresentationUniqueKey = seriesKey,
                            ParentIndexNumber = season.IndexNumber,
                            IncludeItemTypes = new[]
                            {
                                BaseItemKind.Episode
                            },
                            IsVirtualItem = false,
                            IsMissing = false,
                            Limit = 0,
                            DtoOptions = new DtoOptions(false)
                            {
                                EnableImages = false
                            }
                        });

                    if (seasonEpisodeCount == 0)
                    {
                    if (logDeletions)
                    {
                        _logger.LogInformation(
                            "Ignore Empty Folders: Removing " +
                            "season \"{Season}\" of \"{Series}\"",
                            season.Name,
                            series.Name);
                    }

                        try
                        {
                            _libraryManager.DeleteItem(
                                season,
                                new DeleteOptions
                                {
                                    DeleteFileLocation = false
                                });
                            removedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Ignore Empty Folders: Failed to " +
                                "remove season \"{Season}\" of " +
                                "\"{Series}\"",
                                season.Name,
                                series.Name);
                        }
                    }
                }
            }

            progress?.Report((double)(i + 1) / total * 30);
        }

        return removedCount;
    }

    private int CleanMovies(
        bool logDeletions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = true,
            DtoOptions = new DtoOptions(false) { EnableImages = false }
        });

        var missingMovies = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsMissing = true,
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            });

        var allEmptyMovies = movies.Concat(missingMovies)
            .DistinctBy(x => x.Id).ToList();

        _logger.LogInformation(
            "Ignore Empty Folders: Checking {Count} empty movies",
            allEmptyMovies.Count);

        var removedCount = 0;
        var total = allEmptyMovies.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = allEmptyMovies[i];

            if (logDeletions)
            {
                _logger.LogInformation(
                    "Ignore Empty Folders: Removing movie \"{Name}\"",
                    movie.Name);
            }

            try
            {
                _libraryManager.DeleteItem(
                    movie,
                    new DeleteOptions { DeleteFileLocation = false });
                removedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Ignore Empty Folders: Failed to remove " +
                    "movie \"{Name}\"",
                    movie.Name);
            }

            progress?.Report(30 + (double)(i + 1) / total * 20);
        }

        return removedCount;
    }

    private int CleanMusic(
        bool deleteEmptyArtists,
        bool deleteEmptyAlbums,
        bool logDeletions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;

        if (deleteEmptyArtists)
        {
            var artists = _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = new[]
                    {
                        BaseItemKind.MusicArtist
                    },
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false
                    }
                });

            _logger.LogInformation(
                "Ignore Empty Folders: Checking {Count} artists",
                artists.Count);

            foreach (var artist in artists)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trackCount = _libraryManager.GetCount(
                    new InternalItemsQuery
                    {
                        ArtistIds = new[] { artist.Id },
                        IncludeItemTypes = new[] { BaseItemKind.Audio },
                        IsVirtualItem = false,
                        IsMissing = false,
                        Limit = 0,
                        DtoOptions = new DtoOptions(false)
                        {
                            EnableImages = false
                        }
                    });

                if (trackCount == 0)
                {
                    if (logDeletions)
                    {
                        _logger.LogInformation(
                            "Ignore Empty Folders: Removing artist " +
                            "\"{Name}\"",
                            artist.Name);
                    }

                    try
                    {
                        _libraryManager.DeleteItem(
                            artist,
                            new DeleteOptions
                            {
                                DeleteFileLocation = false
                            });
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Ignore Empty Folders: Failed to " +
                            "remove artist \"{Name}\"",
                            artist.Name);
                    }
                }
            }
        }

        if (deleteEmptyAlbums)
        {
            var albums = _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = new[]
                    {
                        BaseItemKind.MusicAlbum
                    },
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false
                    }
                });

            _logger.LogInformation(
                "Ignore Empty Folders: Checking {Count} albums",
                albums.Count);

            foreach (var album in albums)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trackCount = _libraryManager.GetCount(
                    new InternalItemsQuery
                    {
                        ParentId = album.Id,
                        IncludeItemTypes = new[] { BaseItemKind.Audio },
                        IsVirtualItem = false,
                        IsMissing = false,
                        Limit = 0,
                        DtoOptions = new DtoOptions(false)
                        {
                            EnableImages = false
                        }
                    });

                if (trackCount == 0)
                {
                    if (logDeletions)
                    {
                        _logger.LogInformation(
                            "Ignore Empty Folders: Removing album " +
                            "\"{Name}\"",
                            album.Name);
                    }

                    try
                    {
                        _libraryManager.DeleteItem(
                            album,
                            new DeleteOptions
                            {
                                DeleteFileLocation = false
                            });
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Ignore Empty Folders: Failed to " +
                            "remove album \"{Name}\"",
                            album.Name);
                    }
                }
            }
        }

        progress?.Report(70);
        return removedCount;
    }

    private int CleanContainers(
        bool deleteEmptyCollections,
        bool deleteEmptyFolders,
        bool deleteEmptyPlaylists,
        bool logDeletions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;
        var types = new List<BaseItemKind>();
        if (deleteEmptyCollections) types.Add(BaseItemKind.BoxSet);
        if (deleteEmptyFolders) types.Add(BaseItemKind.Folder);
        if (deleteEmptyPlaylists) types.Add(BaseItemKind.Playlist);

        var containers = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = types.ToArray(),
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            });

        _logger.LogInformation(
            "Ignore Empty Folders: Checking {Count} containers",
            containers.Count);

        var total = containers.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var container = containers[i];

            var childCount = _libraryManager.GetCount(
                new InternalItemsQuery
                {
                    ParentId = container.Id,
                    Recursive = true,
                    IsVirtualItem = false,
                    IsMissing = false,
                    Limit = 0,
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false
                    }
                });

            if (childCount == 0)
            {
                if (logDeletions)
                {
                    _logger.LogInformation(
                        "Ignore Empty Folders: Removing " +
                        "{Type} \"{Name}\"",
                        container.GetType().Name,
                        container.Name);
                }

                try
                {
                    _libraryManager.DeleteItem(
                        container,
                        new DeleteOptions
                        {
                            DeleteFileLocation = false
                        });
                    removedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Ignore Empty Folders: Failed to remove " +
                        "{Type} \"{Name}\"",
                        container.GetType().Name,
                        container.Name);
                }
            }

            progress?.Report(70 + (double)(i + 1) / total * 30);
        }

        return removedCount;
    }
}
