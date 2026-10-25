using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Posegraph.Backend;

namespace Posegraph.UI;

public sealed partial class MainWindow
{
    // Only the Draw thread changes the list, sessions and textures. Background tasks
    // return complete results so an old selection cannot replace a newer preview.
    private sealed class ImportedImage(string name, string? path = null, byte[]? bytes = null)
    {
        public string Name { get; } = name;
        public string? Path { get; } = path;
        public byte[]? Bytes { get; } = bytes;
        public SessionInfo? Session { get; set; }
        public string? Error { get; set; }
        public bool Started { get; set; }
        public bool Skipped { get; set; }
    }

    private sealed record LoadedImage(SessionInfo Session, IDalamudTextureWrap Texture);

    private readonly List<ImportedImage> _images = [];
    private readonly Queue<ImportedImage> _imageQueue = new();
    private readonly CancellationTokenSource _imageLifetime = new();
    private CancellationTokenSource _imagePoseCts = new();
    private CancellationToken _imagePoseToken;
    private readonly List<Task> _imageCleanup = [];
    private ImportedImage? _selectedImage, _autoLoadImage;
    private readonly Dictionary<Task<SessionInfo>, ImportedImage> _imageRecognitionTasks = [];
    private Task? _imageBatchConfigTask;
    private int _imageBatchParallelism = 1;
    private Task<LoadedImage>? _imageLoadTask;
    private CancellationTokenSource? _imageLoadCts;
    private Task<string[]>? _imageFilesTask;
    private Task<byte[]?>? _clipboardTask;
    private bool _stopImageBatch;
    private bool ImageImportBusy => _recognizing || _imageFilesTask is not null || _clipboardTask is not null;
    // Background recognition does not block work on an already loaded image.
    private bool ImageWorkBusy => _imageFilesTask is not null || _clipboardTask is not null
        || _imageLoadTask is not null || (_recognizing && _session is null);

    private static bool IsImageInput(string path) => IsImage(path) || Directory.Exists(path);

    private void DrawImageImportMenu()
    {
        if (!ImGui.BeginPopup("##imageImport")) return;
        if (ImGui.MenuItem(Loc.T("选择图片（多选）"))) OpenImageDialog();
        if (ImGui.MenuItem(Loc.T("选择文件夹（包含子文件夹）")))
        {
            var dir = Directory.Exists(_config.LastImageDir) ? _config.LastImageDir : null;
            _fileDialog.OpenFolderDialog(Loc.T("选择图片文件夹"), (ok, path) =>
            {
                if (!ok) return;
                _config.LastImageDir = path;
                _pi.SavePluginConfig(_config);
                ImportImagePaths([path]);
            }, dir, true);
        }
        ImGui.EndPopup();
    }

    private bool CanImportImages()
    {
        if (ImageImportBusy) return false;
        if (BackendOnline) return true;
        SetError(Loc.T("后端离线：先点右上角电源按钮启动"));
        return false;
    }

    private void ImportImagePaths(IEnumerable<string> paths)
    {
        if (!CanImportImages()) return;
        var inputs = paths.ToArray();
        _imageFilesTask = Task.Run(() => ImageFiles.Collect(inputs), _imageLifetime.Token);
        _selectTab = 0;
        SetStatus(Loc.T("正在收集图片…"), Yellow);
    }

    private void ImportClipboardImage()
    {
        if (!CanImportImages()) return;
        _clipboardTask = ClipboardImage.ReadPngAsync(_textures, _textureReadback, _imageLifetime.Token);
        _selectTab = 0;
        SetStatus(Loc.T("正在读取剪切板图像…"), Yellow);
    }

    private void BeginImageBatch(ImportedImage[] images)
    {
        ClearImagePreview();
        ReleaseImageSessions(_images);
        _images.Clear();
        _images.AddRange(images);
        _imageQueue.Clear();
        foreach (var image in images) _imageQueue.Enqueue(image);
        _autoLoadImage = images.Length == 1 ? images[0] : null;
        _selectedImage = _autoLoadImage;
        _stopImageBatch = false;
        _recognizing = true;
        _selectTab = 0;
        ConfigureImageBatch();
    }

    private void RecognizeImageAgain(ImportedImage image)
    {
        if (!CanImportImages()) return;
        ClearImagePreview();
        ReleaseImageSessions([image]);
        image.Session = null;
        image.Error = null;
        image.Skipped = false;
        _imageQueue.Clear();
        _imageQueue.Enqueue(image);
        _autoLoadImage = image;
        _stopImageBatch = false;
        _recognizing = true;
        ConfigureImageBatch();
    }

    private void ConfigureImageBatch()
    {
        // Snapshot the setting: editing Settings while a batch is running only
        // affects the next batch. A single image needs just one model instance.
        _imageBatchParallelism = Math.Min(_imageQueue.Count, Math.Clamp(_config.BatchRecognitionParallelism, 1, 8));
        _imageBatchConfigTask = Task.Run(() => _client.ConfigureRecognitionAsync(_imageBatchParallelism));
    }

