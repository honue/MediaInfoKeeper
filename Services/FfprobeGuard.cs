using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 拦截 Emby 内置 ffprobe，只允许插件显式放行。
    /// </summary>
    public static class FfprobeGuard
    {
        private static readonly AsyncLocal<int> GuardCount = new AsyncLocal<int>();

        private static Harmony harmony;
        private static MethodInfo runFfProcess;
        private static PropertyInfo standardOutput;
        private static PropertyInfo standardError;
        private static object emptyResult;
        private static ILogger logger;
        private static bool isEnabled;

        public static void Initialize(ILogger pluginLogger, bool disableSystemFfprobe)
        {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = disableSystemFfprobe;

            try
            {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                if (mediaEncoding == null)
                {
                    logger.Warn("MediaInfoKeeper ffprobe guard init skipped: Emby.Server.MediaEncoding not found");
                    return;
                }

                var mediaProbeManager = mediaEncoding.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                if (mediaProbeManager == null)
                {
                    logger.Warn("MediaInfoKeeper ffprobe guard init skipped: MediaProbeManager type not found");
                    return;
                }

                // Resolve targets before creating Harmony so we can log candidate signatures
                runFfProcess = FindMethod(mediaProbeManager, "RunFfProcess");

                var processRun = Assembly.Load("Emby.ProcessRun");
                var processResult = processRun?.GetType("Emby.ProcessRun.Common.ProcessResult");
                standardOutput = FindProperty(processResult, "StandardOutput");
                standardError = FindProperty(processResult, "StandardError");

                emptyResult = CreateEmptyResult(runFfProcess?.ReturnType);

                if (runFfProcess == null || emptyResult == null)
                {
                    logger.Warn("MediaInfoKeeper ffprobe guard init failed: target method not found or unsupported return type");
                    return;
                }

                logger.Info($"MediaInfoKeeper ffprobe guard target: {runFfProcess.DeclaringType?.FullName}.{runFfProcess.Name}({string.Join(",", runFfProcess.GetParameters().Select(p => p.ParameterType.Name))}) -> {runFfProcess.ReturnType?.FullName}");

                harmony = new Harmony("mediainfokeeper.ffprobe");

                try
                {
                    harmony.Patch(runFfProcess,
                        prefix: new HarmonyMethod(typeof(FfprobeGuard), nameof(RunFfProcessPrefix)),
                        postfix: new HarmonyMethod(typeof(FfprobeGuard), nameof(RunFfProcessPostfix)));
                }
                catch (Exception patchEx)
                {
                    logger.Error("MediaInfoKeeper ffprobe guard patch failed");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    isEnabled = false;
                    return;
                }

                logger.Info("MediaInfoKeeper ffprobe guard installed");
            }
            catch (Exception e)
            {
                logger.Error("MediaInfoKeeper ffprobe guard init failed");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
                isEnabled = false;
                logger.Warn("MediaInfoKeeper ffprobe guard disabled due to initialization failure; ffprobe will not be intercepted.");
            }
        }

        public static void Configure(bool disableSystemFfprobe)
        {
            isEnabled = disableSystemFfprobe;
            logger?.Info("MediaInfoKeeper ffprobe guard " + (isEnabled ? "enabled" : "disabled"));
        }

        /// <summary>
        /// 创建放行作用域，插件内部调用 ffprobe 时使用。
        /// </summary>
        public static IDisposable Allow()
        {
            GuardCount.Value = GuardCount.Value + 1;
            return new GuardScope();
        }

        private static bool RunFfProcessPrefix(object __instance, object __0, string __1, string __2,
            ref int __3, CancellationToken __4, ref object __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (GuardCount.Value == 0)
            {
                logger?.Info($"ffprobe 拦截: {__1} {__2}");
                __result = emptyResult;
                return false;
            }

            logger?.Info($"ffprobe 允许执行: {__1} {__2}");
            __3 = Math.Max(__3, 60000);
            return true;
        }

        private static void RunFfProcessPostfix(ref object __result)
        {
            if (__result is Task task)
            {
                var result = task.GetType().GetProperty("Result")?.GetValue(task);
                if (result == null) return;

                var stdout = standardOutput?.GetValue(result) as string;
                var stderr = standardError?.GetValue(result) as string;
                if (stdout == null || stderr == null) return;

                var trimmed = new string((stdout ?? string.Empty)
                    .Where(c => !char.IsWhiteSpace(c))
                    .ToArray());
                if (!string.Equals(trimmed, "{}", StringComparison.Ordinal)) return;

                var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                var message = lines[lines.Length - 1].Trim();
                if (!string.IsNullOrEmpty(message))
                {
                    logger.Error("ffprobe error: " + message);
                }
            }
        }

        private static object CreateEmptyResult(Type returnType)
        {
            if (returnType == null) return null;

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                object payload = null;

                try
                {
                    payload = Activator.CreateInstance(resultType, nonPublic: true);
                    if (payload != null)
                    {
                        try { standardOutput?.SetValue(payload, "{}"); }
                        catch { /* best-effort stub */ }
                        try { standardError?.SetValue(payload, "ffprobe suppressed by MediaInfoKeeper"); }
                        catch { /* best-effort stub */ }
                    }
                }
                catch (Exception e)
                {
                    logger?.Debug(e.Message);
                    logger?.Debug(e.ToString());
                }

                var fromResult = typeof(Task).GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .First(m => m.Name == nameof(Task.FromResult) && m.IsGenericMethod)
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, new[] { payload });
            }

            return null;
        }

        private static MethodInfo FindMethod(Type type, string methodName, Func<MethodInfo, bool> predicate = null)
        {
            if (type == null) return null;

            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                          BindingFlags.NonPublic)
                .Where(m => m.Name == methodName);

            if (predicate != null) methods = methods.Where(predicate);

            var methodInfo = methods.FirstOrDefault();
            if (methodInfo == null)
            {
                LogCandidates(type, methodName);
            }

            return methodInfo;
        }

        private static PropertyInfo FindProperty(Type type, string propertyName)
        {
            var property = type?.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static |
                                                         BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
            {
                LogPropertyCandidates(type, propertyName);
            }

            return property;
        }

        private static void LogCandidates(Type type, string methodName)
        {
            try
            {
                var candidates = type?.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                  BindingFlags.NonPublic)
                    .Where(m => m.Name == methodName)
                    .Select(m =>
                        $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) -> {m.ReturnType?.Name}");

                logger?.Info($"{type?.FullName}.{methodName} candidates: {string.Join("; ", candidates ?? Enumerable.Empty<string>())}");
            }
            catch (Exception e)
            {
                logger?.Debug(e.Message);
                logger?.Debug(e.StackTrace);
            }
        }

        private static void LogPropertyCandidates(Type type, string propertyName)
        {
            try
            {
                var candidates = type?.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                     BindingFlags.NonPublic)
                    .Where(p => p.Name == propertyName)
                    .Select(p => $"{p.PropertyType?.Name} {p.Name}");

                logger?.Info($"{type?.FullName}.{propertyName} property candidates: {string.Join("; ", candidates ?? Enumerable.Empty<string>())}");
            }
            catch (Exception e)
            {
                logger?.Debug(e.Message);
                logger?.Debug(e.StackTrace);
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
