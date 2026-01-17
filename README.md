MediaInfoKeeper
===============

<p align="center">
  <img src="Resources/ThumbImage.png" alt="MediaInfoKeeper" width="320" />
</p>


功能
----------

- MediaInfo Keeper：MediaInfo 的缓存、恢复与刷新保护。
- IntroSkip：片头片尾跳过相关。
- Search：搜索与匹配增强。
- Proxy：设置Http代理能力。
- GitHub & Update：版本获取与更新。

计划任务
--------

- 刷新媒体元数据（最近入库）：刷新后从 JSON 导入 MediaInfo（可选覆盖或补全）。
- 提取媒体信息（最近入库）：对最近入库条目执行提取或恢复并写入 JSON。
- 提取媒体信息：对范围内存量条目执行提取或恢复并写入 JSON。
- 导出媒体信息：对已有 MediaInfo 的条目导出 JSON。
- 更新插件至最新版本

安装
----

测试版本 4.9.1.90，4.8 系列不支持。

1. 下载 `MediaInfoKeeper.dll`：<https://github.com/honue/MediaInfoKeeper/releases>
2. 放入 Emby 配置目录中的 `plugins` 目录。
3. 重启 Emby，在插件页面完成配置。

感谢
----

项目参考：

<https://github.com/sjtuross/StrmAssistant>
<https://github.com/xinjiawei/StrmAssistant_less>
