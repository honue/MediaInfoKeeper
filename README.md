MediaInfoKeeper
===============

媒体信息保存流程
--------------

MediaInfoKeeper 的目标是把媒体信息保存为 JSON，在需要时快速恢复，减少首次播放或批量刷新时的提取成本。

当前流程
--------

1. 触发
- 新媒体入库触发处理（ItemAdded）。
- 仅处理视频条目。

2. 恢复优先
- 若启用“MediaInfo 保存与恢复”，先尝试从 JSON 恢复。
- 恢复成功则跳过提取。

3. 提取并保存
- 若没有 MediaInfo 且 JSON 无法恢复，执行一次媒体信息提取。
- 提取完成后写入 JSON。

4. 已有 MediaInfo
- 若条目已经有 MediaInfo，直接覆盖写入 JSON。

5. 条目移除
- 若启用“条目移除时删除 JSON”，删除对应 JSON 文件。

保存规则
--------

- JSON 文件名：默认在媒体同目录下以 {FileName}-mediainfo.json 保存。
- 若配置了“MediaInfo JSON 存储根目录”，则按盘符根目录的相对路径写入该根目录。
- 保存前会清理部分字段（如 MediaSourceInfo.Id、ItemId、Path）。
- 外挂字幕仅保存文件名，恢复时再拼接为绝对路径。

配置项
------

- 启用 MediaInfo：启用后优先恢复，提取后写入 JSON。
- MediaInfo JSON 存储根目录：为空时保存到媒体文件同目录（支持文件夹选择器）。
- 条目移除时删除 JSON：删除条目时移除对应 JSON。

相关 API（实现用到的 Emby 接口）
----------------------------

提取与刷新
- `IProviderManager.RefreshSingleItem(...)`：触发媒体信息提取。
- `MetadataRefreshOptions`：刷新策略，关闭远程元数据/图片抓取。
- `ILibraryManager.GetLibraryOptions(...)`：获取库配置并构造最小化选项。

媒体源与流信息
- `BaseItem.GetMediaSources(...)`：获取 MediaSourceInfo。
- `MediaSourceInfo.MediaStreams`：音视频流与字幕流信息。

章节与写回
- `IItemRepository.GetChapters(...)` / `SaveChapters(...)`：读写章节信息。
- `IItemRepository.SaveMediaStreams(...)`：写回媒体流信息。
- `ILibraryManager.UpdateItems(...)`：更新条目基础字段（时长、大小、码率、分辨率等）。

持久化与恢复
- `IJsonSerializer.SerializeToFile(...)` / `DeserializeFromFileAsync(...)`：JSON 保存与恢复。
- `IFileSystem` / `IDirectoryService`：文件读写、路径处理与存在性检查。
