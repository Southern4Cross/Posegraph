using System.Numerics;

namespace UiPreview;

/// <summary>截图场景：名字 → (程序对象, 输出路径)。窗口用各自的默认大小，方便发现默认尺寸下的裁切。</summary>
public static class Scenes
{
    private static Action<App, string> MainTab(int tab) => (app, path) =>
    {
        App.FakeActors = null;
        app.Main.IsOpen = true;
        app.Main.Position = new Vector2(40, 40);
        app.SelectTab(tab);
        app.Shot(path, () => Host.DrawWindow(app.Main), [app.Main.WindowName]);
    };

    public static readonly Dictionary<string, Action<App, string>> All = new()
    {
        ["main"] = MainTab(0),
        ["main-3d"] = MainTab(1),
        ["main-tuning"] = MainTab(2),
        ["main-warnings"] = MainTab(3),
        ["main-settings"] = MainTab(4),
        // 调整页全部展开（模拟点击折叠栏，从下往上点，上面的位置不变）
        ["main-tuning-open"] = (app, path) =>
        {
            App.FakeActors = null;
            app.Main.IsOpen = true;
            app.Main.Position = new Vector2(40, 40);
            app.Main.Size = new Vector2(420, 760);
            app.Main.SizeCondition = Dalamud.Bindings.ImGui.ImGuiCond.Always;
            app.SelectTab(2);
            void Draw() => Host.DrawWindow(app.Main);
            for (var i = 0; i < 3; i++) Host.Current.Frame(Draw);
            foreach (var y in new[] { 461f, 432f, 404f })
            {
                Host.Current.MousePos = new Vector2(124, y);
                Host.Current.Frame(Draw);
                Host.Current.MouseDown[0] = true;
                Host.Current.Frame(Draw);
                Host.Current.MouseDown[0] = false;
                Host.Current.Frame(Draw);
            }
            Host.Current.MousePos = new Vector2(-1000, -1000);
            app.Shot(path, Draw, [app.Main.WindowName]);
            app.Main.Size = new Vector2(420, 560);
        },
        // 压力测试（不截图，打印统计；不包含在 all 里）：几千张图的文件夹里连续滚动
        ["bench-library"] = (app, _) => Stress.Library(app,
            Environment.GetEnvironmentVariable("STRESS_SAMPLES") ?? @"D:\SirenCode\根据图片获取动作\测试图片"),
        ["bench-wic"] = (_, _) => Stress.Wic(),
        ["library"] = (app, path) =>
        {
            app.Library.IsOpen = true;
            app.Library.Position = new Vector2(40, 40);
            // 列目录、缩略图都在后台：边画边等（不画的话缩略图会被当成已滚出视野、暂不解码）
            for (var i = 0; i < 20; i++)
            {
                Host.Current.Frame(() => Host.DrawWindow(app.Library));
                Thread.Sleep(100);
            }
            app.Shot(path, () => Host.DrawWindow(app.Library), [app.Library.WindowName], frames: 3);
        },
        // 压力测试：最小宽度、长状态、调整页有未保存的修改（主窗口有会话但没有图，只看排版）
        ["stress-main"] = (app, path) =>
        {
            App.FakeActors = null;
            app.Main.IsOpen = true;
            app.Main.Position = new Vector2(40, 40);
            app.Main.Size = new Vector2(330, 560);
            app.Main.SizeCondition = Dalamud.Bindings.ImGui.ImGuiCond.Always;
            App.Set(app.Main, "_session", new Posegraph.Backend.SessionInfo("demo", "demo", [800, 1200], 0, null, []));
            App.Set(app.Main, "_imageSha1", "0000000000000000000000000000000000000000");
            var p = App.Get<Posegraph.Backend.RetargetParams>(app.Main, "_params");
            p.Amplitude["neck"] = 0.7f;
            p.HandStrength = 0.5f;
            App.Set(app.Main, "_statusText", Posegraph.Loc.T("已从识别缓存读取") + Posegraph.Loc.F("，已融合 {0} 张辅助视角（{1}）", 2, "背面、几乎同角度")
                                            + "；back-view.png：" + Posegraph.UI.AuxViewsMenu.Warning(new Posegraph.Backend.AuxView(170, "back", 0.6f, 31, "r_lowleg", 44)));
            app.SelectTab(2);
            app.Shot(path, () => Host.DrawWindow(app.Main), [app.Main.WindowName]);
            app.Main.Size = new Vector2(420, 560);
            App.Set(app.Main, "_session", null);
            App.Set(app.Main, "_statusText", "");
        },
        ["stress-split"] = (app, path) =>
        {
            App.FakeActors = [(201, "Aerith Gainsborough of the Ancient Cetra"), (202, "Tifa")];
            app.Gposing = true;
            app.Main.IsOpen = true;
            app.Split.IsOpen = true;
            app.Config.SplitDock = false;
            app.Split.Position = new Vector2(40, 40);
            app.Split.Size = new Vector2(280, 300);
            app.Split.SizeCondition = Dalamud.Bindings.ImGui.ImGuiCond.Always;
            var status = App.Get<System.Collections.Concurrent.ConcurrentDictionary<string, (string, System.Numerics.Vector4)>>(app.Split, "_rowStatus");
            status["202:Tifa"] = ("找不到角色（请在 GPose 中选中目标）——后端返回 503：模型加载失败：CUDA out of memory", Posegraph.UI.PosegraphTheme.Red);
            for (var i = 0; i < 3; i++) Host.Current.Frame(() => Host.DrawWindow(app.Split));   // 先刷新一次角色身份
            status["202:Tifa"] = ("找不到角色（请在 GPose 中选中目标）——后端返回 503：模型加载失败：CUDA out of memory", Posegraph.UI.PosegraphTheme.Red);
            app.Shot(path, () => Host.DrawWindow(app.Split), [app.Split.WindowName]);
            app.Gposing = false;
            App.FakeActors = null;
            app.Config.SplitDock = true;
        },
        ["changelog"] = (app, path) =>
        {
            app.Update.IsOpen = true;
            app.Shot(path, () => Host.DrawWindow(app.Update), [app.Update.WindowName]);
        },
        ["split"] = (app, path) =>
        {
            App.FakeActors = [(201, "Aerith Gainsborough"), (202, "Tifa Lockhart"), (203, "Y'shtola Rhul")];
            app.Gposing = true;
            app.Main.IsOpen = true;
            app.Split.IsOpen = true;
            app.Main.Position = new Vector2(40, 40);
            app.SelectTab(0);
            app.Shot(path, () =>
            {
                Host.DrawWindow(app.Main);
                Host.DrawWindow(app.Split);
            }, [app.Main.WindowName, app.Split.WindowName], frames: 6);
            app.Gposing = false;
            App.FakeActors = null;
        },
    };
}
