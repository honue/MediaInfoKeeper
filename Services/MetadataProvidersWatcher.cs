using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MediaInfoKeeper;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 控制 Emby ProviderManager 的元数据刷新，仅在显式作用域内放行。
    /// </summary>
    public static class MetadataProvidersWatcher
    {
        private static Harmony harmony;
        private static MethodInfo staticCanRefresh;
        private static MethodInfo instanceCanRefresh;
        private static ILogger logger;
        private static bool isEnabled;
        public static void Initialize(ILogger pluginLogger, bool enableWatcher)
        {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = enableWatcher;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManager == null)
                {
                    logger.Warn("MetadataProvidersWatcher 初始化跳过：未找到 ProviderManager");
                    return;
                }

                staticCanRefresh = ResolveStaticCanRefresh(providerManager);
                instanceCanRefresh = ResolveInstanceCanRefresh(providerManager);

                if (staticCanRefresh == null && instanceCanRefresh == null)
                {
                    logger.Warn("MetadataProvidersWatcher 初始化失败：未找到 CanRefresh 重载");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.metadata");

                try
                {
                if (staticCanRefresh != null)
                {
                    logger.Info($"MetadataProvidersWatcher 目标静态方法: {staticCanRefresh.DeclaringType?.FullName}.{staticCanRefresh.Name}({string.Join(",", staticCanRefresh.GetParameters().Select(p => p.ParameterType.Name))})");
                    harmony.Patch(staticCanRefresh,
                        prefix: new HarmonyMethod(typeof(MetadataProvidersWatcher), nameof(CanRefreshPrefix)));
                }

                if (instanceCanRefresh != null)
                {
                    logger.Info($"MetadataProvidersWatcher 目标实例方法: {instanceCanRefresh.DeclaringType?.FullName}.{instanceCanRefresh.Name}({string.Join(",", instanceCanRefresh.GetParameters().Select(p => p.ParameterType.Name))})");
                    harmony.Patch(instanceCanRefresh,
                        prefix: new HarmonyMethod(typeof(MetadataProvidersWatcher), nameof(CanRefreshPrefix)));
                }
                }
                catch (Exception patchEx)
                {
                    logger.Error("MetadataProvidersWatcher patch 失败");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    return;
                }

                logger.Info("MetadataProvidersWatcher 已安装");
            }
            catch (Exception e)
            {
                logger.Error("MetadataProvidersWatcher 初始化失败");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enableWatcher)
        {
            isEnabled = enableWatcher;
        }

        private static bool CanRefreshPrefix(object __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (IsVideoImageProviderWithEpisode(__args, out var item))
            {
                logger?.Info("MetadataProvidersWatcher 检测到元数据变动");
                logger?.Info($"MetadataProvidersWatcher InternalId:{item.InternalId},Name:{item.Name ?? string.Empty}");
                TriggerMediaInfoRestore(item);
            }
            return true;
        }

        private static bool IsVideoImageProviderWithEpisode(object[] args, out BaseItem item)
        {
            item = null;
            if (args == null || args.Length < 2) return false;

            var provider = args[0];
            if (provider == null) return false;

            var typeName = provider.GetType().FullName ?? provider.GetType().Name;
            if (!string.Equals(typeName, "Emby.Providers.MediaInfo.VideoImageProvider", StringComparison.Ordinal))
            {
                return false;
            }

            if (args[1] is Episode baseItem && baseItem.InternalId != 0)
            {
                item = baseItem;
                return true;
            }

            return false;
        }

        private static void TriggerMediaInfoRestore(BaseItem item)
        {
            if (item == null) return;

            var mediaInfoService = Plugin.MediaInfoService;
            var fileSystem = Plugin.FileSystem;
            if (mediaInfoService == null || fileSystem == null) return;

            Task.Run(async () =>
            {
                try
                {
                    logger?.Info($"{item.Path ?? string.Empty} 10s之后开始恢复媒体信息");
                    await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    var directoryService = new DirectoryService(logger, fileSystem);
                    await mediaInfoService
                        .DeserializeMediaInfo(item, directoryService, "MetadataProvidersWatcher 恢复媒体信息", true)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.Error("MetadataProvidersWatcher 恢复 MediaInfo 失败");
                    logger?.Error(ex.Message);
                }
            });
        }

        private static MethodInfo FindMethod(Type type, string methodName, Func<MethodInfo, bool> predicate = null)
        {
            if (type == null) return null;

            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                          BindingFlags.NonPublic)
                .Where(m => m.Name == methodName);

            if (predicate != null) methods = methods.Where(predicate);

            var methodInfo = methods.FirstOrDefault();
            return methodInfo;
        }

        private static MethodInfo ResolveStaticCanRefresh(Type providerManager)
        {
            try
            {
                var paramTypes = new[]
                {
                    typeof(IMetadataProvider),
                    typeof(BaseItem),
                    typeof(LibraryOptions),
                    typeof(bool),
                    typeof(bool),
                    typeof(bool)
                };
                return providerManager.GetMethod("CanRefresh",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    paramTypes,
                    null) ?? FindMethod(providerManager, "CanRefresh", m => m.IsStatic);
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo ResolveInstanceCanRefresh(Type providerManager)
        {
            try
            {
                var paramTypes = new[]
                {
                    typeof(IImageProvider),
                    typeof(BaseItem),
                    typeof(LibraryOptions),
                    typeof(ImageRefreshOptions),
                    typeof(bool),
                    typeof(bool)
                };
                return providerManager.GetMethod("CanRefresh",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    paramTypes,
                    null) ?? FindMethod(providerManager, "CanRefresh", m => !m.IsStatic);
            }
            catch
            {
                return null;
            }
        }

    }
}
