using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class UpdatePluginTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly IApplicationHost applicationHost;
        private readonly IApplicationPaths applicationPaths;
        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IActivityManager activityManager;
        private readonly IServerApplicationHost serverApplicationHost;

        public UpdatePluginTask(
            IApplicationHost applicationHost,
            IApplicationPaths applicationPaths,
            IHttpClient httpClient,
            IJsonSerializer jsonSerializer,
            IActivityManager activityManager,
            IServerApplicationHost serverApplicationHost)
        {
            this.logger = Plugin.Instance.Logger;
            this.applicationHost = applicationHost;
            this.applicationPaths = applicationPaths;
            this.httpClient = httpClient;
            this.jsonSerializer = jsonSerializer;
            this.activityManager = activityManager;
            this.serverApplicationHost = serverApplicationHost;
        }

        private static string PluginAssemblyFilename => Assembly.GetExecutingAssembly().GetName().Name + ".dll";
        private static string RepoReleaseUrl => "https://api.github.com/repos/honue/MediaInfoKeeper/releases/latest";

        public string Key => "UpdatePluginTask";

        public string Name => "更新插件";

        public string Description => "更新插件至最新版本";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = (DayOfWeek)new Random().Next(7),
                TimeOfDayTicks = TimeSpan.FromMinutes(new Random().Next(24 * 4) * 15).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            try
            {
                using var response = await httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = RepoReleaseUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false
                }, "GET").ConfigureAwait(false);

                await using var contentStream = response.Content;
                var apiResult = jsonSerializer.DeserializeFromStream<ApiResponseInfo>(contentStream);

                var currentVersion = ParseVersion(GetCurrentVersion());
                var remoteVersion = ParseVersion(apiResult?.tag_name);

                if (currentVersion.CompareTo(remoteVersion) < 0)
                {
                    logger.Info("Found new plugin version: {0}", remoteVersion);

                    var url = (apiResult?.assets ?? new List<ApiAssetInfo>())
                        .FirstOrDefault(asset => asset.name == PluginAssemblyFilename)
                        ?.browser_download_url;
                    if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    {
                        throw new Exception("Invalid download url");
                    }

                    await using (var responseStream = await httpClient.Get(new HttpRequestOptions
                                     {
                                         Url = url,
                                         CancellationToken = cancellationToken,
                                         UserAgent = "MediaInfoKeeper",
                                         EnableDefaultUserAgent = false,
                                         Progress = progress
                                     })
                                     .ConfigureAwait(false))
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            await responseStream.CopyToAsync(memoryStream, 81920, cancellationToken)
                                .ConfigureAwait(false);

                            memoryStream.Seek(0, SeekOrigin.Begin);
                            var dllFilePath = Path.Combine(applicationPaths.PluginsPath, PluginAssemblyFilename);

                            await using (var fileStream =
                                         new FileStream(dllFilePath, FileMode.Create, FileAccess.Write))
                            {
                                await memoryStream.CopyToAsync(fileStream, 81920, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }

                    logger.Info("Plugin update complete");

                    activityManager.Create(new ActivityLogEntry
                    {
                        Name = Plugin.Instance.Name + " Updated to " + remoteVersion + " on " +
                               serverApplicationHost.FriendlyName,
                        Type = "PluginUpdateInstalled",
                        Severity = LogSeverity.Info
                    });

                    applicationHost.NotifyPendingRestart();
                }
                else
                {
                    logger.Info("No need to update");
                }
            }
            catch (Exception ex)
            {
                activityManager.Create(new ActivityLogEntry
                {
                    Name = Plugin.Instance.Name + " update failed on " + serverApplicationHost.FriendlyName,
                    Type = "PluginUpdateFailed",
                    Overview = ex.Message,
                    Severity = LogSeverity.Error
                });

                logger.Error("Update error: {0}", ex.Message);
                logger.Debug(ex.StackTrace);
            }

            progress.Report(100);
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Version(0, 0, 0);
            }

            var normalized = value.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(1)
                : value;
            return new Version(normalized);
        }

        private static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : version.ToString(3);
        }

        internal class ApiResponseInfo
        {
            public string tag_name { get; set; }

            public List<ApiAssetInfo> assets { get; set; }
        }

        internal class ApiAssetInfo
        {
            public string name { get; set; }

            public string browser_download_url { get; set; }
        }
    }
}
