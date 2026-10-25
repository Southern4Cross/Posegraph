using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Posegraph.Backend;

namespace Posegraph.UI;

/// <summary>
/// 主界面：状态栏 → 选图识别 → 图像叠加 / 3D 火柴人 / 调整 / 提示 / 设置 → 应用、镜头、还原、导出。
/// 识别只做一次（后端缓存），调参只重跑重定向；拖动滑杆时只刷新预览，松手才应用到角色（Brio 撤销历史保持干净）。
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.35f, 0.85f, 0.4f, 1);
    private static readonly Vector4 Yellow = new(1f, 0.8f, 0.25f, 1);
    private static readonly Vector4 Red = new(1f, 0.35f, 0.3f, 1);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1);
    private static readonly Vector4 Orange = new(1f, 0.55f, 0f, 1);
    private static readonly Vector4 MhrGreen = new(0.31f, 0.86f, 0.31f, 1);

    private const string ImageExtensions = "{.jpg,.jpeg,.png,.webp,.bmp,.gif,.tif,.tiff,.avif}";

    // 静态表里的界面文字存中文原文，使用时再 Loc.T 翻译（运行时可切换语言）

    private static readonly (string Key, string Label, string Tip)[] AmplitudeGroups =
    [
        ("spine", "脊柱", "腰/背/胸三节的弯曲扭转。脊柱用绑定朝向对齐后动作可能偏保守，可适当放大"),
        ("neck", "颈", "颈部转动。插画头颈比例夸张，识别容易估大，建议 0.7"),
        ("head", "头", "头部相对颈的转动。插画建议 0.7"),
        ("clavicle", "锁骨", "耸肩/含胸。过大时斜方肌会隆起、肩部出尖角"),
        ("wrist", "手腕", "手腕弯曲扭转"),
    ];

    private readonly Configuration _config;
    private readonly IDalamudPluginInterface _pi;
    private readonly IClientState _clientState;
    private readonly ITextureProvider _textures;
    private readonly ITextureReadbackProvider _textureReadback;
    private readonly IDragDropManager _dragDrop;
    private readonly BrioBridge _brio;
    private readonly PoseApplier _applier;
    private readonly BridgeServer _server;
    private readonly BackendClient _client;
    private readonly BackendProcess _process;
    private readonly SystemProbe _probe = new();
    private readonly BackendUpdater _updater = new();

    // 后端版本信息（读磁盘，2 秒刷新一次）
    private BackendUpdater.VersionInfo? _installedBackend;
    private int? _packageRuntime;
    private bool _backendOutdated;
    private DateTime _lastVersionCheck = DateTime.MinValue;
    private readonly FileDialogManager _fileDialog = new();

    // 后端状态
    private Health? _health;
    private string? _healthError;
    private DateTime _lastHealth = DateTime.MinValue;
    private Task? _healthTask;

    // 会话
    private SessionInfo? _session;
    private IDalamudTextureWrap? _texture;
    private int _person;
    private RetargetParams _params;
    private bool _paramsDirty;       // 参数可能变了：松手后与已保存的比较，不同就写入配置
    private string _savedParams = "";
    private string _preset;
    private volatile RetargetResult? _result;
    private volatile bool _recognizing;

    // 对话结果在 Draw 中接收，避免后台线程与 ImGui 同时改动聊天记录。
    private string _chatPrompt = "";
    private string _chatApiKey = "";
    private string? _chatError;
    private PoseChatMessage[] _chatHistory = [];
    private Task<PoseChatResponse>? _chatTask;
    private CancellationTokenSource? _chatCts;
    private int _chatRevision, _chatRequestRevision;
    private bool _chatPoseActive, _chatFreshStart, _chatImageDetached, _chatGround = true;
    private string _chatSentPrompt = "";
    private readonly HashSet<string> _chatPendingBones = new(StringComparer.Ordinal);
    private volatile string? _lastAppliedPreviewPose;
    private bool _chatRequestFullBase;
    private readonly ModelCatalogClient _modelCatalog = new();
    private string[] _chatModels = [];
    private Task<string[]>? _modelsTask;
    private CancellationTokenSource? _modelsCts;
    private int _modelsRevision, _modelsRequestRevision;
    private string _modelsRequestEndpoint = "";
    private string? _modelsError;

    // 重定向请求合并：忙时只记"还需要再算一次"
    private volatile bool _rtBusy, _rtDirty, _rtPush;
    private int _previewOnlyRevision;

    // 角色
    private int? _actor;
    private (int Index, string Name)[] _actors = [];
    private DateTime _lastActors = DateTime.MinValue;
    private string? _reference;
    private string? _detectedRace;      // 从角色外貌数据读出的骨架编号
    private string? _raceOverride;      // 手动指定，null 为自动
    private string? _referenceSignature;   // 缓存的参考姿势属于哪个角色身份（PoseApplier.ActorIdentity）
    private string? _currentSignature;     // 当前角色身份，每秒刷新
    private string? _pendingSignature;     // 检测到变化后等它稳定一秒（模型重建完）再重算
    private int? _pushedIndex;             // 应用过姿势的角色：外貌变化重算后自动再应用
    private string? _pushStatus;           // 下一次应用成功时显示的状态（代替"已应用到角色"）
    private readonly Dictionary<string, string> _backups = new();   // 身份 → 第一次应用前的姿势
    private bool _resetPending;                                     // 换了种族：下次应用前先清除 Brio 残留姿势
    private readonly HashSet<string> _cleanSignatures = new();      // 已清除过 Brio 姿势的身份：骨长不符也信游戏值
    private string? _actorKey;                                      // 上面这些状态属于哪个角色（ActorIdentity.Key）
    private readonly Dictionary<string, ActorState> _actorStates = new();   // 本次 GPose 里切换过的角色各自的状态

    /// <summary>切走时记下的角色状态，切回来时恢复。Race：上次看到的骨架编号，切回来时不同说明在别处换过种族。</summary>
    private sealed record ActorState(string? Race, string? RaceOverride, bool ResetPending);

    // 3D 视图
    private float _yaw, _pitch = 0.15f, _zoom = 1f;
    private bool _dragged;
    private string? _selected;

    private string _statusText = Loc.T("就绪");
    private Vector4 _statusColor = Grey;
    private int? _selectTab;
    private int? _drawSelectTab;

    public MainWindow(Configuration config, IDalamudPluginInterface pi, IClientState clientState, ITextureProvider textures,
                      ITextureReadbackProvider textureReadback,
                      IDragDropManager dragDrop, BrioBridge brio, PoseApplier applier, BridgeServer server)
        : base("Posegraph###PosegraphMain")
    {
        _config = config;
        _pi = pi;
        _clientState = clientState;
        _textures = textures;
        _textureReadback = textureReadback;
        _dragDrop = dragDrop;
        _brio = brio;
        _applier = applier;
        _server = server;
        _client = new BackendClient(() => _config.BackendPort);
        _process = new BackendProcess();
        _preset = config.DefaultPreset;
        _params = LoadParams(_preset);
        _savedParams = ParamsSnapshot(_params);
        try { _chatApiKey = ApiKeyProtection.Unprotect(config.ChatApiKeyProtected); }
        catch { _chatError = Loc.T("无法解密已保存的 API 密钥，请在设置中重新输入"); }
        if (config.ChatModelsEndpoint == config.ChatApiEndpoint.Trim())
            _chatModels = (config.ChatModels ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();

        Size = new Vector2(420, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(330, 260), MaximumSize = new Vector2(4000, 4000) };

        if (_config.AutoStartBackend && BackendProcess.Validate(_config) is null)
            _ = Task.Run(async () =>
            {
                try { await _client.HealthAsync(); }   // 已有后端在跑（比如手动启动的）就不再启动
                catch { _process.Start(_config); }
            });
    }

    public void OpenSettings()
    {
        IsOpen = true;
        _selectTab = 4;
    }

    public void Dispose()
    {
        DisposeAdvice();
        _chatCts?.Cancel();
        _chatCts?.Dispose();
        if (_chatTask is { } chat)
            _ = chat.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _modelsCts?.Cancel();
        _modelsCts?.Dispose();
        if (_modelsTask is { } models)
            _ = models.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _modelCatalog.Dispose();
        if (_config.StopBackendOnExit)
            _process.Stop();
        _process.Dispose();
        DisposeImages();   // finishes cache cleanup before disposing the HTTP client
        _updater.Dispose();
        _texture?.Dispose();
    }

    private void SetStatus(string text, Vector4? color = null)
    {
        _statusText = text;
        _statusColor = color ?? Grey;
    }

    private void SetError(string text) => SetStatus(text, Red);

    // ------------------------------------------------------------------ 状态

    private bool BackendOnline => _health is not null;

    private bool ModelReady => _health?.Model.Loaded == true;

    private void RefreshBackendVersion()
    {
        if (DateTime.UtcNow - _lastVersionCheck < TimeSpan.FromSeconds(2) || string.IsNullOrEmpty(_config.BackendDir)) return;
        _lastVersionCheck = DateTime.UtcNow;
        _installedBackend = BackendUpdater.Installed(_config.BackendDir);
        _packageRuntime = BackendUpdater.PackageRuntime(_config.BackendDir);
        _backendOutdated = _packageRuntime is not null && BackendUpdater.NeedsUpdate(_config.BackendDir);
    }

    private void PollHealth()
    {
        _probe.Tick();
        RefreshBackendVersion();
        if (_healthTask is { IsCompleted: false } || DateTime.UtcNow - _lastHealth < TimeSpan.FromSeconds(2)) return;
        _lastHealth = DateTime.UtcNow;
        _healthTask = Task.Run(async () =>
        {
            try
            {
                _health = await _client.HealthAsync();
                _healthError = null;
            }
            catch (Exception ex)
            {
                _health = null;
                _healthError = ex.Message;
            }
        });
    }

    private void RefreshActors()
    {
        if (DateTime.UtcNow - _lastActors < TimeSpan.FromSeconds(1)) return;
        _lastActors = DateTime.UtcNow;
        if (!_clientState.IsGPosing)
        {
            _actors = [];
            if (_actorKey is not null || _actorStates.Count > 0) ForgetActors();
            return;
        }
        _actors = _brio.GetActors().Where(Races.IsPoseable).Select(a => ((int)a.ObjectIndex, a.Name.TextValue)).ToArray();

        var id = _applier.Identify(_actor);
        if (id is null) return;
        // 换了一个角色（GPose 目标换人、下拉框选了别人）：换成那个角色自己的状态，不当作换种族
        if (id.Key != _actorKey)
        {
            SwitchActor(id);
            return;
        }
        if (_currentSignature is not null && _currentSignature != id.Signature &&
            (_chatPoseActive || _chatTask is not null || _chatHistory.Length > 0))
        {
            _resetPending |= id.Race != _detectedRace;
            ResetChatContext(discardPose: true);
            if (_session is not null) RequestRetarget(false);
        }
        // 同一个角色外貌变了（Glamourer 换种族 / 体型）：丢弃参考姿势，重新计算
        if (_reference is not null && _referenceSignature != id.Signature)
        {
            // 模型重建中，或刚变化：等下一秒身份不再变化再读游戏骨长
            if (!id.ModelReady || _pendingSignature != id.Signature)
            {
                _pendingSignature = id.Signature;
                return;
            }
            _pendingSignature = null;
            _currentSignature = id.Signature;
            var raceChanged = id.Race != _detectedRace;
            if (raceChanged) _resetPending = true;   // 旧骨架的姿势增量会叠加到新骨架上
            _reference = null;
            _detectedRace = id.Race;
            ResetChatContext(discardPose: true);
            if (_session is not null)
            {
                var push = _pushedIndex == id.Index;
                var message = raceChanged ? Loc.F("检测到角色种族变化（{0}），已重新计算", Races.Label(id.Race))
                                          : Loc.T("检测到角色外貌变化，已重新计算");
                if (push) _pushStatus = message + Loc.T("并应用");
                else SetStatus(message, Yellow);
                RequestRetarget(push);
            }
            return;
        }
        _pendingSignature = null;
        _currentSignature = id.Signature;
        if (_reference is null)
            _detectedRace = id.Race;
    }

    /// <summary>
    /// 切换到另一个角色：记下当前角色的状态，换成新角色在本次 GPose 里上次的状态。
    /// 不清除新角色的 Brio 姿势——除非切走期间它在别处换过种族（骨架编号与上次看到的不同）。
    /// </summary>
    private void SwitchActor(PoseApplier.ActorIdentity id)
    {
        ResetChatContext(discardPose: true);
        SaveActorState();
        var switched = _actorKey is not null;
        _actorKey = id.Key;
        var state = _actorStates.GetValueOrDefault(id.Key);
        var raceChanged = state?.Race is { } old && id.ModelReady && old != id.Race;
        // 进 GPose 前（还没有角色时）手动指定的种族留给第一个角色
        if (switched || state is not null) _raceOverride = state?.RaceOverride;
        _resetPending = state?.ResetPending == true || raceChanged;
        _detectedRace = id.Race;
        _reference = null;
        _referenceSignature = null;
        _pendingSignature = null;
        _currentSignature = id.Signature;
        if (_session is null) return;
        if (raceChanged) SetStatus(Loc.F("检测到角色种族变化（{0}），已重新计算", Races.Label(id.Race)), Yellow);
        else if (switched) SetStatus(Loc.F("已切换到 {0}，已重新计算", ActorName(id.Index)));
        RequestRetarget(false);
    }

    private void SaveActorState()
    {
        if (_actorKey is not null)
            _actorStates[_actorKey] = new ActorState(_detectedRace, _raceOverride, _resetPending);
    }

    /// <summary>退出 GPose：角色都没了，下次进 GPose 会重新分配 ObjectIndex，按角色记的状态全部作废。</summary>
    private void ForgetActors()
    {
        ResetChatContext(discardPose: true);
        _actorStates.Clear();
        _backups.Clear();
        _cleanSignatures.Clear();
        _actorKey = null;
        _reference = null;
        _referenceSignature = null;
        _pendingSignature = null;
        _currentSignature = null;
        _pushedIndex = null;
        _resetPending = false;
    }

    private string ActorName(int index) => _actors.FirstOrDefault(a => a.Index == index).Name ?? $"#{index}";

    private static void StatusIcon(FontAwesomeIcon icon, Vector4 color, string tip)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        ImGui.SameLine();
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, string tip, bool enabled = true)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
            clicked = ImGuiComponents.IconButton(id, icon);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
        return clicked && enabled;
    }

    private void DrawStatusBar()
    {
        // Brio
        var brio = _pi.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Brio");
        var brioIpc = brio is { IsLoaded: true } && _brio.IsAvailable();
        if (brio is null) StatusIcon(FontAwesomeIcon.TimesCircle, Red, Loc.T("Brio：未安装"));
        else if (!brio.IsLoaded) StatusIcon(FontAwesomeIcon.PauseCircle, Yellow, Loc.F("Brio {0}：已安装但未启用", brio.Version));
        else StatusIcon(FontAwesomeIcon.CheckCircle, brioIpc ? Green : Yellow,
                        brioIpc ? Loc.F("Brio {0}：可用", brio.Version) : Loc.F("Brio {0}：已启用，IPC 暂不可用", brio.Version));

        // GPose
        StatusIcon(FontAwesomeIcon.Camera, _clientState.IsGPosing ? Green : Grey,
                   _clientState.IsGPosing ? Loc.T("GPose：已进入") : Loc.T("GPose：未进入（只能预览和导出，不能应用到角色）"));

        // IVCS（按最近一次计算时读到的游戏姿势判断）
        if (_clientState.IsGPosing && _result?.Ivcs is { } ivcs)
            StatusIcon(FontAwesomeIcon.HandPaper, ivcs ? Green : Yellow,
                       ivcs ? Loc.T("IVCS：已加载") : Loc.T("IVCS：未检测到（手指第三节的弯曲已合并到第二节；装上 IVCS 手指会更准）"));

        // 后端
        var (icon, color, tip) = BackendState();
        StatusIcon(icon, color, tip);

        // 显存 / 内存
        var vramOk = ModelReady || _probe.VramFreeMB >= SystemProbe.RequiredVramMB || _config.BackendDevice == "cpu";
        var vramText = _probe.VramFreeMB is { } free
            ? Loc.F("{0}\n显存可用 {1:0.0} / {2:0.0} GB", _probe.GpuName, free / 1024f, _probe.VramTotalMB / 1024f)
            : _probe.GpuError ?? Loc.T("读取中…");
        vramText += ModelReady ? Loc.F("\n模型已加载（{0}）", DeviceLabel(_health?.Model.Device))
            : _config.BackendDevice == "cpu" ? Loc.T("\n识别设备：CPU（不占显存）")
            : Loc.F("\nGPU 识别约需 {0:0.0} GB", SystemProbe.RequiredVramMB / 1024f) + (vramOk ? ""
                : _config.BackendDevice == "auto" ? Loc.T("，当前不足：将自动改用 CPU（约 10 秒/张）")
                : Loc.T("，当前不足：显存溢出到共享内存后识别会极慢，游戏也可能崩溃。建议在设置里改为“自动”或“CPU”"));
        StatusIcon(FontAwesomeIcon.Microchip, _probe.VramFreeMB is null ? Grey : vramOk ? Green : Yellow, vramText);
        var ramOk = ModelReady || _probe.RamAvailMB >= SystemProbe.RequiredRamMB;
        StatusIcon(FontAwesomeIcon.Memory, _probe.RamTotalMB == 0 ? Grey : ramOk ? Green : Yellow,
                   Loc.F("内存可用 {0:0.0} / {1:0.0} GB", _probe.RamAvailMB / 1024f, _probe.RamTotalMB / 1024f) +
                   (ramOk ? "" : Loc.F("\n加载模型约需 {0:0.0} GB，当前不足", SystemProbe.RequiredRamMB / 1024f)));

        // 右侧：后端开关、模型加载/释放、设置
        var right = ImGui.GetContentRegionMax().X - 3 * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right));
        if (BackendOnline || _process.IsRunning)
        {
            if (IconButton("##stopBackend", FontAwesomeIcon.PowerOff, Loc.T("停止后端（释放显存与内存）")))
                StopBackend();
        }
        else
        {
            var problem = BackendProcess.Validate(_config);
            var resources = _probe.RamAvailMB < SystemProbe.RequiredRamMB ? Loc.T("\n注意：当前可用内存低于建议值") : "";
            if (IconButton("##startBackend", FontAwesomeIcon.Plug, problem ?? Loc.T("启动识别后端") + resources))
            {
                if (problem is null) StartBackend();
                else
                {
                    SetError(problem);
                    _selectTab = 4;
                }
            }
        }
        ImGui.SameLine();
        if (ModelReady)
        {
            if (IconButton("##unload", FontAwesomeIcon.Eject, Loc.T("释放模型显存（下次识别会重新加载）")))
                _ = Run(async () => { await _client.UnloadModelAsync(); SetStatus(Loc.T("已释放模型")); });
        }
        else if (IconButton("##load", FontAwesomeIcon.Download, Loc.T("预加载模型"), BackendOnline && _health?.Model.Loading != true))
            _ = Run(async () => { await _client.LoadModelAsync(); SetStatus(Loc.T("模型加载中…")); });
        ImGui.SameLine();
        if (IconButton("##settings", FontAwesomeIcon.Cog, Loc.T("设置")))
            _selectTab = 4;
    }

    private (FontAwesomeIcon, Vector4, string) BackendState()
    {
        if (_health is { } h)
        {
            if (h.Model.Error is { } err) return (FontAwesomeIcon.Server, Red, Loc.F("后端在线，模型加载失败：{0}", err));
            if (_backendOutdated)
                return (FontAwesomeIcon.Server, Yellow, Loc.F("后端在线 v{0}：有新版本 {1}，到设置页更新后端", h.Version, BackendUpdater.PluginTag));
            if (h.Model.Loading) return (FontAwesomeIcon.Server, Yellow, Loc.T("后端在线，模型加载中…"));
            var release = _config.ReleaseAfter switch { 0 => Loc.T("识别后立即释放"), < 0 => Loc.T("常驻"), var s => Loc.F("空闲 {0} 秒后释放", s) };
            return (FontAwesomeIcon.Server, Green, Loc.F("后端在线 v{0}\n", h.Version) + (h.Model.Loaded
                ? Loc.F("模型已加载：{0} {1}（{2}）", DeviceLabel(h.Model.Device), h.Model.Precision, h.Model.DeviceReason)
                : Loc.T("模型未加载（识别时自动加载）")) + Loc.F("\n设备设置：{0}，{1}", DeviceLabel(_config.BackendDevice), release));
        }
        if (_process.IsRunning) return (FontAwesomeIcon.Server, Yellow, Loc.T("后端启动中…"));
        var exit = _process.ExitCode is { } code ? Loc.F("\n上次启动的后端已退出（代码 {0}），见设置页日志", code) : "";
        return (FontAwesomeIcon.Server, Grey, Loc.T("后端离线") + exit);
    }

    private static string DeviceLabel(string? device) => device switch
    {
        "cuda" => "GPU",
        "cpu" => "CPU",
        "auto" => Loc.T("自动"),
        _ => device ?? "",
    };

    /// <summary>一键包用户：下载与插件同版本的后端源码替换 backend\src（见 BackendUpdater）。</summary>
    private void UpdateBackend()
    {
        _ = Task.Run(async () =>
        {
            var error = await _updater.UpdateAsync(_config.BackendDir, _config.UpdateProxy, StopBackendAndWaitAsync, StartAndCheckAsync);
            _lastVersionCheck = DateTime.MinValue;
            if (error is null) SetStatus(Loc.F("后端已更新到 {0}", BackendUpdater.PluginTag), Green);
            else SetError(error);
        });
    }

    private async Task StopBackendAndWaitAsync()
    {
        if (_process.IsRunning) _process.Stop();
        else
        {
            try { await _client.ShutdownAsync(); } catch { }
        }
        for (var i = 0; i < 20; i++)
        {
            try { await _client.HealthAsync(); }
            catch { break; }
            await Task.Delay(500);
        }
        _health = null;
    }

    /// <summary>启动后端并等它响应；version 非空时要求版本一致（确认运行的是新代码）。</summary>
    private async Task<bool> StartAndCheckAsync(string version)
    {
        if (BackendProcess.Validate(_config) is not null) return false;
        _process.Start(_config);
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            try
            {
                if (await _client.HealthAsync() is { } h && (version.Length == 0 || h.Version == version))
                {
                    _health = h;
                    return true;
                }
            }
            catch { }
            if (!_process.IsRunning) return false;
        }
        return false;
    }

    private void StartBackend()
    {
        try
        {
            _process.Start(_config);
            SetStatus(Loc.T("后端启动中…"));
        }
        catch (Exception ex)
        {
            SetError(Loc.F("后端启动失败：{0}", ex.Message));
        }
    }

    private void StopBackend()
    {
        if (_process.IsRunning) _process.Stop();
        else _ = Run(_client.ShutdownAsync);
        _health = null;
        SetStatus(Loc.T("后端已停止"));
    }

    private Task Run(Func<Task> action) => Task.Run(async () =>
    {
        try { await action(); }
        catch (Exception ex) { SetError(ex.Message); }
    });

    // ------------------------------------------------------------------ 识别与重定向

    /// <summary>预设的值：有保存过的用保存的，否则用内置预设。</summary>
    private RetargetParams LoadParams(string preset)
    {
        if (!_config.SavedParams.TryGetValue(preset, out var saved)) return RetargetParams.Preset(preset);
        var p = saved.Clone();
        p.Offsets.Clear();
        return p;
    }

    private static string ParamsSnapshot(RetargetParams p)
    {
        var c = p.Clone();
        c.Offsets.Clear();
        return System.Text.Json.JsonSerializer.Serialize(c);
    }

    /// <summary>调整过的值按预设保存。拖动滑杆时不写，松手后再写。</summary>
    private void PersistParams()
    {
        if (!_paramsDirty || ImGui.IsAnyItemActive()) return;
        _paramsDirty = false;
        var snapshot = ParamsSnapshot(_params);
        if (snapshot == _savedParams) return;
        var saved = _params.Clone();
        saved.Offsets.Clear();
        _config.SavedParams[_preset] = saved;
        _pi.SavePluginConfig(_config);
        _savedParams = snapshot;
    }

    private void RequestRetarget(bool push)
    {
        _paramsDirty = true;
        if (_session is null || _chatPoseActive || _chatTask is not null) return;
        _rtPush |= push;
        if (_rtBusy)
        {
            _rtDirty = true;
            return;
        }
        _rtBusy = true;
        _ = Task.Run(RetargetLoop);
    }

    private async Task RetargetLoop()
    {
        var activeRevision = Volatile.Read(ref _previewOnlyRevision);
        try
        {
            do
            {
                if (_chatPoseActive || _chatTask is not null || _session is not { } session) break;
                var previewOnlyRevision = Volatile.Read(ref _previewOnlyRevision);
                activeRevision = previewOnlyRevision;
                var ct = _imagePoseToken;
                _rtDirty = false;
                var push = _rtPush;
                _rtPush = false;
                if (push && _resetPending)
                {
                    _resetPending = false;
                    await ClearBrioPoseAsync(ct);
                }
                var reference = await ReferenceAsync();
                if (session != _session || previewOnlyRevision != Volatile.Read(ref _previewOnlyRevision)) continue;
                var trust = _referenceSignature is { } sig && _cleanSignatures.Contains(sig);
                var result = await _client.RetargetAsync(session.SessionId, _person, _params.Clone(), reference,
                                                         _raceOverride ?? _detectedRace, trust);
                if (session != _session || previewOnlyRevision != Volatile.Read(ref _previewOnlyRevision) ||
                    _chatPoseActive || _chatTask is not null) continue;
                // 游戏骨长不对（换种族前残留的 Brio 姿势）：先清除再算一次；清除后仍不符就是 Customize+ / 改体型 MOD
                if (push && result.ReferenceStale && !trust && await ClearBrioPoseAsync(ct))
                {
                    _rtPush = true;
                    _rtDirty = true;
                    continue;
                }
                _result = result;
                if (push) await PushAsync(result.Pose, _actor, ct);
            } while (_rtDirty || _rtPush);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (activeRevision == Volatile.Read(ref _previewOnlyRevision))
                SetError(Loc.F("重定向失败：{0}", ex.Message));
        }
        finally
        {
            _rtBusy = false;
            if ((_rtDirty || _rtPush) && _session is not null && !_imageLifetime.IsCancellationRequested)
                RequestRetarget(false);
        }
    }

    /// <summary>清除 Brio 姿势栈后重新读取参考姿势；这个身份之后的骨长按游戏实际值计算。</summary>
    private async Task<bool> ClearBrioPoseAsync(CancellationToken ct = default)
    {
        if (!await _applier.ResetPoseAsync(_actor, ct)) return false;
        ct.ThrowIfCancellationRequested();
        _reference = null;
        if (await _applier.IdentifyAsync(_actor) is { } id)
        {
            _cleanSignatures.Add(id.Signature);
            _backups.Remove(id.Signature);   // 清除前读到的是残留的错误姿势，不能拿来还原
        }
        _pushStatus ??= Loc.T("已清除换种族前残留的 Brio 姿势并重新应用");
        return true;
    }

    /// <summary>游戏内角色当前姿势：骨长/缩放按游戏实际值（种族、Customize+）。每次计算都重新读取，每个身份的第一份留作还原备份。</summary>
    private async Task<string?> ReferenceAsync()
    {
        var id = await _applier.IdentifyAsync(_actor);
        if (id is null) return null;
        _detectedRace = id.Race;
        // 不缓存：用户在 Brio 里重置 / 手动改了姿势后身份不变，缓存的旧姿势会让骨长一直是错的。
        // 读回自己上次推送的姿势没问题（骨长不变，n_root 下的离地高度后端不取游戏值）
        var pose = await _applier.GetPoseAsync(_actor);
        if (pose is null) return null;
        _reference = pose;
        _referenceSignature = id.Signature;
        _backups.TryAdd(id.Signature, pose);
        return pose;
    }

    private Task PushAsync(string pose) => PushAsync(pose, _actor);

    private async Task PushAsync(string pose, int? actor, CancellationToken ct = default, bool freeze = true,
                                 string? previewPose = null)
    {
        if (!_clientState.IsGPosing)
        {
            SetStatus(Loc.T("不在 GPose：只更新了预览"), Yellow);
            return;
        }
        var status = _pushStatus ?? Loc.T("已应用到角色");
        _pushStatus = null;
        string? error;
        try
        {
            error = await _applier.ApplyAsync(actor, pose, ct, freeze);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        if (error is null)
        {
            _lastAppliedPreviewPose = previewPose ?? pose;
            _pushedIndex = (await _applier.IdentifyAsync(actor))?.Index;
            SetStatus(status, Green);
        }
        else SetError(error);
    }

    private void ApplyNow()
    {
        if (_chatPoseActive && _result is { } chatResult)
        {
            var revision = _chatRevision;
            var identity = _applier.Identify(_actor);
            if (identity is null)
            {
                SetError(Loc.T("找不到角色（请在 GPose 中选中目标）"));
                return;
            }
            _ = Run(async () =>
            {
                var currentIdentity = await _applier.IdentifyAsync(identity.Index);
                if (revision != _chatRevision || currentIdentity?.Signature != identity.Signature) return;
                if (_resetPending)
                {
                    var reset = await _applier.ResetPoseAsync(identity.Index);
                    if (revision != _chatRevision) return;
                    if (!reset) throw new InvalidOperationException(Loc.T("无法重置角色姿势，请检查 Brio"));
                    _reference = null;
                    _cleanSignatures.Add(identity.Signature);
                    _backups.Remove(identity.Signature);
                    _resetPending = false;
                }
                var reference = await _applier.GetPoseAsync(identity.Index);
                currentIdentity = await _applier.IdentifyAsync(identity.Index);
                if (revision != _chatRevision || _resetPending || currentIdentity?.Signature != identity.Signature) return;
                // 纯文字生成也保存角色原姿势；固定 ObjectIndex，避免等待时切换目标后误应用。
                if (reference is not null) _backups.TryAdd(identity.Signature, reference);
                await PushAsync(chatResult.ApplyPose ?? chatResult.Pose, identity.Index, freeze: !chatResult.FacialOnly,
                                previewPose: chatResult.Pose);
            });
            return;
        }
        // 结果是按残留姿势算的、或刚换过种族：重新计算（会先清除 Brio 残留姿势）
        if (_result is { ReferenceStale: false } r && !_resetPending)
        {
            var ct = _imagePoseToken;
            _ = Run(() => PushAsync(r.Pose, _actor, ct));
        }
        else RequestRetarget(true);
    }

    private void Restore()
    {
        if (_currentSignature is null || !_backups.TryGetValue(_currentSignature, out var backup)) return;
        _ = Run(async () =>
        {
            var error = await _applier.ApplyAsync(_actor, backup);
            if (error is null) SetStatus(Loc.T("已还原为第一次应用前的姿势"), Green);
            else SetError(error);
        });
    }

    private void ReplicateCamera()
    {
        if (_session is null) return;
        if (!_server.IsRunning)
        {
            SetError(Loc.T("复刻镜头需要 Posegraph HTTP 服务（/posegraph on）"));
            return;
        }
        SetStatus(Loc.T("复刻镜头中（约 10 秒，画面会跳动）…"), Yellow);
        _ = Run(async () =>
        {
            var r = await _client.CameraAsync(_session.SessionId, _person, _params.Clone(), _actor, _raceOverride ?? _detectedRace);
            SetStatus(Loc.F("镜头已复刻：机位误差 {0:0.0}cm，构图偏差 {1:0.0}%", r.PositionErrorCm, r.FramingErrorPct), Green);
        });
    }

    private void Export()
    {
        if (_result is not { } r) return;
        var name = _session?.Name ?? "pose";
        var dir = Directory.Exists(_config.ExportDir) ? _config.ExportDir : null;
        _fileDialog.SaveFileDialog(Loc.T("导出 Brio 姿势"), ".pose", name, ".pose", (ok, path) =>
        {
            if (!ok) return;
            try
            {
                File.WriteAllText(path, r.Pose);
                _config.ExportDir = Path.GetDirectoryName(path) ?? "";
                _pi.SavePluginConfig(_config);
                SetStatus(Loc.F("已导出 {0}", Path.GetFileName(path)), Green);
            }
            catch (Exception ex)
            {
                SetError(Loc.F("导出失败：{0}", ex.Message));
            }
        }, dir, true);
    }

    private void OpenImageDialog()
    {
        var dir = Directory.Exists(_config.LastImageDir) ? _config.LastImageDir : null;
        _fileDialog.OpenFileDialog(Loc.T("选择图片"), Loc.T("图片") + ImageExtensions, (ok, paths) =>
        {
            if (!ok || paths.Count == 0) return;
            _config.LastImageDir = Path.GetDirectoryName(paths[0]) ?? "";
            _pi.SavePluginConfig(_config);
            ImportImagePaths(paths);
        }, 0, dir, true);
    }

    // ------------------------------------------------------------------ 绘制

    public override void Draw()
    {
        PollImages();
        PollHealth();
        RefreshActors();
        PollChat();
        PollAdvice();
        PollModelCatalog();
        PersistParams();
        DragSource();

        DrawStatusBar();
        DrawToolbar();

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
        _drawSelectTab = _selectTab;
        _selectTab = null;
        // ImGui retains tab order across plugin hot reloads; use a new layout ID
        // so existing sessions adopt the chat-before-settings order as well.
        if (ImGui.BeginTabBar("##tabs-chat-before-settings-v2"))
        {
            Tab(0, Loc.T("图像"), DrawImageTab, footer);
            Tab(1, "3D", DrawStickTab, footer);
            Tab(2, Loc.T("调整"), DrawTuningTab, footer);
            var warnings = _result?.Warnings.Length ?? 0;
            Tab(3, warnings > 0 ? Loc.F("提示 ({0})###warn", warnings) : Loc.T("提示###warn"), DrawWarningsTab, footer);
            Tab(5, Loc.T("对话"), DrawChatTab, footer);
            Tab(4, Loc.T("设置"), DrawSettingsTab, footer);
            ImGui.EndTabBar();
        }
        DrawFooter();
        _fileDialog.Draw();
    }

    private void Tab(int index, string label, Action draw, float footer)
    {
        if (!ImGui.BeginTabItem(label, _drawSelectTab == index ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) return;
        using (var child = ImRaii.Child($"##tab{index}", new Vector2(0, -footer)))
            if (child) draw();
        ImGui.EndTabItem();
    }

    private void DrawToolbar()
    {
        if (IconButton("##open", FontAwesomeIcon.FolderOpen, Loc.T("选择图片（支持多选）或文件夹，也可以拖到图像页"), !ImageImportBusy))
            ImGui.OpenPopup("##imageImport");
        DrawImageImportMenu();
        ImGui.SameLine();
        if (IconButton("##clipboard", FontAwesomeIcon.Clipboard, Loc.T("识别剪切板中的图像"), !ImageImportBusy))
            ImportClipboardImage();
        ImGui.SameLine();
        if (IconButton("##again", FontAwesomeIcon.Redo, Loc.T("重新识别当前图片"), _selectedImage is not null && !ImageImportBusy))
            RecognizeImageAgain(_selectedImage!);
        ImGui.SameLine();

        // 角色
        var label = _actor is { } actor ? ActorName(actor) : Loc.T("GPose 目标");
        ImGui.SetNextItemWidth(ComboWidth(label, 140));
        if (ImGui.BeginCombo("##actor", label))
        {
            if (ImGui.Selectable(Loc.T("GPose 目标"), _actor is null)) SelectActor(null);
            foreach (var (index, name) in _actors)
                if (ImGui.Selectable($"{name}##{index}", _actor == index)) SelectActor(index);
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("要摆姿势的角色"));
        ImGui.SameLine();

        // 多人（预留）：目前整图按一个人识别
        var humans = _session?.Humans.Length ?? 0;
        using (ImRaii.Disabled(humans <= 1))
        {
            var personLabel = humans <= 1 ? Loc.T("1 人") : Loc.F("第 {0} 人", _person + 1);
            ImGui.SetNextItemWidth(ComboWidth(personLabel, 70));
            if (ImGui.BeginCombo("##person", personLabel))
            {
                for (var i = 0; i < humans; i++)
                    if (ImGui.Selectable(Loc.F("第 {0} 人", i + 1), _person == i))
                    {
                        _person = i;
                        ResetChatContext(discardPose: true);
                        RequestRetarget(false);
                    }
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Loc.T("图中人物（多人识别预留，目前整图按一个人处理）"));
        ImGui.SameLine();
        DrawRaceCombo();
        ImGui.SameLine();
        IconButton("##video", FontAwesomeIcon.Film, Loc.T("视频逐帧（预留）"), false);
        if (_recognizing || _rtBusy)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, _recognizing ? Loc.T("识别中…") : Loc.T("计算中…"));
        }
    }

    /// <summary>下拉框宽度：放得下当前文字（加箭头和内边距），不小于 min，也不超过剩余宽度。</summary>
    private static float ComboWidth(string text, float min)
    {
        var style = ImGui.GetStyle();
        var want = ImGui.CalcTextSize(text).X + ImGui.GetFrameHeight() + style.FramePadding.X * 2 + 4;
        return Math.Min(Math.Max(min * ImGuiHelpers.GlobalScale, want), Math.Max(60, ImGui.GetContentRegionAvail().X));
    }

    /// <summary>下一帧刷新角色时按新角色切换状态并重新计算（见 SwitchActor）。</summary>
    private void SelectActor(int? index)
    {
        ResetChatContext(discardPose: true);
        _actor = index;
        _lastActors = DateTime.MinValue;
    }

    /// <summary>种族骨架：默认按角色外貌自动识别，可手动指定（换种族 MOD、识别不到时）。</summary>
    private void DrawRaceCombo()
    {
        var used = _result?.Race;
        var label = _raceOverride is not null ? Races.Label(_raceOverride)
            : used is not null ? used.Name : _detectedRace is not null ? Races.Label(_detectedRace) : Loc.T("种族");
        var warn = used is { Warnings.Length: > 0 };
        var raceLabel = warn ? label + " (!)" : label;
        ImGui.SetNextItemWidth(ComboWidth(raceLabel, 130));
        if (ImGui.BeginCombo("##race", raceLabel))
        {
            if (ImGui.Selectable(Loc.F("自动（{0}）", (_detectedRace is null ? Loc.T("按骨长推断") : Races.Label(_detectedRace))), _raceOverride is null))
                SelectRace(null);
            string? group = null;
            foreach (var r in Races.All)
            {
                if (r.Group != group)
                {
                    ImGui.TextDisabled(Loc.T(r.Group));
                    group = r.Group;
                }
                if (ImGui.Selectable(Loc.F("  {0}{1}##{2}", Loc.T(r.Name), !r.Supported ? Loc.T("（未适配）") : "", r.Code), _raceOverride == r.Code))
                    SelectRace(r.Code);
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
        {
            var source = used?.Source switch
            {
                "plugin" => _raceOverride is null ? Loc.T("按角色外貌识别") : Loc.T("手动指定"),
                "guess" => Loc.T("按游戏内骨长推断"),
                "default" => Loc.T("未能识别，使用默认"),
                _ => Loc.T("尚未计算"),
            };
            var tip = used is null ? Loc.T("种族骨架（应用姿势前自动识别）")
                : Loc.F("骨架：{0}（{1}，{2}），{3}", used.Name, used.Code, used.Group, source) + (used.Warnings.Length > 0 ? "\n" + string.Join("\n", used.Warnings) : "");
            ImGui.SetTooltip(tip);
        }
    }

    private void SelectRace(string? code)
    {
        ResetChatContext(discardPose: true);
        _raceOverride = code;
        RequestRetarget(_config.PushOnRelease);
    }

    private void DrawFooter()
    {
        var hasResult = _result is not null;
        if (IconButton("##apply", FontAwesomeIcon.Play, Loc.T("应用到角色"), hasResult && _clientState.IsGPosing && _chatTask is null && _imageLoadTask is null && !_rtBusy))
            ApplyNow();
        ImGui.SameLine();
        if (IconButton("##camera", FontAwesomeIcon.Video, Loc.T("复刻照片镜头（会改动 GPose 相机）"), _session is not null && _clientState.IsGPosing && !_chatPoseActive && _chatTask is null))
            ReplicateCamera();
        ImGui.SameLine();
        if (IconButton("##restore", FontAwesomeIcon.Undo, Loc.T("还原为第一次应用前的姿势"), _currentSignature is not null && _backups.ContainsKey(_currentSignature) && _clientState.IsGPosing))
            Restore();
        ImGui.SameLine();
        if (IconButton("##export", FontAwesomeIcon.Save, Loc.T("导出 Brio 姿势文件（.pose）"), hasResult))
            Export();
        ImGui.SameLine();
        var autoPush = _config.PushOnRelease;
        if (ImGui.Checkbox("##autopush", ref autoPush))
        {
            _config.PushOnRelease = autoPush;
            _pi.SavePluginConfig(_config);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("松开滑杆时自动应用到角色"));

        ImGui.TextColored(_statusColor, _statusText);
        if (ImGui.IsItemHovered() && _statusText.Length > 30) ImGui.SetTooltip(_statusText);
    }

    // ---------------- 图像页

    private void DrawImageTab()
    {
        if (_images.Count <= 1)
        {
            DrawImagePreview();
            return;
        }
        var avail = ImGui.GetContentRegionAvail();
        var width = Math.Min(190 * ImGuiHelpers.GlobalScale, avail.X * 0.42f);
        using (var list = ImRaii.Child("##imageList", new Vector2(width, 0), true))
            if (list) DrawImageList();
        ImGui.SameLine();
        using (var preview = ImRaii.Child("##imagePreview", new Vector2(0, 0)))
            if (preview) DrawImagePreview();
    }

    private void DrawImagePreview()
    {
        var showMhr = _config.ShowMhrOverlay;
        var showFf = _config.ShowFfxivOverlay;
        var overlayRowWidth = ImGui.CalcTextSize(Loc.T("识别骨架（绿）")).X
            + ImGui.CalcTextSize(Loc.T("FF14 骨架（橙）")).X
            + ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.X * 3;
        var sameRow = ImGui.GetContentRegionAvail().X >= overlayRowWidth;
        var toggled = ImGui.Checkbox(Loc.T("识别骨架（绿）"), ref showMhr);
        if (sameRow) ImGui.SameLine();
        toggled |= ImGui.Checkbox(Loc.T("FF14 骨架（橙）"), ref showFf);
        if (toggled)
        {
            _config.ShowMhrOverlay = showMhr;
            _config.ShowFfxivOverlay = showFf;
            _pi.SavePluginConfig(_config);
        }

        var avail = ImGui.GetContentRegionAvail();
        if (_texture is null || _session is null)
        {
            var message = _imageLoadTask is not null ? Loc.T("加载所选图片中…")
                : _recognizing && _images.Any(i => i.Session is not null) ? Loc.T("从左侧选择已识别的图片以预览和应用")
                : _recognizing ? Loc.T("正在预识别全部图片…")
                : _images.Count > 1 ? Loc.T("从左侧列表选择图片以加载预览")
                : Loc.T("点左上角文件夹选图\n或把图片拖到这里");
            ImGui.Button(message, new Vector2(avail.X, Math.Max(80, avail.Y)));
            DropTarget();
            return;
        }
        var imgW = _session.ImageSize[0];
        var imgH = _session.ImageSize[1];
        var scale = Math.Min(Math.Max(1, avail.X) / imgW, Math.Max(1, avail.Y) / imgH);
        var size = new Vector2(imgW, imgH) * scale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail.X - size.X) / 2);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Image(_texture.Handle, size);
        // Image 没有 ID，不能直接作拖放目标；在同一位置放一个隐形按钮接收
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##imageDrop", size);
        DropTarget();

        if (_result?.Overlay is not { } ov) return;
        var dl = ImGui.GetWindowDrawList();
        if (showMhr) DrawSegments(dl, ov.Mhr, origin, scale, MhrGreen);
        if (showFf) DrawSegments(dl, ov.Ffxiv, origin, scale, Orange);
    }

    /// <summary>左侧实线，右侧细线加端点（检查左右是否颠倒），与 preview_overlay.py 一致。</summary>
    private static void DrawSegments(ImDrawListPtr dl, Segment[] segments, Vector2 origin, float scale, Vector4 color)
    {
        var col = ImGui.GetColorU32(color);
        foreach (var s in segments)
        {
            var a = origin + new Vector2(s.A[0], s.A[1]) * scale;
            var b = origin + new Vector2(s.B[0], s.B[1]) * scale;
            var right = s.Side == "r";
            dl.AddLine(a, b, col, right ? 1.5f : 2.5f);
            if (right) dl.AddCircleFilled(b, 2.5f, col);
        }
    }

    private const string DragLabel = "PosegraphImage";

    private static bool IsImage(string path)
        => ImageFiles.IsImage(path);

    /// <summary>
    /// 外部文件拖放分两步：每帧用 CreateImGuiSource 把系统拖动转成 ImGui 拖放负载，再由目标区域用 CreateImGuiTarget 接收。
    /// 拖着图片进入窗口时自动切到图像页。
    /// </summary>
    private void DragSource()
    {
        _dragDrop.CreateImGuiSource(DragLabel, m => m.Files.Any(IsImageInput), m =>
        {
            ImGui.TextUnformatted(Loc.F("松开以导入 {0} 个文件或文件夹", m.Files.Count(IsImageInput)));
            return true;
        });
        if (_dragDrop.IsDragging && _dragDrop.Files.Any(IsImageInput))
            _selectTab = 0;
    }

    private void DropTarget()
    {
        if (!_dragDrop.CreateImGuiTarget(DragLabel, out var files, out _)) return;
        ImportImagePaths(files);
    }

    // ---------------- 3D 火柴人

    private static readonly Dictionary<string, string> JointNames = new()
    {
        ["j_kosi"] = "腰", ["j_sebo_a"] = "腰椎", ["j_sebo_b"] = "上背", ["j_sebo_c"] = "胸", ["j_kubi"] = "颈", ["j_kao"] = "头",
        ["j_sako"] = "锁骨", ["j_ude_a"] = "肩（上臂）", ["j_ude_b"] = "肘（前臂）", ["j_te"] = "手腕", ["j_naka_a"] = "手",
        ["j_asi_a"] = "髋（大腿）", ["j_asi_c"] = "膝（小腿）", ["j_asi_d"] = "脚踝", ["j_asi_e"] = "脚尖",
    };

    private static string JointLabel(string bone)
    {
        var side = bone.EndsWith("_l") ? Loc.T("左") : bone.EndsWith("_r") ? Loc.T("右") : "";
        var key = side.Length > 0 ? bone[..^2] : bone;
        return side + (JointNames.TryGetValue(key, out var n) ? Loc.T(n) : bone);
    }

    private void DrawStickTab()
    {
        if (_result?.Stick is not { } stick)
        {
            ImGui.TextDisabled(Loc.T("识别后在这里查看 FF14 骨架（拖动旋转、滚轮缩放、点选关节微调）"));
            return;
        }
        var avail = ImGui.GetContentRegionAvail();
        var panel = _selected is null ? ImGui.GetTextLineHeightWithSpacing() : ImGui.GetFrameHeightWithSpacing() * 4 + 4;
        var size = new Vector2(avail.X, Math.Max(140, avail.Y - panel));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##stick", size);
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3))
        {
            _yaw += io.MouseDelta.X * 0.01f;
            _pitch = Math.Clamp(_pitch + io.MouseDelta.Y * 0.01f, -1.5f, 1.5f);
            _dragged = true;
        }
        if (ImGui.IsItemHovered() && io.MouseWheel != 0)
            _zoom = Math.Clamp(_zoom * (1 + io.MouseWheel * 0.1f), 0.3f, 5f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("拖动旋转，滚轮缩放，点击关节选中；双击复位视角"));
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            (_yaw, _pitch, _zoom) = (0, 0.15f, 1);

        var joints = stick.Joints.ToDictionary(j => j.Name);
        var center = joints.TryGetValue("j_kosi", out var hip) ? ToVec(hip.Pos) : Vector3.Zero;
        var scale = size.Y / 2.1f * _zoom;
        var mid = origin + size / 2;
        Vector2 Project(Vector3 p)
        {
            p -= center;
            var (sy, cy) = MathF.SinCos(_yaw);
            var (sp, cp) = MathF.SinCos(_pitch);
            var x = p.X * cy + p.Z * sy;
            var z = -p.X * sy + p.Z * cy;
            var y = p.Y * cp - z * sp;
            // 从角色正前方看：模型 +x（角色左侧）在屏幕右侧
            return mid + new Vector2(x, -y) * scale;
        }

        if (ImGui.IsItemDeactivated())
        {
            if (!_dragged)
            {
                var mouse = ImGui.GetMousePos();
                var hit = stick.Joints.Select(j => (j.Name, D: Vector2.Distance(Project(ToVec(j.Pos)), mouse)))
                    .Where(t => t.D < 10).OrderBy(t => t.D).Select(t => t.Name).FirstOrDefault();
                _selected = hit;
            }
            _dragged = false;
        }

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, origin + size, true);
        dl.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 0.85f)), 4);

        // 地面网格与朝向
        var floor = stick.Joints.Min(j => j.Pos[1]);
        var grid = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.12f));
        for (var i = -3; i <= 3; i++)
        {
            var t = i * 0.25f;
            dl.AddLine(Project(new Vector3(center.X + t, floor, center.Z - 0.75f)), Project(new Vector3(center.X + t, floor, center.Z + 0.75f)), grid);
            dl.AddLine(Project(new Vector3(center.X - 0.75f, floor, center.Z + t)), Project(new Vector3(center.X + 0.75f, floor, center.Z + t)), grid);
        }
        var front = Project(new Vector3(center.X, floor, center.Z + 0.6f));
        dl.AddLine(Project(new Vector3(center.X, floor, center.Z)), front, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.35f)), 2);
        dl.AddText(front, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.5f)), Loc.T("前"));

        if (_config.ShowMhrInStick)
        {
            var mc = ImGui.GetColorU32(MhrGreen with { W = 0.6f });
            foreach (var s in stick.Mhr)
                dl.AddLine(Project(new Vector3(s.A[0], s.A[1], s.A[2])), Project(new Vector3(s.B[0], s.B[1], s.B[2])), mc, 1.5f);
        }

        var severity = _result.Severity;
        foreach (var j in stick.Joints)
        {
            if (j.Parent is null || !joints.TryGetValue(j.Parent, out var parent)) continue;
            var color = j.Name.EndsWith("_l") ? Orange : j.Name.EndsWith("_r") ? new Vector4(0.35f, 0.65f, 1f, 1) : new Vector4(0.9f, 0.9f, 0.9f, 1);
            dl.AddLine(Project(ToVec(parent.Pos)), Project(ToVec(j.Pos)), ImGui.GetColorU32(color), 3);
        }
        foreach (var j in stick.Joints)
        {
            var p = Project(ToVec(j.Pos));
            var level = severity.GetValueOrDefault(j.Name);
            var color = level == "bad" ? Red : level == "warn" ? Yellow : new Vector4(0.85f, 0.85f, 0.85f, 1);
            var radius = level is null ? 3f : 5f;
            if (_params.Offsets.TryGetValue(j.Name, out var off) && off.Any(v => v != 0))
                dl.AddCircle(p, radius + 3, ImGui.GetColorU32(new Vector4(0.7f, 0.5f, 1f, 1)), 0, 1.5f);
            dl.AddCircleFilled(p, radius, ImGui.GetColorU32(color));
            if (j.Name == _selected)
                dl.AddCircle(p, radius + 5, ImGui.GetColorU32(new Vector4(0.3f, 0.9f, 1f, 1)), 0, 2);
        }
        dl.PopClipRect();

        DrawJointPanel();
    }

    private static Vector3 ToVec(float[] p) => new(p[0], p[1], p[2]);

    private void DrawJointPanel()
    {
        if (_chatPoseActive || _chatTask is not null)
        {
            ImGui.TextWrapped(Loc.T("请在“对话”页继续调整。"));
            return;
        }
        if (_selected is not { } bone)
        {
            var mhr = _config.ShowMhrInStick;
            if (ImGui.Checkbox(Loc.T("叠加识别骨架"), ref mhr))
            {
                _config.ShowMhrInStick = mhr;
                _pi.SavePluginConfig(_config);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(Loc.T("黄/红点：超出游戏常见范围"));
            return;
        }
        ImGui.Text($"{JointLabel(bone)}  ");
        ImGui.SameLine();
        ImGui.TextDisabled(bone);
        ImGui.SameLine();
        if (IconButton("##resetJoint", FontAwesomeIcon.UndoAlt, Loc.T("清除这个关节的手动偏移"), _params.Offsets.ContainsKey(bone)))
        {
            _params.Offsets.Remove(bone);
            RequestRetarget(_config.PushOnRelease);
        }
        ImGui.SameLine();
        if (IconButton("##deselect", FontAwesomeIcon.Times, Loc.T("取消选中")))
            _selected = null;
        foreach (var w in _result?.Warnings.Where(w => w.Bone == bone) ?? [])
        {
            ImGui.SameLine();
            ImGui.TextColored(w.Level == "bad" ? Red : Yellow, $"{w.Value:0}°/{w.Limit:0}°");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(w.Message);
        }

        var off = _params.Offsets.TryGetValue(bone, out var v) ? (float[])v.Clone() : new float[3];
        string[] axes = [Loc.T("前后俯仰（绕左右轴）"), Loc.T("水平转（绕竖直轴）"), Loc.T("侧倾（绕前后轴）")];
        var width = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3;
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            var changed = ImGui.SliderFloat($"##off{i}", ref off[i], -90, 90, "%.0f°");
            ItemTip(Loc.F("{0}：绕这个关节转动它下面的整段肢体（模型坐标轴）。右键归零", axes[i]));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                off[i] = 0;
                changed = true;
            }
            if (changed)
            {
                _params.Offsets[bone] = off;
                RequestRetarget(false);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() || (changed && ImGui.IsItemClicked(ImGuiMouseButton.Right)))
                RequestRetarget(_config.PushOnRelease);
        }
    }

    private static void ItemTip(string tip)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    // ---------------- 调整页

    /// <summary>拖动中只刷新预览，松手才应用到角色；右键恢复默认值。</summary>
    private void Slider(string label, ref float value, float min, float max, string format, float def, string tip)
    {
        ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 90));
        var changed = ImGui.SliderFloat(label, ref value, min, max, format);
        ItemTip(tip + Loc.F("\n右键恢复默认（{0:0.##}）", def));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            value = def;
            RequestRetarget(_config.PushOnRelease);
            return;
        }
        if (changed) RequestRetarget(false);
        if (ImGui.IsItemDeactivatedAfterEdit()) RequestRetarget(_config.PushOnRelease);
    }

    private void Toggle(string label, ref bool value, string tip)
    {
        if (ImGui.Checkbox(label, ref value)) RequestRetarget(_config.PushOnRelease);
        ItemTip(tip);
    }

    private void DrawTuningTab()
    {
        if (_chatPoseActive || _chatTask is not null)
        {
            ImGui.TextWrapped(Loc.T("请在“对话”页继续调整。"));
            using (ImRaii.Disabled(_session is null))
                if (ImGui.Button(Loc.T("返回图像姿势")))
                {
                    ResetChatContext(discardPose: true);
                    _chatImageDetached = false;
                    RequestRetarget(false);
                }
            return;
        }
        ImGui.SetNextItemWidth(110);
        var presetLabel = _preset == "illustration" ? Loc.T("插画") : Loc.T("照片");
        if (ImGui.BeginCombo("##preset", presetLabel))
        {
            foreach (var (key, name) in new[] { ("photo", Loc.T("照片")), ("illustration", Loc.T("插画")) })
                if (ImGui.Selectable(name, _preset == key))
                {
                    _preset = key;
                    var offsets = _params.Offsets;
                    _params = LoadParams(key);
                    _params.Offsets = offsets;
                    _savedParams = ParamsSnapshot(_params);
                    _config.DefaultPreset = key;
                    _pi.SavePluginConfig(_config);
                    RequestRetarget(_config.PushOnRelease);
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("预设：插画会把颈/头幅度设为 0.7（游戏实测合适）") + "\n" + Loc.T("调整过的值按预设自动保存，点右边的按钮恢复默认"));
        ImGui.SameLine();
        if (IconButton("##resetAll", FontAwesomeIcon.UndoAlt, Loc.T("全部恢复预设值（含手动偏移）")))
        {
            _params = RetargetParams.Preset(_preset);
            if (_config.SavedParams.Remove(_preset)) _pi.SavePluginConfig(_config);
            _savedParams = ParamsSnapshot(_params);
            RequestRetarget(_config.PushOnRelease);
        }

        if (ImGui.CollapsingHeader(Loc.T("幅度"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var (key, label, tip) in AmplitudeGroups)
            {
                var v = _params.Amp(key);
                var before = v;
                Slider($"{Loc.T(label)}##amp{key}", ref v, 0, 1.5f, "%.2f", RetargetParams.Preset(_preset).Amp(key),
                       Loc.T(tip) + Loc.T("\n1 = 照片原样，<1 收敛，>1 夸张"));
                if (v != before) _params.Amplitude[key] = v;
            }
        }
        if (ImGui.CollapsingHeader(Loc.T("手部末端补偿")))
        {
            var p = _params;
            var s = p.HandStrength;
            Slider(Loc.T("强度##hand"), ref s, 0, 1, "%.2f", 1, Loc.T("手靠近头/胸/髋时，用 IK 保持手与身体部位的相对位置（两套骨架比例不同，只复制角度会错位）。0 关闭"));
            p.HandStrength = s;
            var near = p.HandNear * 100;
            Slider(Loc.T("完全生效##near"), ref near, 5, 60, "%.0f cm", 20, Loc.T("手腕距部位小于这个距离时完全按部位相对位置放置"));
            p.HandNear = Math.Min(near, p.HandFar * 100 - 1) / 100;
            var far = p.HandFar * 100;
            Slider(Loc.T("不再生效##far"), ref far, 10, 80, "%.0f cm", 40, Loc.T("手腕距部位大于这个距离时只复制角度"));
            p.HandFar = Math.Max(far, p.HandNear * 100 + 1) / 100;
        }
        if (ImGui.CollapsingHeader(Loc.T("站姿")))
        {
            var upright = _params.Upright;
            Toggle(Loc.T("重力校正"), ref upright, Loc.T("照片相机俯仰/侧倾会让人整体歪斜：双脚着地时把重心摆到支撑脚正上方。半身照（脚不在画面内）自动跳过"));
            _params.Upright = upright;
            using (ImRaii.Disabled(!upright))
            {
                var max = _params.UprightMaxDeg;
                Slider(Loc.T("校正上限##upmax"), ref max, 0, 45, "%.0f°", 30, Loc.T("倾斜超过这个角度视为有意的姿势（如倾身），不校正"));
                _params.UprightMaxDeg = max;
            }
            var ground = _params.Ground;
            Toggle(Loc.T("自动贴地"), ref ground, Loc.T("把最低的关节放到地面（跪坐/躺下不悬空）。跳跃、腾空的图请关闭"));
            _params.Ground = ground;
        }
        if (ImGui.CollapsingHeader(Loc.T("辅助骨")))
        {
            var h = _params.HelperStrength;
            Slider(Loc.T("联动强度##helper"), ref h, 0, 1.5f, "%.2f", 1, Loc.T("肘/腕/肩/膝辅助骨按游戏动画采样的联动规则转动，防止关节处蒙皮变形。1 为游戏拟合值"));
            _params.HelperStrength = h;
            var experiment = _params.MidlanderFemaleExperiment;
            if (ImGui.Checkbox(Loc.T("中原女实验适配"), ref experiment))
            {
                _params.MidlanderFemaleExperiment = experiment;
                Interlocked.Increment(ref _previewOnlyRevision);
                _rtPush = false;
                _result = null;
                RequestRetarget(false);
            }
            ItemTip(Loc.T("标定范围：原版中原女，身高50、胸围0、脸模f0211。试用胸围0尺寸、肘辅助骨缩放和膝辅助骨曲线；深屈膝仍需更多实机验证。切换仅刷新预览，请手动应用到角色。"));
        }
        if (_result is { } r)
        {
            ImGui.Separator();
            ImGui.TextDisabled(Loc.F("重定向 {0:0} ms  补偿 {1}", r.ElapsedMs, Compact(r.Compensation)));
        }
    }

    private static string Compact(System.Text.Json.JsonElement e)
    {
        var parts = new List<string>();
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
        foreach (var p in e.EnumerateObject())
        {
            var text = p.Name switch
            {
                "l" or "r" => Loc.F("{0}手 {1:0}cm", (p.Name == "l" ? Loc.T("左") : Loc.T("右")), p.Value.GetProperty("shift_cm").GetDouble()),
                "upright_deg" => Loc.F("重力 {0:0.0}°", p.Value.GetDouble()),
                "ground_drop_cm" => Loc.F("贴地 {0:0}cm", p.Value.GetDouble()),
                _ => null,
            };
            if (text is not null) parts.Add(text);
        }
        return string.Join(Loc.T("，"), parts);
    }

    // ---------------- 提示页

    private void DrawWarningsTab()
    {
        DrawAdvice();
        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("本地规则检查"));
        if (_result is not { } r)
        {
            ImGui.TextDisabled(Loc.T("识别后在这里显示合理性检查"));
            return;
        }
        foreach (var w in r.Race?.Warnings ?? [])
            ImGui.TextColored(Yellow, w);
        if (r.Warnings.Length == 0)
        {
            ImGui.TextColored(Green, Loc.T("没有发现超出游戏常见范围的关节"));
            return;
        }
        for (var i = 0; i < r.Warnings.Length; i++)
        {
            var w = r.Warnings[i];
            var color = w.Level == "bad" ? Red : w.Level == "warn" ? Yellow : Grey;
            StatusIcon(w.Level == "info" ? FontAwesomeIcon.InfoCircle : FontAwesomeIcon.ExclamationTriangle, color,
                       Loc.F("{0}：{1:0.#}（阈值 {2:0.#}）", w.Bone, w.Value, w.Limit));
            ImGui.TextWrapped(Loc.F("{0}：{1}", JointLabel(w.Bone), w.Message));
            ItemTip(Loc.T("点击在 3D 页查看这个关节"));
            if (ImGui.IsItemClicked())
            {
                _selected = w.Bone;
                _selectTab = 1;
            }
            if (!_chatPoseActive && _chatTask is null && w.Group is { } group && w.Suggest is { } suggest && Math.Abs(_params.Amp(group) - suggest) > 0.01f &&
                _params.Amp(group) > suggest)
            {
                var name = Loc.T(AmplitudeGroups.FirstOrDefault(g => g.Key == group).Label ?? group);
                if (ImGui.SmallButton(Loc.F("把{0}幅度设为 {1:0.0#}##sug{2}", name, suggest, i)))
                {
                    _params.Amplitude[group] = suggest;
                    RequestRetarget(_config.PushOnRelease);
                }
            }
            ImGui.Spacing();
        }
    }

    // ---------------- 对话页

    private void CancelChat()
    {
        _chatRevision++;
        _chatCts?.Cancel();
    }

    private void ResetChatContext(bool discardPose)
    {
        if (discardPose) InvalidateAdvice();
        CancelChat();
        _chatHistory = [];
        _chatError = null;
        _chatFreshStart = false;
        if (discardPose)
        {
            _chatPendingBones.Clear();
            _lastAppliedPreviewPose = null;
        }
        Interlocked.Increment(ref _previewOnlyRevision);
        _rtDirty = _rtPush = false;
        if (discardPose && _chatPoseActive)
        {
            _result = null;
            _chatPoseActive = false;
            _selected = null;
        }
    }

    private void SendChat()
    {
        if (_chatTask is not null || ImageWorkBusy || _rtBusy || string.IsNullOrWhiteSpace(_chatPrompt)) return;
        if (!BackendOnline)
        {
            _chatError = Loc.T("后端离线：先点右上角电源按钮启动");
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.ChatApiEndpoint) || string.IsNullOrWhiteSpace(_config.ChatModel))
        {
            _chatError = Loc.T("请先在设置中配置 API 并选择模型");
            _selectTab = 4;
            return;
        }

        _chatError = null;
        _chatSentPrompt = _chatPrompt.Trim();
        _chatRequestRevision = ++_chatRevision;
        Interlocked.Increment(ref _previewOnlyRevision);
        _rtDirty = _rtPush = false;
        var prompt = _chatSentPrompt;
        var history = _chatHistory.ToArray();
        // Brio 尚待清理只影响首次起点；未应用的聊天预览仍是后续轮次的当前姿势。
        var fresh = _chatFreshStart || (_resetPending && !_chatPoseActive);
        if (_result?.Pose == _lastAppliedPreviewPose) _chatPendingBones.Clear();
        if (!fresh && !_chatPoseActive && _result is { } imageResult && imageResult.Pose != _lastAppliedPreviewPose)
            _chatPendingBones.UnionWith(ChatPoseBoneNames(imageResult.Pose));
        _chatRequestFullBase = fresh;
        var pose = fresh ? null : _result?.Pose;
        var actor = _actor;
        var race = _raceOverride ?? _detectedRace;
        var sessionId = fresh || _chatImageDetached ? null : _session?.SessionId;
        var includeImage = _config.ChatIncludeImage && sessionId is not null;
        var ground = _chatGround;
        var provider = new PoseChatProvider(_config.ChatApiEndpoint.Trim(), _chatApiKey, _config.ChatModel.Trim());
        _chatCts = new CancellationTokenSource();
        var ct = _chatCts.Token;
        _chatTask = Task.Run(async () =>
        {
            var snapshot = await _applier.GetSnapshotAsync(actor);
            if (pose is null && !fresh) pose = snapshot?.Pose;
            ct.ThrowIfCancellationRequested();
            return await _client.PoseChatAsync(new PoseChatRequest(prompt, history, pose, race, sessionId,
                                                                  includeImage, ground, provider, snapshot?.Rig, snapshot?.Pose), ct);
        }, ct);
        SetStatus(Loc.T("正在生成动作预览…"), Yellow);
    }

    private void PollChat()
    {
        if (_chatTask is not { IsCompleted: true } task) return;
        try
        {
            var response = task.GetAwaiter().GetResult();
            if (_chatRequestRevision != _chatRevision) return;
            if (_result?.Pose == _lastAppliedPreviewPose) _chatPendingBones.Clear();
            _chatPendingBones.UnionWith(ChatPoseBoneNames(_chatRequestFullBase
                ? response.Result.Pose : response.Result.ApplyPose ?? response.Result.Pose));
            var (applyPose, facialOnly) = BuildChatApplyPose(response.Result.Pose, response.Result.ApplyPose,
                                                           _chatPendingBones);
            _result = response.Result with { ApplyPose = applyPose, FacialOnly = facialOnly };
            _chatHistory = response.History.TakeLast(20).ToArray();
            _chatPoseActive = true;
            _chatFreshStart = false;
            _selected = null;
            if (_chatPrompt.Trim() == _chatSentPrompt) _chatPrompt = "";
            SetStatus(Loc.T("预览已更新"), Green);
        }
        catch (OperationCanceledException)
        {
            if (_chatRequestRevision == _chatRevision) _chatError = Loc.T("动作生成超时，请重试或检查模型服务");
        }
        catch (Exception ex)
        {
            if (_chatRequestRevision == _chatRevision)
            {
                _chatError = ex.Message;
                SetError(Loc.T("动作生成失败，原姿势已保留"));
            }
        }
        finally
        {
            _chatTask = null;
            _chatCts?.Dispose();
            _chatCts = null;
            if (_chatRequestRevision != _chatRevision && !_chatPoseActive && !_chatFreshStart &&
                _session is not null && _imageLoadTask is null) RequestRetarget(false);
        }
    }

    private static string[] ChatPoseBoneNames(string pose)
    {
        using var document = JsonDocument.Parse(pose);
        return document.RootElement.GetProperty("Bones").EnumerateObject().Select(bone => bone.Name).ToArray();
    }

    /// <summary>Combine un-applied turns using only their latest full-pose transforms.</summary>
    internal static (string ApplyPose, bool FacialOnly) BuildChatApplyPose(
        string fullPose, string? latestPatch, IEnumerable<string> pendingBones)
    {
        var full = JsonNode.Parse(fullPose)?.AsObject()
            ?? throw new InvalidOperationException("Pose response is empty");
        var transforms = full["Bones"]?.AsObject()
            ?? throw new InvalidOperationException("Pose response has no bones");
        var patch = JsonNode.Parse(latestPatch ?? fullPose)?.AsObject()
            ?? throw new InvalidOperationException("Pose patch is empty");
        var selected = new JsonObject();
        foreach (var name in pendingBones.Distinct(StringComparer.Ordinal))
            if (transforms.TryGetPropertyValue(name, out var transform) && transform is not null)
                selected[name] = transform.DeepClone();
        patch["Bones"] = selected;
        foreach (var slot in new[] { "MainHand", "OffHand", "Prop", "Ornament" }) patch[slot] = new JsonObject();
        return (patch.ToJsonString(), selected.All(bone => bone.Key.StartsWith("j_f_", StringComparison.Ordinal)));
    }

    private void DrawChatTab()
    {
        ImGui.TextDisabled(Loc.F("模型：{0}", string.IsNullOrWhiteSpace(_config.ChatModel) ? Loc.T("尚未配置") : _config.ChatModel));
        if (ImGui.SmallButton(Loc.T("配置模型"))) _selectTab = 4;
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("新对话")))
        {
            ResetChatContext(discardPose: false);
            SetStatus(Loc.T("已清空对话，保留当前姿势"));
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("从默认站姿开始")))
        {
            ResetChatContext(discardPose: true);
            _chatFreshStart = true;
            _chatImageDetached = true;
            _result = null;
            SetStatus(Loc.T("已切换为默认站姿起点"));
        }

        var includeImage = _config.ChatIncludeImage && !_chatImageDetached;
        using (ImRaii.Disabled(_session is null || _chatFreshStart || _chatImageDetached || _chatTask is not null))
            if (ImGui.Checkbox(Loc.T("发送参考图"), ref includeImage))
            {
                _config.ChatIncludeImage = includeImage;
                _pi.SavePluginConfig(_config);
            }
        ItemTip(Loc.T("压缩后发送到模型 API，需要视觉模型。"));
        ImGui.SameLine();
        ImGui.Checkbox(Loc.T("贴地##chatGround"), ref _chatGround);
        if (_chatFreshStart) ImGui.TextDisabled(Loc.T("起点：默认站姿"));

        var logHeight = Math.Max(70, ImGui.GetContentRegionAvail().Y - 160 * ImGuiHelpers.GlobalScale);
        using (var log = ImRaii.Child("##poseChatLog", new Vector2(0, logHeight), true))
        {
            if (log)
            {
                if (_chatHistory.Length == 0)
                    ImGui.TextWrapped(Loc.T("例如：左手举过头顶。左右以角色自身为准。"));
                foreach (var message in _chatHistory)
                {
                    ImGui.TextColored(message.Role == "user" ? Green : Orange,
                                      message.Role == "user" ? Loc.T("你") : Loc.T("动作助手"));
                    ImGui.TextWrapped(message.Content);
                    ImGui.Spacing();
                }
                if (_chatTask is not null)
                {
                    ImGui.TextColored(Green, Loc.T("你"));
                    ImGui.TextWrapped(_chatSentPrompt);
                    ImGui.TextColored(Yellow, Loc.T("正在生成动作预览…"));
                }
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1);
            }
        }
        if (_chatError is not null)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Red);
            ImGui.TextWrapped(_chatError);
        }
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.Disabled(_chatTask is not null))
            ImGui.InputTextMultiline("##poseChatPrompt", ref _chatPrompt, 4000, new Vector2(-1, 60 * ImGuiHelpers.GlobalScale));
        if (_chatTask is not null)
        {
            if (ImGui.Button(Loc.T("取消生成")))
            {
                CancelChat();
                SetStatus(Loc.T("已取消动作生成，原姿势已保留"));
            }
        }
        else
        {
            using (ImRaii.Disabled(!BackendOnline || ImageWorkBusy || _rtBusy || string.IsNullOrWhiteSpace(_chatPrompt)))
                if (ImGui.Button(Loc.T("生成预览"))) SendChat();
        }
        if (_chatPoseActive)
        {
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("查看 3D"))) _selectTab = 1;
        }
    }

    // ---------------- 设置页

    private void DrawSettingsTab()
    {
        var changed = false;
        ImGui.SetNextItemWidth(200);
        var langLabel = Loc.Languages.FirstOrDefault(l => l.Key == _config.Language).Label ?? _config.Language;
        if (ImGui.BeginCombo(Loc.T("语言 / Language"), _config.Language == "auto" ? Loc.T(langLabel) : langLabel))
        {
            foreach (var (key, label) in Loc.Languages)
                if (ImGui.Selectable(key == "auto" ? Loc.T(label) : label, _config.Language == key))
                {
                    _config.Language = key;
                    Loc.Apply(key, _clientState, _pi);
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("自动：游戏客户端是中文时用中文，否则看 Dalamud 界面语言，都不是中文则用英文"));

        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("识别后端（backend）"));
        if (ImGui.Button(Loc.T("选择一键包目录…")))
            _fileDialog.OpenFolderDialog(Loc.T("选择一键包解压后的目录"), (ok, path) =>
            {
                if (!ok) return;
                if (!BackendProcess.TryUsePackage(_config, path))
                {
                    SetError(Loc.F("不是 Posegraph 一键包目录（缺少 python\\python.exe 或 backend）：{0}", path));
                    return;
                }
                _pi.SavePluginConfig(_config);
                SetStatus(Loc.T("已设置一键包路径"), Green);
            }, null, true);
        ItemTip(Loc.T("一键包解压后的目录（含 python 与 backend 两个文件夹），会自动填好下面两项"));
        var python = _config.BackendPython;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - 60);
        if (ImGui.InputText("python##py", ref python, 512)) { _config.BackendPython = python.Trim('"'); changed = true; }
        ItemTip(Loc.T(@"后端的 python.exe：一键包里是 <解压目录>\python\python.exe，源码安装是 backend\.venv\Scripts\python.exe"));
        ImGui.SameLine();
        if (IconButton("##browsePy", FontAwesomeIcon.FolderOpen, Loc.T("浏览")))
            _fileDialog.OpenFileDialog(Loc.T("选择 python.exe"), "python{.exe}", (ok, paths) =>
            {
                if (!ok || paths.Count == 0) return;
                _config.BackendPython = paths[0];
                // 常见布局：backend\.venv\Scripts\python.exe
                var guess = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(paths[0])!, "..", ".."));
                if (string.IsNullOrEmpty(_config.BackendDir) && File.Exists(Path.Combine(guess, "src", "posegraph", "app.py")))
                    _config.BackendDir = guess;
                _pi.SavePluginConfig(_config);
            }, 1, null, true);

        var dir = _config.BackendDir;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - 60);
        if (ImGui.InputText(Loc.T("目录##dir"), ref dir, 512)) { _config.BackendDir = dir.Trim('"'); changed = true; }
        ItemTip(Loc.T("backend 目录（含 src\\posegraph）"));
        ImGui.SameLine();
        if (IconButton("##browseDir", FontAwesomeIcon.FolderOpen, Loc.T("浏览")))
            _fileDialog.OpenFolderDialog(Loc.T("选择 backend 目录"), (ok, path) =>
            {
                if (!ok) return;
                _config.BackendDir = path;
                _pi.SavePluginConfig(_config);
            }, null, true);

        var problem = BackendProcess.Validate(_config);
        if (problem is null) ImGui.TextColored(Green, Loc.T("路径有效"));
        else ImGui.TextColored(Yellow, problem);

        changed |= DrawUpdateSection();

        var port = _config.BackendPort;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt(Loc.T("后端端口"), ref port, 0)) { _config.BackendPort = Math.Clamp(port, 1024, 65535); changed = true; }

        changed |= Check(Loc.T("插件加载时自动启动后端"), _config.AutoStartBackend, v => _config.AutoStartBackend = v);
        var modelChanged = DrawDeviceSettings();
        using (ImRaii.Disabled(_config.ReleaseAfter == 0))
            changed |= Check(Loc.T("启动后预加载模型"), _config.PreloadModel, v => _config.PreloadModel = v,
                             Loc.T("首次识别不用等加载；“识别后立即释放”时无效"));
        if (modelChanged && BackendOnline)
            _ = Run(async () => { await _client.ConfigureAsync(_config); SetStatus(Loc.T("已更新识别设备设置（下次加载生效）")); });
        changed |= modelChanged;
        changed |= DrawRecognitionConcurrencySettings();
        changed |= Check(Loc.T("退出时关闭插件启动的后端"), _config.StopBackendOnExit, v => _config.StopBackendOnExit = v);

        changed |= DrawChatSettings();

        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("操作"));
        changed |= Check(Loc.T("松开滑杆时自动应用到角色"), _config.PushOnRelease, v => _config.PushOnRelease = v);
        changed |= Check(Loc.T("识别完成后立即应用到角色"), _config.PushAfterRecognize, v => _config.PushAfterRecognize = v);
        changed |= Check(Loc.T("进入 GPose 且 Brio 就绪时自动打开界面"), _config.AutoOpenInGpose, v => _config.AutoOpenInGpose = v,
                         Loc.T("自动打开的界面在退出 GPose 时自动关闭；用指令或插件列表打开的不会关闭"));
        changed |= Check(Loc.T("游戏界面隐藏时仍显示"), _config.ShowWhenUiHidden, v =>
        {
            _config.ShowWhenUiHidden = v;
            Plugin.ApplyUiHide(_pi, _config);
        }, Loc.T("关闭时与游戏界面一起隐藏（如 GPose 里隐藏界面截图）。界面不显示时可以打开试试"));

        ImGui.Separator();
        ImGui.TextDisabled(Loc.F("Posegraph HTTP（127.0.0.1:{0}，复刻镜头需要）：{1}", _config.Port, (_server.IsRunning ? Loc.T("运行中") : Loc.T("已停止"))));
        ImGui.SameLine();
        if (ImGui.SmallButton(_server.IsRunning ? Loc.T("停止") : Loc.T("启动")))
        {
            if (_server.IsRunning) _server.Stop();
            else
            {
                try { _server.Start(_config.Port); }
                catch (Exception ex) { SetError(Loc.F("端口 {0} 启动失败：{1}", _config.Port, ex.Message)); }
            }
        }

        if (changed) _pi.SavePluginConfig(_config);

        if (ImGui.CollapsingHeader(Loc.T("后端日志")))
        {
            using var child = ImRaii.Child("##log", new Vector2(0, 160), true);
            foreach (var line in _process.Log)
                ImGui.TextUnformatted(line);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY()) ImGui.SetScrollHereY(1);
        }
    }

    private void InvalidateModelCatalog()
    {
        _modelsRevision++;
        _modelsCts?.Cancel();
        _modelsError = null;
        _chatModels = [];
        _config.ChatModels = [];
        _config.ChatModelsEndpoint = "";
    }

    private void FetchModelCatalog()
    {
        if (_modelsTask is not null || _chatTask is not null || string.IsNullOrWhiteSpace(_config.ChatApiEndpoint)) return;
        _modelsError = null;
        _modelsRequestEndpoint = _config.ChatApiEndpoint.Trim();
        _modelsRequestRevision = ++_modelsRevision;
        var endpoint = _modelsRequestEndpoint;
        var key = _chatApiKey;
        _modelsCts = new CancellationTokenSource();
        var ct = _modelsCts.Token;
        _modelsTask = Task.Run(() => _modelCatalog.FetchAsync(endpoint, key, ct), ct);
    }

    private void PollModelCatalog()
    {
        if (_modelsTask is not { IsCompleted: true } task) return;
        try
        {
            var models = task.GetAwaiter().GetResult();
            if (_modelsRequestRevision != _modelsRevision || _modelsRequestEndpoint != _config.ChatApiEndpoint.Trim()) return;
            _chatModels = models;
            _config.ChatModels = models;
            _config.ChatModelsEndpoint = _modelsRequestEndpoint;
            _pi.SavePluginConfig(_config);
            if (models.Length == 0) _modelsError = Loc.T("服务未返回模型");
        }
        catch (OperationCanceledException)
        {
            if (_modelsRequestRevision == _modelsRevision) _modelsError = Loc.T("拉取模型超时，请重试");
        }
        catch (Exception ex)
        {
            if (_modelsRequestRevision == _modelsRevision) _modelsError = ex.Message;
        }
        finally
        {
            _modelsTask = null;
            _modelsCts?.Dispose();
            _modelsCts = null;
        }
    }

    private bool DrawChatSettings()
    {
        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("提示词动作"));
        var changed = false;
        using (ImRaii.Disabled(_chatTask is not null))
        {
            using (ImRaii.Disabled(_modelsTask is not null))
            {
                var endpoint = _config.ChatApiEndpoint;
                ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 100));
                if (ImGui.InputText(Loc.T("API 端点##chatEndpoint"), ref endpoint, 2048))
                {
                    _config.ChatApiEndpoint = endpoint.Trim();
                    InvalidateModelCatalog();
                    changed = true;
                }
                ItemTip(Loc.T("兼容 OpenAI 的 API 基址或完整 /chat/completions 地址。"));
                ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 100));
                if (ImGui.InputText(Loc.T("API 密钥##chatKey"), ref _chatApiKey, 4096, ImGuiInputTextFlags.Password))
                {
                    InvalidateModelCatalog();
                    changed = true;
                    try
                    {
                        _config.ChatApiKeyProtected = ApiKeyProtection.Protect(_chatApiKey);
                        _chatError = null;
                    }
                    catch { _chatError = Loc.T("无法加密 API 密钥，本次密钥未保存"); }
                }
                ItemTip(Loc.T("在本机加密保存；无需认证可留空。"));
            }

            using (ImRaii.Disabled(_modelsTask is not null || string.IsNullOrWhiteSpace(_config.ChatApiEndpoint)))
                if (ImGui.Button(Loc.T(_modelsTask is null ? "拉取模型" : "拉取中…"))) FetchModelCatalog();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 100));
            var modelLabel = !string.IsNullOrWhiteSpace(_config.ChatModel) ? _config.ChatModel
                : Loc.T(_chatModels.Length == 0 ? "先拉取模型" : "选择模型");
            using (ImRaii.Disabled(_modelsTask is not null || _chatModels.Length == 0))
            {
                if (ImGui.BeginCombo(Loc.T("模型名##chatModel"), modelLabel))
                {
                    foreach (var model in _chatModels)
                        if (ImGui.Selectable(model, _config.ChatModel == model))
                        {
                            _config.ChatModel = model;
                            _chatError = null;
                            changed = true;
                        }
                    ImGui.EndCombo();
                }
            }
        }
        if (_modelsError is not null)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Red);
            ImGui.TextWrapped(_modelsError);
        }
        if (_chatError is not null)
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Red);
            ImGui.TextWrapped(_chatError);
        }
        return changed;
    }

    /// <summary>设置页的后端更新区域。返回配置是否有改动。</summary>
    private bool DrawUpdateSection()
    {
        var changed = false;
        ImGui.Text(Loc.F("后端版本 {0}，插件 {1}", _installedBackend?.Version ?? Loc.T("未知"), BackendUpdater.PluginTag));
        ImGui.SameLine();
        if (_packageRuntime is null)
            ImGui.TextDisabled(Loc.T("（源码安装：请用 git 更新到同版本）"));
        else if (_updater.Busy)
            ImGui.TextColored(Yellow, _updater.Status ?? Loc.T("更新中…"));
        else if (_backendOutdated)
        {
            if (IconButton("##updateBackend", FontAwesomeIcon.CloudDownloadAlt,
                           Loc.T("从 GitHub 下载同版本的后端源码（不到 1MB），只替换 backend\\src，Python、依赖和模型不动。会先停止后端，更新后自动启动")))
                UpdateBackend();
            ImGui.SameLine();
            ImGui.TextColored(Yellow, Loc.F("可更新到 {0}", BackendUpdater.PluginTag));
        }
        else
            ImGui.TextColored(Green, Loc.T("已是最新"));

        if (_packageRuntime is not null)
        {
            var proxy = _config.UpdateProxy;
            ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 140));
            if (ImGui.InputText(Loc.T("下载加速前缀##proxy"), ref proxy, 256))
            {
                _config.UpdateProxy = proxy.Trim();
                changed = true;
            }
            ItemTip(Loc.T("连不上 GitHub 时可以填加速前缀（如 https://gh-proxy.org/），会加在下载地址前面；留空为直连"));
        }
        return changed;
    }

    private static readonly (string Key, string Label, string Tip)[] Devices =
    [
        ("auto", "自动", "可用显存足够（约 3GB）时用 GPU，否则用 CPU"),
        ("cuda", "GPU", "总是用显卡（约 1.5 秒/张）。显存紧张时可能拖慢甚至挤崩游戏"),
        ("cpu", "CPU", "不占显存（约 10 秒/张，取决于 CPU），内存约需 4GB"),
    ];

    private static readonly (int Seconds, string Label)[] ReleasePolicies =
        [(0, "识别后立即释放"), (60, "空闲 1 分钟后释放"), (300, "空闲 5 分钟后释放"), (-1, "常驻（不释放）")];

    /// <summary>识别设备与释放策略。返回是否有改动。</summary>
    private bool DrawDeviceSettings()
    {
        var changed = false;
        ImGui.Text(Loc.T("识别设备"));
        foreach (var (key, label, tip) in Devices)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(Loc.T(label), _config.BackendDevice == key))
            {
                _config.BackendDevice = key;
                changed = true;
            }
            ItemTip(Loc.T(tip));
        }
        ImGui.SetNextItemWidth(180);
        var current = ReleasePolicies.FirstOrDefault(p => p.Seconds == _config.ReleaseAfter).Label is { } known
            ? Loc.T(known) : Loc.F("空闲 {0} 秒后释放", _config.ReleaseAfter);
        if (ImGui.BeginCombo(Loc.T("模型释放"), current))
        {
            foreach (var (seconds, label) in ReleasePolicies)
                if (ImGui.Selectable(Loc.T(label), _config.ReleaseAfter == seconds))
                {
                    _config.ReleaseAfter = seconds;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("释放后显存/内存归还给游戏，下次识别重新加载（约 5~10 秒）。\nGPU 模式用过一次后，CUDA 运行时会常驻少量显存，直到后端退出"));
        using (ImRaii.Disabled(_config.BackendDevice == "cpu"))
            changed |= Check(Loc.T("GPU 模式下 MHR 放 CPU"), _config.MhrOnCpu, v => _config.MhrOnCpu = v, Loc.T("显存约省 750MB，结果一致，每张约慢 0.5 秒"));
        return changed;
    }

    private static bool Check(string label, bool value, Action<bool> set, string? tip = null)
    {
        var v = value;
        var changed = ImGui.Checkbox(label, ref v);
        if (tip is not null) ItemTip(tip);
        if (changed) set(v);
        return changed;
    }
}
