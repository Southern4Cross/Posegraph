# UiPreview：离线界面截图

不进游戏，直接运行插件的真实窗口代码（`Posegraph.dll`），用 Dalamud 自带的 cimgui 生成绘制数据，再用 SkiaSharp 软件光栅化成 PNG。
用来快速检查排版（默认窗口大小下有没有裁切）、制作 README / 教程 / 更新日志的截图。

## 用法

先编译插件（Release），再：

```
dotnet run -c Release -- all --out shots
dotnet run -c Release -- main changelog --lang en --scale 1.25 --out shots_en
```

- 不带场景名时列出全部场景；`all` 为全部
- `--lang zh|en` 界面语言，`--scale` 界面缩放（对应 Dalamud 的全局缩放）
- 每个场景输出一张按窗口裁切的 PNG

场景在 `Scenes.cs` 里加：设好窗口状态，调用 `app.Shot(...)`。

## 原理与限制

- 字体用 Dalamud 资源目录里的 Noto Sans CJK 与 FontAwesome（`%AppData%\XIVLauncherCN\dalamudAssets\dev\UIRes`），颜色用 Dalamud 默认主题，插件窗口再套自己的主题，和游戏里看到的基本一致
- Dalamud 服务（`IDalamudPluginInterface`、`ITextureProvider` 等）是替身（`Stub.cs`、`Services.cs`）；
  依赖游戏的静态成员（字体、界面缩放）和读游戏内存 / Brio IPC 的入口用 Harmony 换掉（`Host.cs`、`PluginPatches.cs`）
- 不能读 `UiBuilder.DefaultFontSizePx` 这类取游戏服务配置的成员，离线时会一直等待
- 需要识别结果的画面（图像叠加、3D 骨架）要后端在线，目前的场景都是未识别状态

## 横幅

`make_banner.py` 生成更新日志窗口的横幅 `Posegraph/Changelog/banner.png`（几个识别出的姿势画成骨架节点图）：

```
python make_banner.py ../../backend/outputs/dancing.npz ../../backend/outputs/flex.npz ...
```
