using System;
using System.Runtime.InteropServices;

namespace ALyricEase.Headless;

/// <summary>
/// 按进程采样 GPU 利用率,口径对齐任务管理器"进程"页的 GPU 列。
///
/// 数据源是 Windows 自带的 PDH 计数器 <c>\GPU Engine(*)\Utilization Percentage</c>,
/// 实例名形如 <c>pid_1234_luid_0x00000000_0x0000d94a_phys_0_eng_0_engtype_3d</c>。
/// 必须走这个源:.NET BCL 里没有任何 GPU 读数(那是驱动与 DWM 的事),而 GPU 也不能用
/// "自己数帧"顶替 —— 合并一帧的代价与场景复杂度成正比,"几帧"说明不了"显卡多忙"。
/// 同理,CPU 归零也不能证明 GPU 归零(帧已经交给了合成线程/驱动)。
///
/// 三个必须守的口径:
/// ① 同一进程会有**多条实例**(每个 (LUID, 引擎, phys) 一条,含 3D / Copy / Compute /
///    VideoDecode),要把属于本进程的全部**加总**;只取一条(比如只取 engtype_3d)
///    在跨适配器或有转码/拷贝流量时会明显偏小。
/// ② 计数器实例**只增不减**:进程退出后其实例仍留在列表里(本机实测 443 条),
///    所以必须**按 pid 过滤**,绝不能"把数组里的值加起来"。
/// ③ 纯百分比计数器是**速率型**,PDH 要两次采集才有值;首次采样恒为 0,
///    构造后要先 prime 一次并把第一个样本丢掉。
/// </summary>
internal sealed class GpuUsageSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x8000_07D2;
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";

    /// <summary>一条 PDH 格式化计数器值(联合体只取 double 成员,布局见 ReadItems 注释)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FmtCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W:实例名指针 + 值。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FmtCounterValueItem
    {
        public IntPtr Name;
        public FmtCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly int _pid;
    private IntPtr _query;
    private IntPtr _counter;
    private bool _disposed;

    /// <summary>一条样本:属于本进程的各引擎利用率之和 / 最大单引擎 / 命中的引擎条数。</summary>
    public readonly record struct Sample(double Sum, double Max, int Engines)
    {
        public static Sample Empty => new(0, 0, 0);
    }

    private GpuUsageSampler(int pid)
    {
        _pid = pid;
    }

    /// <summary>
    /// 打开查询并 prime 一次。失败(计数器集缺失、无权限)返回 null —— 调用方应据此
    /// 明确报告"这台机器量不到 GPU",而不是把 0% 当成"很省"。
    /// </summary>
    public static GpuUsageSampler? TryCreate(int pid, out string diagnosis)
    {
        diagnosis = "";
        var sampler = new GpuUsageSampler(pid);
        if (sampler.Open() is not { } error)
        {
            // prime:速率型计数器需要两次采集之间有时间差
            sampler.Prime();
            return sampler;
        }

        sampler.Dispose();
        diagnosis = error;
        return null;
    }

    private string? Open()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return "PdhOpenQueryW 失败";
        }

        if (PdhAddEnglishCounterW(_query, CounterPath, IntPtr.Zero, out _counter) != 0)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return $"PdhAddEnglishCounterW 失败({CounterPath});该机器可能没有 GPU Engine 计数器集";
        }

        return null;
    }

    /// <summary>丢掉的第一帧:纯粹为了让第二次采集有可算的速率。</summary>
    private void Prime() => PdhCollectQueryData(_query);

    /// <summary>采一条。返回 false 表示本次 PDH 调用失败(不抛,避免探针被计数器问题打断)。</summary>
    public bool TrySample(out Sample sample)
    {
        sample = Sample.Empty;
        if (_disposed || _query == IntPtr.Zero) return false;
        if (PdhCollectQueryData(_query) != 0) return false;

        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out _, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return false;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(_counter, PdhFmtDouble, ref size, out var count, buffer);
            if (status != 0) return false;

            var stride = Marshal.SizeOf<FmtCounterValueItem>();
            var sum = 0.0;
            var max = 0.0;
            var engines = 0;
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<FmtCounterValueItem>(
                    (IntPtr)((long)buffer + (long)index * stride));
                if (item.Name == IntPtr.Zero) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (name is null || !TryParsePid(name, out var pid) || pid != _pid) continue;

                var value = item.Value.DoubleValue;
                if (double.IsNaN(value) || value < 0) continue;
                sum += value;
                if (value > max) max = value;
                engines++;
            }

            sample = new Sample(sum, max, engines);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>实例名首段就是 pid:<c>pid_&lt;数字&gt;_luid_...</c>。</summary>
    internal static bool TryParsePid(string instanceName, out int pid)
    {
        pid = 0;
        const string prefix = "pid_";
        if (!instanceName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var end = instanceName.IndexOf('_', prefix.Length);
        if (end < 0) return false;
        return int.TryParse(instanceName.AsSpan(prefix.Length, end - prefix.Length), out pid);
    }

    /// <summary>结构布局自检:PDH_FMT_COUNTERVALUE_ITEM_W 在 x64 上应为 24 字节
    /// (名字指针 8 + [状态 4 + 填充 4 + double 8])。布局对不上时读到的会是垃圾值,
    /// 那种"数字很漂亮"的错误比崩溃更难查,所以在探针启动时就核对一次。</summary>
    public static int ItemStride => Marshal.SizeOf<FmtCounterValueItem>();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
