#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class EmbyDllMethodsScanTask : IScheduledTask
    {
        private const BindingFlags MethodBindingFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private static readonly string[] DefaultDllNameBlacklistPrefixes = { "Microsoft.", "System.", "netstandard", "mscorlib" };

        private readonly IApplicationHost applicationHost;
        private readonly ILogger logger;

        public EmbyDllMethodsScanTask(ILogManager logManager, IApplicationHost applicationHost)
        {
            this.applicationHost = applicationHost;
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperEmbyDllMethodsScanTask";

        public string Name => "00.导出Emby DLL 方法";

        public string Description => "扫描 Emby /system 下 DLL 并导出方法清单到 TXT。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            var exportRoot = Plugin.Instance.Options.GetMediaInfoOptions().MediaInfoJsonRootFolder?.Trim();
            if (string.IsNullOrWhiteSpace(exportRoot))
            {
                throw new InvalidOperationException("请先在插件配置中设置“MediaInfo JSON 存储根目录”(MediaInfoJsonRootFolder)。");
            }

            Directory.CreateDirectory(exportRoot);

            var appPaths = applicationHost.Resolve<IApplicationPaths>();
            var systemPath = ResolveSystemPath(appPaths);
            if (!Directory.Exists(systemPath))
            {
                throw new DirectoryNotFoundException($"未找到 Emby /system 目录: {systemPath}");
            }

            var allDllFiles = Directory.GetFiles(systemPath, "*.dll", SearchOption.AllDirectories)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var whitelistPrefixes = GetWhitelistPrefixes();
            var blacklistPrefixes = GetBlacklistPrefixes();

            foreach (var dll in allDllFiles)
            {
                logger.Info("发现 DLL: {0} {1}", dll, ShouldExportDll(dll, whitelistPrefixes, blacklistPrefixes) ? "(will export)" : "(will skip)");
            }

            var dllFiles = allDllFiles
                .Where(path => ShouldExportDll(path, whitelistPrefixes, blacklistPrefixes))
                .ToArray();
            var filteredCount = allDllFiles.Length - dllFiles.Length;

            var embyVersion = Plugin.Instance?.AppHost?.ApplicationVersion?.ToString() ?? "unknown";
            var safeVersion = SanitizeForFileName(embyVersion);
            var outputDirectory = Path.Combine(exportRoot, $"emby_{safeVersion}");
            Directory.CreateDirectory(outputDirectory);
            logger.Info("开始导出 /system DLL 方法: totalDll={0}, filteredDll={1}, exportingDll={2}, outputDir={3}",
                allDllFiles.Length, filteredCount, dllFiles.Length, outputDirectory);

            var resolverFiles = dllFiles
                .Concat(GetRuntimeAssemblies())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (dllFiles.Length == 0)
            {
                progress.Report(100);
                logger.Info("未发现 DLL，未生成导出文件: {0}", outputDirectory);
                return;
            }

            var loadContext = new DllScanLoadContext(resolverFiles);
            try
            {
                for (var i = 0; i < dllFiles.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dllPath = dllFiles[i];

                    try
                    {
                        var assembly = loadContext.LoadFromAssemblyPath(dllPath);
                        var dllName = Path.GetFileName(dllPath);
                        var dllVersion = assembly.GetName().Version?.ToString() ?? "Unknown";
                        var safeDllVersion = SanitizeForFileName(dllVersion);
                        var safeDllName = SanitizeForFileName(Path.GetFileNameWithoutExtension(dllPath));
                        var outputFileName = $"{safeDllName}_{safeDllVersion}_methods.txt";
                        var outputFilePath = Path.Combine(outputDirectory, outputFileName);
                        var exportedMethods = 0;
                        var types = SafeGetTypes(assembly)
                            .OrderBy(type => type.FullName ?? type.Name, StringComparer.Ordinal);
                        using var writer = new StreamWriter(outputFilePath, false, new UTF8Encoding(false));
                        writer.WriteLine($"DLL_NAME: {dllName}");
                        writer.WriteLine($"DLL_VERSION: {dllVersion}");
                        writer.WriteLine();

                        foreach (var type in types)
                        {
                            ConstructorInfo[] constructors;
                            MethodInfo[] methods;
                            try
                            {
                                constructors = type.GetConstructors(MethodBindingFlags);
                                methods = type.GetMethods(MethodBindingFlags);
                            }
                            catch (Exception ex)
                            {
                                logger.Warn("扫描 Type 失败: {0}, error={1}", type.FullName ?? type.Name, ex.Message);
                                continue;
                            }

                            foreach (var constructor in constructors.OrderBy(item => item.ToString(), StringComparer.Ordinal))
                            {
                                try
                                {
                                    WriteMethodBlock(writer, dllName, type, constructor);
                                    exportedMethods++;
                                }
                                catch (Exception ex)
                                {
                                    logger.Warn("扫描 Constructor 失败: {0}.{1}, error={2}", type.FullName ?? type.Name, constructor.Name, ex.Message);
                                }
                            }

                            foreach (var method in methods.OrderBy(item => item.Name, StringComparer.Ordinal))
                            {
                                try
                                {
                                    WriteMethodBlock(writer, dllName, type, method);
                                    exportedMethods++;
                                }
                                catch (Exception ex)
                                {
                                    logger.Warn("扫描 Method 失败: {0}.{1}, error={2}", type.FullName ?? type.Name, method.Name, ex.Message);
                                }
                            }
                        }

                        logger.Info("DLL 导出成功: {0}, version={1}, methods={2}, file={3}", dllPath, dllVersion, exportedMethods, outputFilePath);
                    }
                    catch (Exception ex)
                    {
                        logger.Info("跳过不可映射 DLL: {0}, error={1}", dllPath, ex.Message);
                    }
                    progress.Report((i + 1) / (double)dllFiles.Length * 100);
                }
            }
            finally
            {
                loadContext.Unload();
            }

            progress.Report(100);
            logger.Info("导出完成: {0}", outputDirectory);
        }

        private static string ResolveSystemPath(IApplicationPaths paths)
        {
            var candidate = TryGetStringProperty(paths, "ProgramSystemPath");
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            candidate = TryGetStringProperty(paths, "ApplicationSystemPath");
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            return "/system";
        }

        private static string TryGetStringProperty(object target, string propertyName)
        {
            if (target == null)
            {
                return null;
            }

            var prop = target.GetType().GetProperty(propertyName);
            if (prop == null)
            {
                return null;
            }

            return prop.GetValue(target) as string;
        }

        private static IEnumerable<string> GetRuntimeAssemblies()
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(runtimeDir, "*.dll", SearchOption.TopDirectoryOnly);
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes().Where(type => type != null);
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
        }

        private static void WriteMethodBlock(TextWriter writer, string dllName, Type declaringType, MethodBase method)
        {
            ParameterInfo[] parameters;
            try
            {
                parameters = method.GetParameters();
            }
            catch
            {
                parameters = Array.Empty<ParameterInfo>();
            }

            var returnTypeText = "Unknown";
            try
            {
                if (method is ConstructorInfo)
                {
                    returnTypeText = "(constructor)";
                }
                else if (method is MethodInfo currentMethodInfo)
                {
                    returnTypeText = GetParameterTypeFullName(currentMethodInfo.ReturnType);
                }
            }
            catch
            {
                returnTypeText = "Unknown";
            }

            writer.WriteLine("====================================================");
            writer.WriteLine($"TYPE: {declaringType.FullName ?? declaringType.Name}");
            writer.WriteLine();
            writer.WriteLine($"METHOD: {method.Name}");
            writer.WriteLine();
            writer.WriteLine("PARAMETERS:");
            if (parameters.Length == 0)
            {
                writer.WriteLine("    (none)");
            }
            else
            {
                foreach (var parameter in parameters)
                {
                    writer.WriteLine($"    {GetParameterTypeFullName(parameter.ParameterType)}");
                }
            }
            writer.WriteLine();
            writer.WriteLine($"RETURN_TYPE: {returnTypeText}");
            writer.WriteLine($"STATIC: {method.IsStatic}");
            writer.WriteLine($"CONSTRUCTOR: {method is ConstructorInfo}");
            writer.WriteLine($"GENERIC: {(method is MethodInfo methodInfo ? methodInfo.GetGenericArguments().Length : 0)}");
            writer.WriteLine($"ASYNC: {IsAsyncMethod(method)}");
            writer.WriteLine($"PROPERTY_ACCESSOR: {IsPropertyAccessor(method)}");
            writer.WriteLine("====================================================");
            writer.WriteLine();
        }

        private static string GetParameterTypeFullName(Type parameterType)
        {
            if (parameterType == null)
            {
                return "Unknown";
            }

            return parameterType.FullName ?? parameterType.ToString();
        }

        private static bool IsAsyncMethod(MethodBase method)
        {
            try
            {
                return method is MethodInfo methodInfo &&
                       methodInfo.GetCustomAttributes(typeof(AsyncStateMachineAttribute), false).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPropertyAccessor(MethodBase method)
        {
            return method.IsSpecialName &&
                   (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                    method.Name.StartsWith("set_", StringComparison.Ordinal));
        }

        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var buffer = value
                .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
                .ToArray();
            return new string(buffer);
        }

        private static bool IsBlacklistedDll(string dllPath, IReadOnlyList<string> blacklistPrefixes)
        {
            var fileName = Path.GetFileName(dllPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            if (blacklistPrefixes == null || blacklistPrefixes.Count == 0)
            {
                return false;
            }

            foreach (var prefix in blacklistPrefixes)
            {
                if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsWhitelistedDll(string dllPath, IReadOnlyList<string> whitelistPrefixes)
        {
            if (whitelistPrefixes == null || whitelistPrefixes.Count == 0)
            {
                return false;
            }

            var fileName = Path.GetFileName(dllPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            foreach (var prefix in whitelistPrefixes)
            {
                if (!string.IsNullOrWhiteSpace(prefix) &&
                    fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldExportDll(string dllPath, IReadOnlyList<string> whitelistPrefixes, IReadOnlyList<string> blacklistPrefixes)
        {
            var whitelisted = IsWhitelistedDll(dllPath, whitelistPrefixes);
            if (whitelisted)
            {
                return true;
            }
            return !IsBlacklistedDll(dllPath, blacklistPrefixes);
        }

        private static string[] GetWhitelistPrefixes()
        {
            var raw = Plugin.Instance?.Options?.Debug?.DllNameWhitelistPrefixes;
            return ParsePrefixList(raw);
        }

        private static string[] GetBlacklistPrefixes()
        {
            var raw = Plugin.Instance?.Options?.Debug?.DllNameBlacklistPrefixes;
            var parsed = ParsePrefixList(raw);
            return parsed.Length == 0 ? DefaultDllNameBlacklistPrefixes : parsed;
        }

        private static string[] ParsePrefixList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            return raw
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private sealed class DllScanLoadContext : AssemblyLoadContext
        {
            private readonly Dictionary<string, string> assemblyPathMap;

            public DllScanLoadContext(IEnumerable<string> assemblyPaths)
                : base(isCollectible: true)
            {
                assemblyPathMap = assemblyPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new
                    {
                        Name = Path.GetFileNameWithoutExtension(path),
                        Path = path
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                if (assemblyName?.Name == null)
                {
                    return null;
                }

                if (assemblyPathMap.TryGetValue(assemblyName.Name, out var path) && File.Exists(path))
                {
                    return LoadFromAssemblyPath(path);
                }

                return null;
            }
        }
    }
}
#endif
