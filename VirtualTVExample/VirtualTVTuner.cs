using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.System;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.IO;
using System.Net;
using MediaBrowser.Model.Querying;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using System.Globalization;

namespace VirtualTVExample
{
    public class VirtualTVTuner : BaseTunerHost, ITunerHost
    {
        private ILibraryManager libraryManager;
        private IUserManager userManager;
        private IMediaSourceManager mediaSourceManager;

        private int GuideSchduleDays = 14;

        public VirtualTVTuner(ILibraryManager libraryManager, IUserManager userManager, IMediaSourceManager mediaSourceManager, IServerApplicationHost appHost)
            : base(appHost)
        {
            this.libraryManager = libraryManager;
            this.userManager = userManager;
            this.mediaSourceManager = mediaSourceManager;
        }

        public override string Name => "Virtual TV Example";

        public override string Type => "virtualtvexample";

        public override string SetupUrl => Plugin.GetPluginPageUrl("virtualtvexample");

        public override bool SupportsGuideData(TunerHostInfo tuner)
        {
            return true;
        }

        public override bool SupportsRemappingGuideData(TunerHostInfo tuner)
        {
            return false;
        }

        protected override Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var options = GetProviderOptions<ProviderTunerOptions>(tuner);

            var list = new List<ChannelInfo>();

            // this could be some kind of dynamic list based on provider options
            list.Add(new ChannelInfo
            {
                Id = CreateEmbyChannelId(tuner, "favoritemovies"),
                Name = "Favorite Movies",
                TunerHostId = tuner.Id,
                ChannelType = ChannelType.TV
            });

            list.Add(new ChannelInfo
            {
                Id = CreateEmbyChannelId(tuner, "favoriteshows"),
                Name = "Favorite Shows",
                TunerHostId = tuner.Id,
                ChannelType = ChannelType.TV
            });

            list.Add(new ChannelInfo
            {
                Id = CreateEmbyChannelId(tuner, "favoritesongs"),
                Name = "Favorite Songs",
                TunerHostId = tuner.Id,
                ChannelType = ChannelType.Radio
            });

            return Task.FromResult(list);
        }

        public override TunerHostInfo GetDefaultConfiguration()
        {
            var info = base.GetDefaultConfiguration();

            SetCustomOptions(info, new ProviderTunerOptions());

            return info;
        }

        protected override Task<ILiveStream> GetChannelStream(TunerHostInfo tuner, BaseItem dbChannnel, ChannelInfo tunerChannel, string mediaSourceId, CancellationToken cancellationToken)
        {
            // we're sending media sources from library content, therefore we'll never get in here because there's no live stream to open
            // in theory we could get here if an item in the library has a playback media source that is a live stream. in this case we'd have to call mediaSourceManager to open it
            // we'll cross that bridge if we get to it
            //throw new NotImplementedException();
            return base.GetChannelStream(tuner, dbChannnel, tunerChannel, mediaSourceId, cancellationToken);
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, BaseItem dbChannnel, ChannelInfo tunerChannel, CancellationToken cancellationToken)
        {
            var tunerChannelId = GetTunerChannelIdFromEmbyChannelId(tuner, tunerChannel.Id);
            var options = GetProviderOptions<ProviderTunerOptions>(tuner);

            // get the program that is currently airing
            var query = new InternalItemsQuery()
            {
                ParentIds = new[] { dbChannnel.InternalId },
                IsAiring = true,
                IncludeItemTypes = new[] { typeof(LiveTvProgram).Name },
                Limit = 1
            };

            var program = libraryManager.GetItemList(query).FirstOrDefault();
            if (program == null)
            {
                throw new ResourceNotFoundException("Program not found.");
            }

            // use the provider id we added to the program to locate the original library item
            var item = libraryManager.GetItemById(program.GetProviderId("DbId"));

            if (item == null)
            {
                return Task.FromResult(new List<MediaSourceInfo>());
            }

            // pass null for the user and false for the other options such as path substitution.
            // this is an internal method to get media sources, which normally would not call mediaSourceManager.GetPlayackMediaSources.
            // the user and path substitution options will end up getting applied by the outer consumer of this, which is why they don't need to be passed down lower
            return mediaSourceManager.GetPlayackMediaSources(item, null, false, false, cancellationToken);
        }

