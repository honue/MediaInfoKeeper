using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 控制 Emby ProviderManager 的元数据刷新，仅在显式作用域内放行。
    /// </summary>
    public static class MetadataProvidersGuard
    {
        private static readonly AsyncLocal<int> GuardCount = new AsyncLocal<int>();

        private static Harmony harmony;
        private static MethodInfo staticCanRefresh;
        private static MethodInfo instanceCanRefresh;
        private static ILogger logger;
        private static bool isEnabled;

        public static void Initialize(ILogger pluginLogger, bool disableSystemMetadata)
        {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = disableSystemMetadata;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManager == null)
                {
                    logger.Warn("MetadataProvidersGuard init skipped: ProviderManager not found");
                    return;
                }

                staticCanRefresh = ResolveStaticCanRefresh(providerManager);
                instanceCanRefresh = ResolveInstanceCanRefresh(providerManager);

                if (staticCanRefresh == null && instanceCanRefresh == null)
                {
                    logger.Warn("MetadataProvidersGuard init failed: no CanRefresh overloads found");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.metadata");

                try
                {
                if (staticCanRefresh != null)
                {
                    logger.Info($"MetadataProvidersGuard target static: {staticCanRefresh.DeclaringType?.FullName}.{staticCanRefresh.Name}({string.Join(",", staticCanRefresh.GetParameters().Select(p => p.ParameterType.Name))})");
                    harmony.Patch(staticCanRefresh,
                        prefix: new HarmonyMethod(typeof(MetadataProvidersGuard), nameof(CanRefreshPrefix)));
                }

                if (instanceCanRefresh != null)
                {
                    logger.Info($"MetadataProvidersGuard target instance: {instanceCanRefresh.DeclaringType?.FullName}.{instanceCanRefresh.Name}({string.Join(",", instanceCanRefresh.GetParameters().Select(p => p.ParameterType.Name))})");
                    harmony.Patch(instanceCanRefresh,
                        prefix: new HarmonyMethod(typeof(MetadataProvidersGuard), nameof(CanRefreshPrefix)));
                }
                }
                catch (Exception patchEx)
                {
                    logger.Error("MetadataProvidersGuard patch failed");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    isEnabled = false;
                    return;
                }

                logger.Info("MetadataProvidersGuard installed");
            }
            catch (Exception e)
            {
                logger.Error("MetadataProvidersGuard init failed");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool disableSystemMetadata)
        {
            isEnabled = disableSystemMetadata;
            logger?.Info("MetadataProvidersGuard " + (isEnabled ? "enabled" : "disabled"));
        }

        public static IDisposable Allow()
        {
            GuardCount.Value = GuardCount.Value + 1;
            return new GuardScope();
        }

        private static bool CanRefreshPrefix(ref bool __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (GuardCount.Value == 0)
            {
                __result = false;
                logger?.Info($"MetadataProvidersGuard 拦截 CanRefresh");
                return false;
            }
            
            logger?.Info($"MetadataProvidersGuard 放行 CanRefresh");
            return true;
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

        private sealed class GuardScope : IDisposable
        {
            public void Dispose()
            {
                if (GuardCount.Value > 0)
                {
                    GuardCount.Value--;
                }
            }
        }
    }
}
