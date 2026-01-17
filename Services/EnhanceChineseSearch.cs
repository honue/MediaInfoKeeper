using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaInfoKeeper.Configuration;
using SQLitePCL.pretty;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public static class EnhanceChineseSearch
    {
        private static readonly Version Ver4830 = new Version("4.8.3.0");
        private static readonly Version Ver4900 = new Version("4.9.0.0");
        private static readonly Version Ver4937 = new Version("4.9.0.37");

        private static Version appVer;
        private static Type raw;
        private static MethodInfo sqlite3_enable_load_extension;
        private static FieldInfo sqlite3_db;
        private static MethodInfo createConnection;
        private static PropertyInfo dbFilePath;
        private static MethodInfo getJoinCommandText;
        private static MethodInfo createSearchTerm;
        private static MethodInfo cacheIdsFromTextParams;

        private static readonly object InitLock = new object();
        private static readonly object PhaseLock = new object();
        private static bool isInitialized;
        private static bool isConnectionPatched;
        private static bool areSearchFunctionsPatched;
        private static bool patchPhase2Initialized;

        private static ILogger logger;
        private static Harmony harmony;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static string tokenizerPath;
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        public static void Initialize(ILogger pluginLogger, EnhanceChineseSearchOptions options)
        {
            if (isInitialized)
            {
                Configure(options);
                return;
            }

            lock (InitLock)
            {
                if (isInitialized)
                {
                    Configure(options);
                    return;
                }

                logger = pluginLogger;
                harmony = new Harmony("mediainfokeeper.search");
                appVer = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);
                tokenizerPath = ResolveTokenizerPath();

                try
                {
                    var sqlitePclEx = Assembly.Load("SQLitePCLRawEx.core");
                    raw = sqlitePclEx.GetType("SQLitePCLEx.raw");
                    sqlite3_enable_load_extension = raw?.GetMethod(
                        "sqlite3_enable_load_extension",
                        BindingFlags.Static | BindingFlags.Public);

                    sqlite3_db = typeof(SQLiteDatabaseConnection)
                        .GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

                    var embySqlite = Assembly.Load("Emby.Sqlite");
                    var baseSqliteRepository = embySqlite.GetType("Emby.Sqlite.BaseSqliteRepository");

                    switch (Environment.OSVersion.Platform)
                    {
                        case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                            createConnection = baseSqliteRepository?.GetMethod(
                                "CreateNewConnection",
                                BindingFlags.NonPublic | BindingFlags.Instance,
                                null,
                                new[] { typeof(bool) },
                                null);
                            break;
                        case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                            createConnection = baseSqliteRepository?.GetMethod(
                                "CreateConnection",
                                BindingFlags.NonPublic | BindingFlags.Instance,
                                null,
                                new[] { typeof(bool), typeof(CancellationToken) },
                                null);
                            break;
                    }

                    dbFilePath = baseSqliteRepository?.GetProperty(
                        "DbFilePath",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                    var sqliteItemRepository =
                        embyServerImplementationsAssembly.GetType(
                            "Emby.Server.Implementations.Data.SqliteItemRepository");
                    getJoinCommandText = sqliteItemRepository?.GetMethod(
                        "GetJoinCommandText",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[]
                        {
                            typeof(InternalItemsQuery),
                            typeof(List<KeyValuePair<string, string>>),
                            typeof(string),
                            typeof(string),
                            typeof(bool)
                        },
                        null);
                    createSearchTerm = sqliteItemRepository?.GetMethod(
                        "CreateSearchTerm",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    cacheIdsFromTextParams = sqliteItemRepository?.GetMethod(
                        "CacheIdsFromTextParams",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                    if (createConnection == null || dbFilePath == null || getJoinCommandText == null ||
                        createSearchTerm == null || cacheIdsFromTextParams == null || sqlite3_db == null ||
                        sqlite3_enable_load_extension == null)
                    {
                        logger?.Warn("增强搜索初始化失败：缺少反射目标。");
                        return;
                    }

                    isInitialized = true;
                }
                catch (Exception e)
                {
                    logger?.Error("增强搜索初始化失败。");
                    logger?.Error(e.ToString());
                }
            }

            Configure(options);
        }

        public static void Configure(EnhanceChineseSearchOptions options)
        {
            if (!isInitialized || options == null)
            {
                return;
            }

            if (!options.EnhanceChineseSearch && !options.EnhanceChineseSearchRestore)
            {
                return;
            }

            if (appVer >= Ver4830)
            {
                PatchPhase1();
            }
            else
            {
                ResetOptions();
            }
        }

        private static string ResolveTokenizerPath()
        {
            var basePath = AppContext.BaseDirectory;
            try
            {
                var appHost = Plugin.Instance?.AppHost;
                if (appHost != null)
                {
                    var applicationPaths = appHost.Resolve<MediaBrowser.Common.Configuration.IApplicationPaths>();
                    if (applicationPaths != null)
                    {
                        basePath = applicationPaths.PluginsPath;
                    }
                }
            }
            catch
            {
                // Fall back to base directory when application paths are unavailable.
            }

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return Path.Combine(basePath, "simple.dll");
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return Path.Combine(basePath, "libsimple");
                default:
                    return Path.Combine(basePath, "simple.dll");
            }
        }

        private static bool CreateConnectionPostfixPlatform()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return PatchCreateConnection(nameof(CreateConnectionPostfixWin));
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return PatchCreateConnection(nameof(CreateConnectionPostfixLinux));
                default:
                    return false;
            }
        }

        private static bool PatchCreateConnection(string postfixName)
        {
            if (isConnectionPatched || harmony == null || createConnection == null)
            {
                return isConnectionPatched;
            }

            try
            {
                var postfix = new HarmonyMethod(typeof(EnhanceChineseSearch), postfixName);
                harmony.Patch(createConnection, postfix: postfix);
                isConnectionPatched = true;
                return true;
            }
            catch (Exception e)
            {
                logger?.Error("EnhanceChineseSearch patch CreateConnection failed.");
                logger?.Error(e.ToString());
                return false;
            }
        }

        private static void PatchPhase1()
        {
            if (EnsureTokenizerExists() && CreateConnectionPostfixPlatform())
            {
                return;
            }

            logger?.Warn("增强搜索 PatchPhase1 失败。");
            ResetOptions();
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            var ftsTableName = appVer >= Ver4830 ? "fts_search9" : "fts_search8";

            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(sql, 'tokenize=""simple""') > 0 THEN 'simple'
                        WHEN instr(sql, 'tokenize=""unicode61 remove_diacritics 2""') > 0 THEN 'unicode61 remove_diacritics 2'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{ftsTableName}';";

            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;

            try
            {
                using (var statement = connection.PrepareStatement(tokenizerCheckQuery))
                {
                    if (statement.MoveNext())
                    {
                        CurrentTokenizerName = statement.Current?.GetString(0) ?? "unknown";
                    }
                }

                logger?.Info("增强搜索 - 当前分词器（前）为 " + CurrentTokenizerName);

                var options = Plugin.Instance?.Options?.EnhanceChineseSearch;
                if (options != null && !string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
                {
                    if (options.EnhanceChineseSearchRestore)
                    {
                        if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                        {
                            rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                        }

                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            logger?.Info("增强搜索 - 恢复成功");
                        }

                        ResetOptions();
                    }
                    else if (options.EnhanceChineseSearch)
                    {
                        patchSearchFunctionsResult = PatchSearchFunctions();

                        if (patchSearchFunctionsResult)
                        {
                            if (string.Equals(CurrentTokenizerName, "unicode61 remove_diacritics 2", StringComparison.Ordinal))
                            {
                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "simple");
                            }

                            if (rebuildFtsResult)
                            {
                                CurrentTokenizerName = "simple";
                                logger?.Info("增强搜索 - 加载成功");
                            }
                        }
                    }
                }

                logger?.Info("增强搜索 - 当前分词器（后）为 " + CurrentTokenizerName);
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - PatchPhase2 失败。");
                logger?.Warn(e.ToString());
            }

            if (!patchSearchFunctionsResult || !rebuildFtsResult ||
                string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
            {
                ResetOptions();
            }
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            string populateQuery;

            if (appVer < Ver4900)
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization("Album") +
                    " from MediaItems";
            }
            else
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization(
                        "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                    " from MediaItems";
            }

            connection.BeginTransaction(TransactionMode.Deferred);
            try
            {
                connection.Execute($"DROP TABLE IF EXISTS {ftsTableName}");

                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')";
                connection.Execute(createFtsTableQuery);

                logger?.Info($"增强搜索 - 填充 {ftsTableName} 开始");

                connection.Execute(populateQuery);
                connection.CommitTransaction();

                logger?.Info($"增强搜索 - 填充 {ftsTableName} 完成");

                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();
                logger?.Warn("增强搜索 - 重建 FTS 失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(" + columnName + ",'''',''),'.','')";
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (resourceName == null || expectedSha1 == null)
            {
                return false;
            }

            try
            {
                if (File.Exists(tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(tokenizerPath);
                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 != highestSha1)
                        {
                            ExportTokenizer(resourceName);
                        }

                        return true;
                    }

                    return true;
                }

                ExportTokenizer(resourceName);
                return true;
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 检查分词器失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    logger?.Warn("增强搜索 - 未找到分词器资源: " + resourceName);
                    return;
                }

                using (var fileStream = new FileStream(tokenizerPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        private static string GetTokenizerResourceName()
        {
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            var winSimpleTokenizer = $"{tokenizerNamespace}.win.simple.dll";
            var linuxSimpleTokenizer = $"{tokenizerNamespace}.linux.libsimple.so";

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return winSimpleTokenizer;
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return linuxSimpleTokenizer;
                default:
                    return null;
            }
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "a83d90af9fb88e75a1ddf2436c8b67954c761c83" },
                        { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" },
                        { new Version(0, 5, 2), "338bb0915d6f4625b54f041bdeb6791b6e590c4e" },
                        { new Version(0, 5, 3), "338bb0915d6f4625b54f041bdeb6791b6e590c4e" }
                    };
                case PlatformID.Unix:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "f7fb8ba0b98e358dfaa87570dc3426ee7f00e1b6" },
                        { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" },
                        { new Version(0, 5, 2), "e89eeb7938894e4e8b284896285e7dc90da715bc" },
                        { new Version(0, 5, 3), "a6188af48c0fef201cb24dbebc65c4cf5b4ddf9b" }
                    };
                default:
                    return null;
            }
        }

        private static string ComputeSha1(string filePath)
        {
            using (var sha1 = SHA1.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha1.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void ResetOptions()
        {
            var options = Plugin.Instance?.OptionsStore?.GetOptions();
            if (options?.EnhanceChineseSearch == null)
            {
                return;
            }

            options.EnhanceChineseSearch.EnhanceChineseSearch = false;
            options.EnhanceChineseSearch.EnhanceChineseSearchRestore = false;
            Plugin.Instance.OptionsStore.SetOptions(options);
        }

        private static bool PatchSearchFunctions()
        {
            if (areSearchFunctionsPatched || harmony == null)
            {
                return areSearchFunctionsPatched;
            }

            try
            {
                harmony.Patch(getJoinCommandText,
                    postfix: new HarmonyMethod(typeof(EnhanceChineseSearch), nameof(GetJoinCommandTextPostfix)));
                harmony.Patch(createSearchTerm,
                    prefix: new HarmonyMethod(typeof(EnhanceChineseSearch), nameof(CreateSearchTermPrefix)));
                harmony.Patch(cacheIdsFromTextParams,
                    prefix: new HarmonyMethod(typeof(EnhanceChineseSearch), nameof(CacheIdsFromTextParamsPrefix)));

                areSearchFunctionsPatched = true;
                return true;
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 补丁搜索函数失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            try
            {
                var db = sqlite3_db.GetValue(connection);
                sqlite3_enable_load_extension.Invoke(raw, new[] { db, 1 });
                connection.Execute("SELECT load_extension('" + tokenizerPath + "')");
                return true;
            }
            catch (SQLiteException ex)
            {
                logger?.Error("增强搜索 - 加载扩展失败: " + ex.Message);
                logger?.Error(ex.StackTrace);
            }
            catch (Exception e)
            {
                logger?.Warn("增强搜索 - 加载分词器失败。");
                logger?.Warn(e.ToString());
            }

            return false;
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixWin(
            object __instance,
            [HarmonyArgument("isReadOnly")] bool isReadOnly,
            ref IDatabaseConnection __result)
        {
            if (patchPhase2Initialized)
            {
                return;
            }

            lock (PhaseLock)
            {
                if (patchPhase2Initialized)
                {
                    return;
                }

                var db = dbFilePath.GetValue(__instance) as string;
                if (db?.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return;
                }

                var tokenizerLoaded = LoadTokenizerExtension(__result);
                if (tokenizerLoaded)
                {
                    patchPhase2Initialized = true;
                    PatchPhase2(__result);
                }
            }
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfixLinux(
            object __instance,
            [HarmonyArgument("isReadOnly")] bool isReadOnly,
            [HarmonyArgument("cancellationToken")] CancellationToken cancellationToken,
            ref IDatabaseConnection __result)
        {
            if (isReadOnly || patchPhase2Initialized)
            {
                return;
            }

            lock (PhaseLock)
            {
                if (patchPhase2Initialized)
                {
                    return;
                }

                var db = dbFilePath.GetValue(__instance) as string;
                if (db?.EndsWith("library.db", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return;
                }

                var tokenizerLoaded = LoadTokenizerExtension(__result);
                if (tokenizerLoaded)
                {
                    patchPhase2Initialized = true;
                    PatchPhase2(__result);
                }
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(
            InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams,
            string mediaItemsTableQualifier,
            ref StringBuilder __result)
        {
            var sql = __result.ToString();
            var newSql = sql;

            var hasMatchParam =
                newSql.IndexOf("match @SearchTerm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(newSql, @"\bmatch\b\s*\(?\s*@SearchTerm\b", RegexOptions.IgnoreCase);

            if (!string.IsNullOrEmpty(query.SearchTerm) && hasMatchParam)
            {
                var options = Plugin.Instance?.Options?.EnhanceChineseSearch;
                var excludeOriginalTitle = options?.ExcludeOriginalTitleFromSearch == true;
                var replacement = excludeOriginalTitle
                    ? "match '-OriginalTitle:' || simple_query(@SearchTerm)"
                    : "match simple_query(@SearchTerm)";

                newSql = Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    replacement,
                    RegexOptions.IgnoreCase);
            }

            if (!string.IsNullOrEmpty(query.Name) && hasMatchParam)
            {
                newSql = Regex.Replace(
                    newSql,
                    @"\bmatch\b\s*\(?\s*@SearchTerm\b",
                    "match 'Name:' || simple_query(@SearchTerm)",
                    RegexOptions.IgnoreCase);

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;
                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }

                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }

            if (!string.Equals(sql, newSql, StringComparison.Ordinal))
            {
                __result.Clear().Append(newSql);
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(string searchTerm, ref string __result)
        {
            __result = searchTerm.Replace(".", string.Empty).Replace("'", string.Empty);
            return false;
        }

        [HarmonyPrefix]
        private static bool CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            if ((query.PersonTypes?.Length ?? 0) == 0)
            {
                var nameStartsWith = query.NameStartsWith;
                if (!string.IsNullOrEmpty(nameStartsWith))
                {
                    query.SearchTerm = nameStartsWith;
                    query.NameStartsWith = null;
                }

                var searchTerm = query.SearchTerm;
                if (query.IncludeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
                {
                    query.IncludeItemTypes = SearchScopeUtility.GetSearchScope();
                }

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    foreach (var provider in patterns)
                    {
                        var match = provider.Value.Match(searchTerm.Trim());
                        if (match.Success)
                        {
                            var idValue = provider.Key == "imdb" ? match.Value : match.Groups[2].Value;

                            query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                            {
                                new KeyValuePair<string, string>(provider.Key, idValue)
                            };
                            query.SearchTerm = null;
                            break;
                        }
                    }
                }

                if (appVer >= Ver4937 && !string.IsNullOrEmpty(query.SearchTerm))
                {
                    LoadTokenizerExtension(db);
                }
            }

            return true;
        }
    }
}