        protected override Task<List<ProgramInfo>> GetProgramsInternal(TunerHostInfo tuner, string tunerChannelId, DateTimeOffset startDateUtc, DateTimeOffset endDateUtc, CancellationToken cancellationToken)
        {
            var list = new List<ProgramInfo>();

            // ensure we have a schedule for the channel saved so that running repeated guide refreshes will produce consistent results
            var schedule = EnsureChannelSchedule(tuner, tunerChannelId);

            foreach (var scheduleItem in schedule.Programs)
            {
                var item = libraryManager.GetItemById(scheduleItem.ItemId);

                // library item no longer available. we'll leave this up to plugin developers to either leave the time slot empty, or put something else there
                if (item == null)
                {
                    continue;
                }

                if (!item.RunTimeTicks.HasValue)
                {
                    continue;
                }

                // check the bounds of what the core server is currently asking for
                if (scheduleItem.EndDate < startDateUtc)
                {
                    // already ended
                    continue;
                }

                if (scheduleItem.StartDate >= endDateUtc)
                {
                    // airs later in the future, beyond what the core server is asking for right now
                    break;
                }

                var program = ConvertToProgramInfo(item);

                program.ChannelId = tunerChannelId;
                program.StartDate = scheduleItem.StartDate;
                program.EndDate = scheduleItem.EndDate;

                program.Id = GetProgramEntryId(program.ShowId, program.StartDate, program.ChannelId);

                list.Add(program);
            }

            return Task.FromResult(list);
        }

        private ProgramInfo ConvertToProgramInfo(BaseItem item)
        {
            var info = new ProgramInfo();

            info.ShowId = item.InternalId.ToString(CultureInfo.InvariantCulture);

            info.Name = item.Name;
            info.Overview = item.Overview;
            info.EpisodeNumber = item.IndexNumber;
            info.SeasonNumber = item.ParentIndexNumber;

            info.ProviderIds = new ProviderIdDictionary(item.ProviderIds);

            // store the original item id so that we can get to it later during playback
            info.ProviderIds["DbId"] = item.InternalId.ToString(CultureInfo.InvariantCulture);

            info.Width = item.Width;
            info.Height = item.Height;
            info.IsMovie = item is Movie;
            info.IsSeries = item is Episode;

            var video = item as Video;
            if (video != null)
            {
                info.Is3D = video.Is3D;
            }

            info.OfficialRating = item.OfficialRating;
            info.CommunityRating = item.CommunityRating;
            info.Genres = item.Genres.ToList();
            info.OriginalAirDate = item.PremiereDate;
            info.ProductionYear = item.ProductionYear;

            var episode = item as Episode;
            if (episode != null)
            {
                var series = episode.Series;

                info.SeriesProviderIds = new ProviderIdDictionary(series.ProviderIds);
                info.SeriesId = episode.SeriesId.ToString(CultureInfo.InvariantCulture);

                info.EpisodeTitle = info.Name;
                info.Name = series.Name;

                AddImages(info, series);
            }
            else
            {
                AddImages(info, item);
            }

            // supplement with album images, when available
            var hasAlbum = item as Audio;
            if (hasAlbum != null)
            {
                var album = libraryManager.GetItemById(hasAlbum.AlbumId);
                if (album != null)
                {
                    AddImages(info, album);
                }
            }

            return info;
        }

        private void AddImages(ProgramInfo info, BaseItem item)
        {
            var image = item.GetImageInfo(ImageType.Primary, 0);

            if (image != null && string.IsNullOrEmpty(info.ImagePath))
            {
                info.ImageUrl = image.Path;
            }

            image = item.GetImageInfo(ImageType.Logo, 0);

            if (image != null && string.IsNullOrEmpty(info.LogoImageUrl))
            {
                info.LogoImageUrl = image.Path;
            }

            image = item.GetImageInfo(ImageType.Backdrop, 0);

            if (image != null && string.IsNullOrEmpty(info.BackdropImageUrl))
            {
                info.BackdropImageUrl = image.Path;
            }
        }

