MediaInfoKeeper
===============

<p align="center">
  <img src="Resources/ThumbImage.png" alt="MediaInfoKeeper" width="320" />
</p>

目录
----

- [安装](#安装)
- [媒体信息保存流程](#媒体信息保存流程)
- [保存规则](#保存规则)
- [配置项](#配置项)
- [计划任务](#计划任务)
- [感谢](#感谢)

安装
----
支持Emby 4.9.1.80 版本，4.8 不支持，我没做兼容处理。
1. 下载 `MediaInfoKeeper.dll`：<https://github.com/honue/MediaInfoKeeper/releases>
2. 放入 Emby 配置目录中的 `plugins` 目录。
3. 重启 Emby 后在插件页面配置使用。

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
 - 追更媒体库：用于入库触发与删除 JSON 逻辑；留空表示全部。支持多选。
 - 计划任务媒体库：用于计划任务范围；留空表示全部。支持多选。
- 最近入库条目数量：用于“最近条目提取媒体信息”计划任务，默认 100。
- MediaInfo JSON 存储根目录：为空时保存到媒体文件同目录（支持文件夹选择器）。
- 条目移除时删除 JSON：删除条目时移除对应 JSON。

计划任务
--------

- MediaInfoKeeper - 批量提取媒体信息：对计划任务媒体库范围内的存量视频条目执行提取并写入 JSON。
- MediaInfoKeeper - 最近条目提取媒体信息：对计划任务媒体库范围内的最近入库条目执行提取并写入 JSON（数量可配置）。
- MediaInfoKeeper - 导出现有媒体信息：对计划任务媒体库范围内已有 MediaInfo 的条目导出 JSON，无 MediaInfo 则跳过。

感谢
----

项目参考：<https://github.com/sjtuross/StrmAssistant>

