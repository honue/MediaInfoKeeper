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
支持Emby 4.9.1.80 及以上版本，4.8 不支持，我没做兼容处理。
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

配置项
------

- 启用 MediaInfo：启用后优先恢复，提取后写入 JSON。
- MediaInfo JSON 存储根目录：为空时保存到媒体文件同目录（支持文件夹选择器）。
- 条目移除时删除 JSON：删除条目时移除对应 JSON。
- 禁用 Emby 系统 ffprobe：仅插件内部允许调用。
- 禁用 Emby 系统元数据刷新：仅插件内部允许调用。
- 显示 MetadataProvidersGuard 日志：记录 CanRefresh 拦截/放行日志，默认关闭。
- 追更媒体库：用于入库触发与删除 JSON 逻辑；留空表示全部。支持多选。
- 计划任务媒体库：用于计划任务范围；留空表示全部。支持多选。
- 最近入库条目数量：用于“提取媒体信息（最近入库）”计划任务，默认 100。
- 最近入库时间窗口（天）：用于“刷新媒体元数据（最近入库）”计划任务，0 表示不限制。
- 元数据刷新模式：用于“刷新媒体元数据（最近入库）”计划任务，补全或全部替换。

计划任务
--------

- 提取媒体信息（范围内）：对计划任务媒体库范围内的存量视频条目执行提取并写入 JSON。（已存在Json则恢复）
- 提取媒体信息（最近入库）：对计划任务媒体库范围内的最近入库条目执行提取并写入 JSON（数量可配置）。（已存在Json则恢复）
- 刷新媒体元数据（最近入库）：刷新最近入库条目的元数据后，从 JSON 导入媒体信息（可选覆盖或补全）。
- 导出媒体信息（范围内）：对计划任务媒体库范围内已有 MediaInfo 的条目导出 JSON，无 MediaInfo 则跳过。

感谢
----

项目参考：<https://github.com/sjtuross/StrmAssistant>