        private string GetTunerDataPath(string tunerId)
        {
            return Path.Combine(Config.ApplicationPaths.CachePath, "livetv", Type, tunerId);
        }

        private string GetChannelDataPath(string tunerId, string tunerChannelId)
        {
            return Path.Combine(GetTunerDataPath(tunerId), tunerChannelId);
        }

        private ChannelSchedule EnsureChannelSchedule(TunerHostInfo tuner, string tunerChannelId)
        {
            var schedule = GetSavedChannelSchedule(tuner, tunerChannelId);

            var saveSchedule = false;

            // create schedule if first time
            if (schedule == null)
            {
                schedule = CreateChannelSchedule(tuner, tunerChannelId);

                saveSchedule = schedule.Programs.Count > 0;
            }

            // if the schedule hasn't been updated in a while, do that now
            // remove old data to keep our storage trim
            // add new data to the end
            if ((DateTime.UtcNow - schedule.LastUpdated) >= TimeSpan.FromHours(6))
            {
                schedule.RemoveOldPrograms();
                AddNewItemsToSchedule(tuner, tunerChannelId, schedule);
                saveSchedule = true;
            }

            if (saveSchedule)
            {
                schedule.LastUpdated = DateTimeOffset.UtcNow;
                SaveChannelSchedule(tuner, tunerChannelId, schedule);
            }

            return schedule;
        }

        private void SaveChannelSchedule(TunerHostInfo tuner, string tunerChannelId, ChannelSchedule channelSchedule)
        {
            var path = GetChannelDataPath(tuner.Id, tunerChannelId);

            try
            {
                FileSystem.CreateDirectory(FileSystem.GetDirectoryName(path));

                JsonSerializer.SerializeToFile(channelSchedule, path);
            }
            catch (Exception ex)
            {
                Logger.ErrorException("Error in SaveChannelSchedule for {0}", ex, path);
            }
        }

        private ChannelSchedule GetSavedChannelSchedule(TunerHostInfo tuner, string tunerChannelId)
        {
            var path = GetChannelDataPath(tuner.Id, tunerChannelId);

            try
            {
                return JsonSerializer.DeserializeFromFile<ChannelSchedule>(path);
            }
            catch (DirectoryNotFoundException)
            {

            }
            catch (FileNotFoundException)
            {

            }
            catch (Exception ex)
            {
                Logger.ErrorException("Error in GetSavedChannelSchedule for {0}", ex, path);
            }

            return null;
        }

        private void AddNewItemsToSchedule(TunerHostInfo tuner, string tunerChannelId, ChannelSchedule channelSchedule)
        {
            var scheduleStartDate = channelSchedule.Programs.FirstOrDefault()?.StartDate ?? DateTimeOffset.UtcNow;
            var scheduleEndDate = scheduleStartDate.AddDays(GuideSchduleDays);

            var options = GetProviderOptions<ProviderTunerOptions>(tuner);

            var user = !string.IsNullOrEmpty(options.UserId) ? userManager.GetUserById(options.UserId) : null;

            // user is gone, there's nothing to do anymore
            if (user == null)
            {
                return;
            }

            // TODO: add new data to the schedule, not exceeding scheduleEndDate
            // you'll need an algorithm to add new items to the schedule, hopefully without too much duplication of what's already there

            var query = CreateItemsQuery(tuner, tunerChannelId, user);
        }

