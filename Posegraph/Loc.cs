using System.Globalization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Posegraph;

/// <summary>
/// 界面多语言。源码里的界面文字以简体中文为键，Loc.T 按当前语言查表（Loc.En.cs），查不到时原样返回中文。
/// 语言设置为 auto 时：游戏客户端是中文 → 中文；否则 Dalamud 界面语言是中文 → 中文；其余为英文。
/// </summary>
public static partial class Loc
{
    public static string Lang { get; private set; } = "zh";

    /// <summary>设置页的语言选项（auto 的显示名按当前语言翻译，其余用各自语言书写）。</summary>
    public static readonly (string Key, string Label)[] Languages =
    [
        ("auto", "自动（跟随游戏客户端）"),
        ("zh", "简体中文"),
        ("en", "English"),
    ];

    public static void Apply(string setting, IClientState clientState, IDalamudPluginInterface pi)
        => Lang = setting is "zh" or "en" ? setting : Detect(clientState, pi);

    private static string Detect(IClientState clientState, IDalamudPluginInterface pi)
    {
        // 按枚举名判断：国际服 Dalamud 的 ClientLanguage 没有中文成员，直接引用会编译不过
        if (clientState.ClientLanguage.ToString().Contains("Chinese", StringComparison.OrdinalIgnoreCase))
            return "zh";
        return pi.UiLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    public static string T(string zh) => Lang == "en" && En.TryGetValue(zh, out var en) ? en : zh;

    public static string F(string zh, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(zh), args);
}
