using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Emby.Web.GenericEdit;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using JsonSerializerOptions = MediaBrowser.Model.Serialization.JsonSerializerOptions;

namespace MediaInfoKeeper.Options.UIBaseClasses.Store {
    internal class SimpleFileStore<TOptionType> : SimpleContentStore<TOptionType>
        where TOptionType : EditableOptionsBase, new() {
        private static readonly HashSet<string> NonPersistentPropertyNames = new(StringComparer.Ordinal) {
            "EditorTitle",
            "EditorDescription",
            "FeatureRequiresPremiere",
            "IsNewItem",
            "ItemAddedTaskEntries",
            "ScheduledTaskEntries",
            "ScraperEntries",
            "RefreshQueueStatus",
            "ShowRefreshQueueStatus",
            "LibraryList",
            "SubsequentMarkerModeList",
            "SearchItemTypeList",
            "ChineseSearchTokenizerStatus",
            "ShowChineseSearchTokenizerStatus",
            "OptimizeDatabaseButton",
            "HidePersonOptionList",
            "FallbackLanguageList",
            "TvdbFallbackLanguageList",
            "UpdateChannelList",
            "ProjectUrl",
            "VersionStatus",
            "ReleaseHistoryBody",
            "UpdatePluginProjectUrl",
            "UpdatePluginVersionStatus",
            "UpdatePluginReleaseHistoryBody",
            "DebugMediaInfoUrl",
            "ProxyLatencyStatus",
            "ShowProxyLatencyStatus",
            "TmdbReplacementStatus",
            "ShowTmdbReplacementStatus"
        };

        private readonly IFileSystem fileSystem;
        private readonly IJsonSerializer jsonSerializer;
        private readonly object lockObj = new();

        private readonly ILogger logger;
        private readonly string pluginconfigPath;
        private readonly string pluginFullName;
        private TOptionType options;

        public SimpleFileStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName) {
            this.logger = logger;
            this.pluginFullName = pluginFullName;
            jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            fileSystem = applicationHost.Resolve<IFileSystem>();

            var applicationPaths = applicationHost.Resolve<IApplicationPaths>();
            pluginconfigPath = applicationPaths.PluginConfigurationsPath;

            if (!fileSystem.DirectoryExists(pluginconfigPath)) fileSystem.CreateDirectory(pluginconfigPath);

            OptionsFileName = string.Format("{0}.json", pluginFullName);
        }

        public virtual string OptionsFileName { get; }

        public string OptionsFilePath => Path.Combine(pluginconfigPath, OptionsFileName);

        public event EventHandler<FileSavingEventArgs> FileSaving;

        public event EventHandler<FileSavedEventArgs> FileSaved;

        public override TOptionType GetOptions() {
            lock (lockObj) {
                if (options == null) return ReloadOptions();

                return options;
            }
        }

        public TOptionType LoadOptionsFromDisk() {
            lock (lockObj) {
                return LoadOptionsFromDiskCore() ?? new TOptionType();
            }
        }

        public TOptionType ReloadOptions() {
            lock (lockObj) {
                options = LoadOptionsFromDiskCore() ?? new TOptionType();
                return options ?? new TOptionType();
            }
        }

        private TOptionType LoadOptionsFromDiskCore() {
            var tempOptions = new TOptionType();

            try {
                if (!fileSystem.FileExists(OptionsFilePath)) return tempOptions;

                using (var stream = fileSystem.OpenRead(OptionsFilePath)) {
                    JsonNode rootNode = null;
                    try {
                        rootNode = JsonNode.Parse(stream);
                    }
                    catch (Exception ex) {
                        logger.Warn("无法解析配置 JSON，回退为原始反序列化结果：{0}", ex.Message);
                    }

                    if (rootNode != null) {
                        rootNode = TransformLoadedJson(rootNode) ?? rootNode;
                        using var transformedStream = new MemoryStream();
                        using (var writer =
                               new Utf8JsonWriter(transformedStream, new JsonWriterOptions { Indented = true })) {
                            rootNode.WriteTo(writer);
                            writer.Flush();
                        }

                        transformedStream.Position = 0;
                        var transformed = tempOptions.DeserializeFromJsonStream(transformedStream, jsonSerializer);
                        return transformed as TOptionType ?? tempOptions;
                    }

                    stream.Position = 0;
                    var deserialized = tempOptions.DeserializeFromJsonStream(stream, jsonSerializer);
                    return deserialized as TOptionType ?? tempOptions;
                }
            }
            catch (Exception ex) {
                logger.ErrorException("Error loading plugin options for {0} from {1}", ex, pluginFullName,
                    OptionsFilePath);
                return tempOptions;
            }
        }

        public override void SetOptions(TOptionType newOptions) {
            SetOptionsInternal(newOptions, true);
        }

        protected void SetOptionsSilently(TOptionType newOptions) {
            SetOptionsInternal(newOptions, false);
        }

        private void SetOptionsInternal(TOptionType newOptions, bool raiseEvents) {
            if (newOptions == null) throw new ArgumentNullException(nameof(newOptions));

            if (raiseEvents) {
                var savingArgs = new FileSavingEventArgs(newOptions);
                FileSaving?.Invoke(this, savingArgs);

                if (savingArgs.Cancel) return;
            }

            lock (lockObj) {
                using (var stream =
                       fileSystem.GetFileStream(OptionsFilePath, FileOpenMode.Create, FileAccessMode.Write)) {
                    WriteSanitizedOptions(stream, newOptions);
                }
            }

            lock (lockObj) {
                options = newOptions;
            }

            if (raiseEvents) {
                var savedArgs = new FileSavedEventArgs(newOptions);
                FileSaved?.Invoke(this, savedArgs);
            }
        }

        private void WriteSanitizedOptions(Stream destination, TOptionType options) {
            using var buffer = new MemoryStream();
            jsonSerializer.SerializeToStream(
                options,
                buffer,
                new JsonSerializerOptions { Indent = true });

            buffer.Position = 0;
            JsonNode rootNode = null;
            try {
                rootNode = JsonNode.Parse(buffer);
            }
            catch (Exception ex) {
                logger.Warn("无法解析配置 JSON，回退为原始序列化结果：{0}", ex.Message);
            }

            if (rootNode == null) {
                buffer.Position = 0;
                buffer.CopyTo(destination);
                return;
            }

            rootNode = TransformSavingJson(rootNode, options) ?? rootNode;
            SanitizeJsonNode(rootNode);
            using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
            rootNode.WriteTo(writer);
            writer.Flush();
        }

        protected virtual JsonNode TransformLoadedJson(JsonNode rootNode) {
            return rootNode;
        }

        protected virtual JsonNode TransformSavingJson(JsonNode rootNode, TOptionType options) {
            return rootNode;
        }

        private static void SanitizeJsonNode(JsonNode node) {
            if (node is JsonObject jsonObject) {
                var propertyNames = new List<string>();
                foreach (var property in jsonObject) propertyNames.Add(property.Key);

                foreach (var propertyName in propertyNames) {
                    if (NonPersistentPropertyNames.Contains(propertyName)) {
                        jsonObject.Remove(propertyName);
                        continue;
                    }

                    var childNode = jsonObject[propertyName];
                    if (childNode != null) SanitizeJsonNode(childNode);
                }

                return;
            }

            if (node is JsonArray jsonArray)
                foreach (var childNode in jsonArray)
                    if (childNode != null)
                        SanitizeJsonNode(childNode);
        }
    }
}