    private void StartImageRecognitions()
    {
        if (_stopImageBatch)
            while (_imageQueue.TryDequeue(out var skipped)) skipped.Skipped = true;
        while (_imageRecognitionTasks.Count < _imageBatchParallelism && _imageQueue.TryDequeue(out var image))
        {
            image.Started = true;
            var task = Task.Run(async () =>
            {
                var bytes = image.Bytes ?? await File.ReadAllBytesAsync(image.Path!, _imageLifetime.Token);
                // Finish every in-flight inference even on disposal so its returned
                // session ID can still be deleted. Stop only skips queued images.
                return await _client.CreateSessionAsync(bytes, System.IO.Path.GetFileNameWithoutExtension(image.Name));
            });
            _imageRecognitionTasks.Add(task, image);
        }
        if (_imageRecognitionTasks.Count == 0 && _imageQueue.Count == 0)
        {
            _recognizing = false;
            var succeeded = _images.Count(i => i.Session is not null);
            var failed = _images.Count(i => i.Error is not null);
            if (_selectedImage is null || _autoLoadImage is not null)
                SetStatus(Loc.F("预识别完成：{0} 张成功，{1} 张失败", succeeded, failed), failed > 0 ? Yellow : Green);
            if (_autoLoadImage?.Session is not null) SelectImage(_autoLoadImage);
            else if (_autoLoadImage?.Error is { } error)
            {
                _selectedImage = _autoLoadImage;
                SetError(Loc.F("识别失败：{0}", error));
            }
            _autoLoadImage = null;
            return;
        }
        var completed = _images.Count(i => i.Session is not null || i.Error is not null || i.Skipped);
        if (_selectedImage is null || _autoLoadImage is not null)
            SetStatus(Loc.F("预识别：{0}/{1}，同时识别 {2} 张", completed, _images.Count, _imageRecognitionTasks.Count), Yellow);
    }

    private void PollImages()
    {
        _imageCleanup.RemoveAll(t => t.IsCompleted);
        if (_imageFilesTask is { IsCompleted: true } filesTask)
        {
            _imageFilesTask = null;
            try
            {
                var files = filesTask.GetAwaiter().GetResult();
                if (files.Length == 0) SetError(Loc.T("没有找到支持的图片"));
                else BeginImageBatch(files.Select(p => new ImportedImage(Path.GetFileName(p), p)).ToArray());
            }
            catch (Exception ex) { SetError(Loc.F("导入图片失败：{0}", ex.Message)); }
        }
        if (_clipboardTask is { IsCompleted: true } clipboardTask)
        {
            _clipboardTask = null;
            try
            {
                var bytes = clipboardTask.GetAwaiter().GetResult();
                if (bytes is null) SetError(Loc.T("剪切板中没有可识别的图像，请先复制图片"));
                else BeginImageBatch([new ImportedImage(Loc.T("剪切板图像") + ".png", bytes: bytes)]);
            }
            catch (Exception ex) { SetError(Loc.F("读取剪切板图像失败：{0}", ex.Message)); }
        }
        if (_imageBatchConfigTask is { IsCompleted: true } configTask)
        {
            _imageBatchConfigTask = null;
            try { configTask.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                var message = Loc.F("设置识别并行数失败：{0}", ex.Message);
                while (_imageQueue.TryDequeue(out var image)) image.Error = message;
                _recognizing = false;
                _autoLoadImage = null;
                SetError(message);
            }
        }
        foreach (var (task, image) in _imageRecognitionTasks.Where(kv => kv.Key.IsCompleted).ToArray())
        {
            _imageRecognitionTasks.Remove(task);
            try { image.Session = task.GetAwaiter().GetResult(); }
            catch (Exception ex) { image.Error = ex.Message; }
        }
        if (_recognizing && _imageBatchConfigTask is null) StartImageRecognitions();
        if (_imageLoadTask is { IsCompleted: true } loadTask)
        {
            _imageLoadTask = null;
            _imageLoadCts?.Dispose();
            _imageLoadCts = null;
            try
            {
                var loaded = loadTask.GetAwaiter().GetResult();
                _texture = loaded.Texture;
                _session = loaded.Session;
                _person = 0;
                SetStatus(Loc.F("识别完成（{0} {1:0.0} 秒）", DeviceLabel(loaded.Session.Device), loaded.Session.InferenceMs / 1000f), Green);
                RequestRetarget(_config.PushAfterRecognize);
            }
            catch (Exception ex) { SetError(Loc.F("加载图片失败：{0}", ex.Message)); }
        }
    }

