using Dalamud.Game.Command;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.UI;

namespace Posegraph;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/posegraph";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly BridgeServer _server;
    private readonly WindowSystem _windows = new("Posegraph");
    private readonly MainWindow _main;

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IChatGui chat,
        IFramework framework,
        IClientState clientState,
        ITargetManager targetManager,
        ITextureProvider textures,
        IDragDropManager dragDrop,
        IPluginLog log)
    {
        _pi = pi;
        _commands = commands;
        _chat = chat;
        _log = log;
        _config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        Loc.Apply(_config.Language, clientState, pi);

        var brio = new BrioBridge(pi);
        _server = new BridgeServer(framework, clientState, targetManager, brio, log);
        if (_config.AutoStartServer)
            TryStart();

        _main = new MainWindow(_config, pi, clientState, textures, dragDrop, brio,
                               new PoseApplier(framework, clientState, targetManager, brio), _server);
        _windows.AddWindow(_main);
        pi.UiBuilder.Draw += _windows.Draw;
        pi.UiBuilder.OpenMainUi += _main.Toggle;
        pi.UiBuilder.OpenConfigUi += _main.OpenSettings;

        _commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = Loc.T("打开主界面；on / off / status：开关或查看本地 HTTP 服务"),
        });
    }

    private void TryStart()
    {
        try { _server.Start(_config.Port); }
        catch (Exception ex)
        {
            _log.Error(ex, Loc.T("Posegraph HTTP 启动失败"));
            _chat.PrintError(Loc.F("[Posegraph] 端口 {0} 启动失败：{1}", _config.Port, ex.Message));
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
                _main.Toggle();
                return;
            case "on":
                TryStart();
                break;
            case "off":
                _server.Stop();
                break;
        }
        _chat.Print(_server.IsRunning
            ? Loc.F("[Posegraph] 运行中：http://127.0.0.1:{0}/", _server.Port)
            : Loc.T("[Posegraph] 已停止"));
    }

    public void Dispose()
    {
        _commands.RemoveHandler(Command);
        _pi.UiBuilder.Draw -= _windows.Draw;
        _pi.UiBuilder.OpenMainUi -= _main.Toggle;
        _pi.UiBuilder.OpenConfigUi -= _main.OpenSettings;
        _windows.RemoveAllWindows();
        _main.Dispose();
        _server.Dispose();
        _pi.SavePluginConfig(_config);
    }
}
