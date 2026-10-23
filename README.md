# presents-plugins

Alife 插件分发仓库（presents 出品）

## 版本号规则（重要）

**插件大版本 = 能用的 Alife 大版本。**

Alife 市场只保留「插件主版本 ≤ 客户端主版本」的版本，所以同一个插件会分成两条版本线，互不干扰：

| 插件 | Alife 5.0.0 及以上（5.x 线） | Alife 4.x（旧线） |
| --- | --- | --- |
| presents.Wallpaper 壁纸 | **5.7.3** | 1.7.2 |
| presents.BiliScroll B站刷视频 | **5.0.0** | 4.9.6 |
| presents.DeskPet3D 3D桌宠 | 1.2.0 | （同一版） |
| presents.EmotionLive2DPet Live2D桌宠 | 5.0.0 | 1.0.2 |
| presents.RawHtml | 1.0.0 | （同一版） |

- **5.x 只给 Alife 5.0.0+ 用，4.x 请勿安装或更新到 5.x**。5.0.0 移除了 Electron.NET，并把插件编译环境里的 ASP.NET Core 组件裁掉了，旧写法在 5.x 上直接编译失败；同理 5.x 的包在 4.x 上也跑不起来。
- 4.x 客户端不会看到 5.x 的版本（市场按主版本过滤），会自动停留在旧线最新版；5.x 客户端则会被市场自动升级到 5.x。
- 两条线的**功能一致**，只是底层接口按对应 Alife 版本适配。

## 安装

每个 plugin id 对应一个目录，例如 `presents.Wallpaper`。解压后目录名必须与 plugin id 一致，重启 Alife 后由客户端自行编译加载。