    private void ClearImagePreview()
    {
        ResetChatContext(discardPose: true);
        _chatImageDetached = false;
        _session = null;
        _result = null;
        _selectedImage = null;
        _selected = null;
        _params.Offsets.Clear();
        _imagePoseCts.Cancel();
        _imagePoseCts.Dispose();
        _imagePoseCts = new CancellationTokenSource();
        _imagePoseToken = _imagePoseCts.Token;
        _texture?.Dispose();
        _texture = null;
        _imageLoadCts?.Cancel();
        _imageLoadCts?.Dispose();
        _imageLoadCts = null;
        if (_imageLoadTask is { } oldLoad) _imageCleanup.Add(DiscardImageLoadAsync(oldLoad));
        _imageLoadTask = null;
    }

    private static async Task DiscardImageLoadAsync(Task<LoadedImage> task)
    {
        try { (await task).Texture.Dispose(); }
        catch { /* Observe failures from a selection that was replaced. */ }
    }

    private void SelectImage(ImportedImage image)
    {
        if (image.Session is not { } session) return;
        _autoLoadImage = null;   // a deliberate choice takes priority over retry/single-image auto-loading
        if (_selectedImage == image && (_session is not null || _imageLoadTask is not null)) return;
        ClearImagePreview();
        _selectedImage = image;
        _imageLoadCts = CancellationTokenSource.CreateLinkedTokenSource(_imageLifetime.Token);
        var ct = _imageLoadCts.Token;
        _imageLoadTask = Task.Run(async () =>
        {
            var png = await _client.SessionImageAsync(session.SessionId, ct);
            ct.ThrowIfCancellationRequested();
            var texture = await _textures.CreateFromImageAsync(png);
            if (ct.IsCancellationRequested)
            {
                texture.Dispose();
                ct.ThrowIfCancellationRequested();
            }
            return new LoadedImage(session, texture);
        });
        SetStatus(Loc.F("加载图片：{0}", image.Name), Yellow);
    }

    private void DrawImageList()
    {
        ImGui.TextUnformatted(Loc.F("图片 ({0})", _images.Count));
        if (_recognizing)
        {
            ImGui.TextWrapped(Loc.T("已识别的图片可立即预览和应用"));
            var completed = _images.Count(i => i.Session is not null || i.Error is not null || i.Skipped);
            ImGui.ProgressBar((float)completed / _images.Count, new Vector2(-1, 0), $"{completed}/{_images.Count}");
            using (ImRaii.Disabled(_stopImageBatch))
                if (ImGui.SmallButton(Loc.T("停止预识别"))) _stopImageBatch = true;
        }
        else ImGui.TextWrapped(Loc.T("选择图片后加载预览"));
        ImGui.Separator();
        for (var i = 0; i < _images.Count; i++)
        {
            var image = _images[i];
            using var id = ImRaii.PushId(i);
            using (ImRaii.Disabled(image.Session is null))
                if (ImGui.Selectable(image.Name + "##image", _selectedImage == image)) SelectImage(image);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(image.Path ?? image.Name);
            var state = image.Session is not null ? Loc.T("已识别")
                : image.Error is not null ? Loc.T("识别失败")
                : image.Skipped ? Loc.T("已跳过")
                : image.Started ? Loc.T("识别中…") : Loc.T("等待识别");
            ImGui.TextColored(image.Error is not null ? Red : image.Session is not null ? Green : Grey, state);
            if (image.Error is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(image.Error);
            if (image.Session is null && !_recognizing)
                if (ImGui.SmallButton(Loc.T("重试"))) RecognizeImageAgain(image);
            ImGui.Separator();
        }
    }

    private async Task DeleteImageSessionAsync(string sid)
    {
        try { await _client.DeleteSessionAsync(sid); }
        catch { /* A stopped/older backend has already discarded these sessions. */ }
    }

    private void ReleaseImageSessions(IEnumerable<ImportedImage> images)
    {
        foreach (var image in images)
            if (image.Session is { } session) _imageCleanup.Add(DeleteImageSessionAsync(session.SessionId));
    }

    private void DisposeImages()
    {
        _imageLifetime.Cancel();
        _imagePoseCts.Cancel();
        _imagePoseCts.Dispose();
        _imageLoadCts?.Cancel();
        _imageLoadCts?.Dispose();
        if (_imageLoadTask is { } load) _imageCleanup.Add(DiscardImageLoadAsync(load));
        var recognitions = _imageRecognitionTasks.Keys.ToArray();
        var sessions = _images.Where(i => i.Session is not null).Select(i => i.Session!.SessionId).ToList();
        var cleanup = _imageCleanup.ToArray();
        var sources = new Task?[] { _imageFilesTask, _clipboardTask, _imageBatchConfigTask }.OfType<Task>().ToArray();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var recognition in recognitions)
                {
                    try { sessions.Add((await recognition).SessionId); }
                    catch { }
                }
                foreach (var sid in sessions.Distinct()) await DeleteImageSessionAsync(sid);
                await Task.WhenAll(cleanup);
                foreach (var source in sources)
                {
                    try { await source; }
                    catch { /* Observe folder/clipboard cancellation and release its resources. */ }
                }
            }
            finally
            {
                _imageLifetime.Dispose();
                _client.Dispose();
            }
        });
    }
}