        private ChannelSchedule CreateChannelSchedule(TunerHostInfo tuner, string tunerChannelId)
        {
            var options = GetProviderOptions<ProviderTunerOptions>(tuner);

            var user = !string.IsNullOrEmpty(options.UserId) ? userManager.GetUserById(options.UserId) : null;

            var schedule = new ChannelSchedule()
            {
                LastUpdated = DateTimeOffset.UtcNow
            };

            if (user == null)
            {
                return schedule;
            }

            // round the start date to the beginning of the hour so that the data doesn't start at a funky time
            var startDateUtc = RoundToHour(DateTimeOffset.UtcNow);

            var endDateUtc = startDateUtc.AddDays(GuideSchduleDays);

            var query = CreateItemsQuery(tuner, tunerChannelId, user);

            var items = libraryManager.GetItemList(query);

            foreach (var item in items)
            {
                if (!item.RunTimeTicks.HasValue)
                {
                    continue;
                }

                var program = new ChannelScheduleItem();

                program.StartDate = startDateUtc;
                program.EndDate = startDateUtc.AddTicks(item.RunTimeTicks.Value);

                program.ItemId = item.InternalId;

                schedule.Programs.Add(program);

                startDateUtc = program.EndDate;

                if (startDateUtc >= endDateUtc)
                {
                    break;
                }
            }

            return schedule;
        }

        private InternalItemsQuery CreateItemsQuery(TunerHostInfo tuner, string tunerChannelId, User user)
        {
            // TODO: Fill in data querying algorithms below based on whatever channel it is

            var query = new InternalItemsQuery(user)
            {
                Recursive = true,
                IsFavorite = true
            };

            if (string.Equals(tunerChannelId, "favoritemovies", StringComparison.OrdinalIgnoreCase))
            {
                query.IncludeItemTypes = new[] { typeof(Movie).Name };
            }
            else if (string.Equals(tunerChannelId, "favoriteshows", StringComparison.OrdinalIgnoreCase))
            {
                query.IncludeItemTypes = new[] { typeof(Episode).Name };
            }
            else if (string.Equals(tunerChannelId, "favoritesongs", StringComparison.OrdinalIgnoreCase))
            {
                query.IncludeItemTypes = new[] { typeof(Audio).Name };
            }

            return query;
        }

        private DateTimeOffset RoundToHour(DateTimeOffset date)
        {
            var hours = date.Hour;

            return date.Subtract(date.TimeOfDay).AddHours(hours);
        }

        private void ResetTunerData(TunerHostInfo tuner)
        {
            try
            {
                FileSystem.DeleteDirectory(GetTunerDataPath(tuner.Id), true);
            }
            catch (IOException)
            {

            }
        }

        public override Task OnSaved(TunerHostInfo tuner, bool isNew, CancellationToken cancellationToken)
        {
            // reset data
            // plugin developers could refine this a little and perhaps make selective changes rather than resetting the whole schedule after any config change
            ResetTunerData(tuner);

            return base.OnSaved(tuner, isNew, cancellationToken);
        }

        public override Task OnDeleted(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            // reset data
            ResetTunerData(tuner);

            return base.OnDeleted(tuner, cancellationToken);
        }

        public override Task ValdidateOptions(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Holds additional options to add to the TunerHostInfo
    /// </summary>
    internal class ProviderTunerOptions
    {
        public string UserId { get; set; }
    }

    internal class ChannelSchedule
    {
        public DateTimeOffset LastUpdated { get; set; }
        public List<ChannelScheduleItem> Programs { get; set; } = new List<ChannelScheduleItem>();

        public void RemoveOldPrograms()
        {
            var countToRemove = 0;
            var programs = Programs;

            DateTimeOffset currentDate = DateTimeOffset.UtcNow;

            for (var i = 0; i < programs.Count; i++)
            {
                var program = programs[i];
                if (program.IsOld(currentDate))
                {
                    countToRemove++;
                }
                else
                {
                    break;
                }
            }

            if (countToRemove > 0)
            {
                programs.RemoveRange(0, countToRemove);
            }
        }
    }

    internal class ChannelScheduleItem
    {
        public long ItemId { get; set; }
        public DateTimeOffset StartDate { get; set; }
        public DateTimeOffset EndDate { get; set; }

        public bool IsOld(DateTimeOffset now)
        {
            return EndDate <= now;
        }
    }
}
