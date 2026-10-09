<p align="center">
  <img src="Resources/ThumbImage.png" alt="MediaInfoKeeper" width="320" />
</p>

<p align="center">
  <a href="https://github.com/honue/MediaInfoKeeper/releases">
    <img src="https://img.shields.io/github/v/release/honue/MediaInfoKeeper?label=MediaInfoKeeper" />
  </a>
  <a href="https://github.com/honue/MediaInfoKeeper/stargazers">
    <img src="https://img.shields.io/github/stars/honue/MediaInfoKeeper" />
  </a>
  <a href="https://api.github.com/repos/honue/mediainfokeeper/releases/latest">
    <img src="https://img.shields.io/github/downloads/honue/MediaInfoKeeper/latest/total?label=LatestUsers" />
  </a>
  <a href="https://github.com/honue/MediaInfoKeeper/releases">
    <img src="https://img.shields.io/github/downloads/honue/MediaInfoKeeper/total?label=Downloads" />
  </a>
  <a href="https://github.com/MediaBrowser/Emby.Releases/releases">
    <img src="https://img.shields.io/github/v/release/MediaBrowser/Emby.Releases?label=Emby" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/honue/MediaInfoKeeper" />
  </a>
  <br>
  <a href="https://github.com/honue/MediaInfoKeeper/wiki">
    <img src="https://img.shields.io/badge/Wiki-功能说明-blue?logo=github&amp;logoColor=white" />
  </a>
  <a href="https://t.me/EmbyPatch_Channel">
    <img src="https://img.shields.io/badge/Telegram频道-EmbyPatch-26A5E4?logo=telegram&amp;logoColor=white" />
  </a>
</p>
<p align="center">
  <img src="Resources/ScreenShot.png" alt="MediaInfoKeeper" width="640" />
</p>

✨ 功能概览
--------

- **媒体信息管理**：入库/浏览时自动提取 MediaInfo 并持久化为 JSON，可备份恢复，避免反复触发 ffprobe。
- **片头片尾**：自动扫描打标，支持与片头数据库共享识别结果。
- **元数据增强**：中文搜索优化、TMDB/TVDB 补充设置、弹幕与豆瓣角色刮削。
- **strm 直连**：视频/音乐 `.strm` 文件直连播放，减少中转。
- **多版本合并**：电影/剧集多版本自动合并显示。
- **网络代理**：为元数据请求提供代理与 TMDB 反代能力。
- **手动与定时工具**：插件更新、最近入库元数据刷新、媒体信息备份恢复，以及补齐缺失媒体流、片头片尾标记和视频预览缩略图。

安装完成后，在 **Emby 控制台 → 插件 → MediaInfoKeeper** 打开配置页。每项设置的详细说明见 [使用指南](Docs/使用指南.md)，更底层的实现细节参考原作者 [GitHub Wiki](https://github.com/honue/MediaInfoKeeper/wiki)。

⏬ 安装
--------

1. 下载 dll 文件：[Releases](https://github.com/honue/MediaInfoKeeper/releases)
   不带后缀的是通用版本，[最新通用版本](https://github.com/honue/MediaInfoKeeper/releases/latest/download/MediaInfoKeeper.dll)。
2. 放入 Emby 配置目录中的 `plugins` 目录。
3. 务必重命名为 `MediaInfoKeeper.dll`，否则后续自动更新会有两个 dll 存在。
4. 重启 Emby，在插件页面完成配置。

🧩 兼容性
-----------

- 版本说明：本仓库最新代码始终以支持 Emby 最新 release 为目标，开发过程中可能出现阶段性兼容问题。

- 当前插件 `latest` 版本适配Emby `4.10.0.40`，全平台支持。

- 更新限制：插件自动更新任务已按 [版本区间](Version.json) 限制，不会更新到当前 Emby 不支持的插件版本。

- 不支持：`4.8` 系列，`4.9.5.0` 最后支持版本 `v1.7.5.3`。

⚖️ Lisence
-----------

本项目基于 [GNU General Public License v3.0](LICENSE) 开源。

- 二次开发、改名发布或重新打包 DLL 时，必须在用户可见位置标注原作者与原项目来源。
- 发布派生版本或二进制文件时，必须遵守 GPLv3，保留许可证，并公开对应版本源码或提供有效源码获取方式。

⭐ Stars
------------

<p align="center">
  <img src="https://api.star-history.com/svg?repos=honue/MediaInfoKeeper&type=Date" width="600"/>
</p>

🙏 致谢
----

本项目在部分功能设计、补丁思路与资源集成上参考或使用了以下开源项目，感谢各位对 Emby 社区的长期贡献：

- [StrmAssistant](https://github.com/sjtuross/StrmAssistant)：部分功能 Patch 思路以及 `simple` 分词器文件来源。

- [dd-danmaku](https://github.com/l429609201/dd-danmaku)：弹幕资源 `ede.js` 来源。
