# presents-plugins（已归档）

> **本仓库已归档，不再更新。**
> presents 出品的 Alife 插件已拆分为独立仓库，每个插件一个仓库，源码、Issue 与 Release 都在各自仓库里。

## 新仓库

| 插件 | 说明 | 仓库 |
| --- | --- | --- |
| presents.BiliScroll | B站刷视频 | https://github.com/presents0721/presents.BiliScroll |
| presents.DeskPet3D | 3D桌宠 | https://github.com/presents0721/presents.DeskPet3D |
| presents.EmotionLive2DPet | Live2D桌宠 | https://github.com/presents0721/presents.EmotionLive2DPet |
| presents.GalMode | GalMode | https://github.com/presents0721/presents.GalMode |
| presents.QQSuite | QQSuite | https://github.com/presents0721/presents.QQSuite |
| presents.RawHtml | RawHtml | https://github.com/presents0721/presents.RawHtml |
| presents.Video | 视频理解 | https://github.com/presents0721/presents.VideoAnalyzer |
| presents.Vision.Qwen3VL | Qwen3-VL视觉分析 | https://github.com/presents0721/presents.Vision.Qwen3VL |
| presents.Wallpaper | 壁纸 | https://github.com/presents0721/presents.Wallpaper |

## 旧版本

本仓库保留的发行包与 tag 仅作历史存档，可继续下载；新版本请到上表对应仓库的 Releases 获取。

## 版本号规则（仍然适用）

**插件大版本 = 能用的 Alife 大版本。**

Alife 市场只保留「插件主版本 ≤ 客户端主版本」的版本，所以同一个插件会分成两条版本线，互不干扰：

- **5.x 只给 Alife 5.0.0+ 用，4.x 请勿安装或更新到 5.x**。5.0.0 移除了 Electron.NET，并把插件编译环境里的 ASP.NET Core 组件裁掉了，旧写法在 5.x 上直接编译失败；同理 5.x 的包在 4.x 上也跑不起来。
- 4.x 客户端不会看到 5.x 的版本（市场按主版本过滤），会自动停留在旧线最新版；5.x 客户端则会被市场自动升级到 5.x。
- 两条线的**功能一致**，只是底层接口按对应 Alife 版本适配。

## 安装

每个 plugin id 对应一个目录，例如 `presents.Wallpaper`。解压后目录名必须与 plugin id 一致，重启 Alife 后由客户端自行编译加载。
