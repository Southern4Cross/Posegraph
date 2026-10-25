using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Posegraph.Backend;

/// <summary>
/// 显存 / 内存检测，判断能否加载识别模型。显存用 nvidia-smi（只支持 N 卡），后台每几秒刷新一次。
/// </summary>
public sealed class SystemProbe
{
    /// <summary>GPU 模式所需可用显存：模型峰值约 2GB（MHR 放 CPU）+ CUDA 上下文与余量。低于它时"自动"档改用 CPU。</summary>
    public const int RequiredVramMB = 3000;

    /// <summary>加载权重时的内存峰值约 4GB。</summary>
    public const int RequiredRamMB = 4096;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private DateTime _last = DateTime.MinValue;
    private Task? _running;

    public string? GpuName { get; private set; }
    public int? VramFreeMB { get; private set; }
    public int? VramTotalMB { get; private set; }
    public string? GpuError { get; private set; }
    public int RamAvailMB { get; private set; }
    public int RamTotalMB { get; private set; }

    /// <summary>界面每帧调用；到时间才在后台刷新。</summary>
    public void Tick()
    {
        if (_running is { IsCompleted: false } || DateTime.UtcNow - _last < Interval) return;
        _last = DateTime.UtcNow;
        _running = Task.Run(Refresh);
    }

    private void Refresh()
    {
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            RamAvailMB = (int)(mem.AvailPhys / 1048576);
            RamTotalMB = (int)(mem.TotalPhys / 1048576);
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            })!;
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            var parts = line?.Split(',').Select(s => s.Trim()).ToArray();
            if (parts is not { Length: 3 }) throw new InvalidOperationException(Loc.T("nvidia-smi 输出无法解析"));
            GpuName = parts[0];
            VramTotalMB = int.Parse(parts[1]);
            VramFreeMB = int.Parse(parts[2]);
            GpuError = null;
        }
        catch (Exception ex)
        {
            GpuError = Loc.F("无法读取显存（需要 NVIDIA 显卡与驱动）：{0}", ex.Message);
            VramFreeMB = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
