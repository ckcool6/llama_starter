using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace llamaStarter;

public record ProcessMetrics(
    double CpuPercent,
    double RamUsedMb,
    double SystemRamFreeMb,
    double SystemRamTotalMb,
    double GpuPercent,
    double VramUsedMb,
    double SystemVramFreeMb,
    double SystemVramTotalMb
);

public static class HardwareMonitor
{
    private static double _cachedTotalVramMb = 0;
    private static readonly ConcurrentDictionary<int, ProcessGpuTracker> _trackers = new();

    static HardwareMonitor()
    {
        _cachedTotalVramMb = QueryTotalVramDxgi();
    }

    public static ProcessMetrics Collect(Process? process, ref TimeSpan prevCpuTime, ref DateTime prevSampleTime)
    {
        double cpuPercent = 0;
        double ramUsedMb = 0;

        // 1. 获取目标进程 CPU 与 RAM
        if (process != null && !process.HasExited)
        {
            try
            {
                process.Refresh();
                ramUsedMb = process.WorkingSet64 / (1024.0 * 1024.0);

                var now = DateTime.UtcNow;
                var currCpuTime = process.TotalProcessorTime;
                var wallTimeDelta = (now - prevSampleTime).TotalMilliseconds;
                var cpuTimeDelta = (currCpuTime - prevCpuTime).TotalMilliseconds;

                if (wallTimeDelta > 200 && prevSampleTime != DateTime.MinValue)
                {
                    cpuPercent = Math.Clamp(
                        (cpuTimeDelta / (wallTimeDelta * Environment.ProcessorCount)) * 100.0,
                        0.0, 100.0
                    );
                }

                prevCpuTime = currCpuTime;
                prevSampleTime = now;
            }
            catch { }
        }

        // 2. 获取整机物理内存 RAM
        double sysRamTotalMb = 0;
        double sysRamFreeMb = 0;
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            sysRamTotalMb = memStatus.ullTotalPhys / (1024.0 * 1024.0);
            sysRamFreeMb = memStatus.ullAvailPhys / (1024.0 * 1024.0);
        }

        // 3. 读取当前 PID 的 GPU 与 显存 VRAM (通过持久化 Tracker 连续采样)
        double gpuPercent = 0;
        double vramUsedMb = 0;

        if (process != null && !process.HasExited)
        {
            try
            {
                var tracker = _trackers.GetOrAdd(process.Id, pid => new ProcessGpuTracker(pid));
                tracker.Sample(out gpuPercent, out vramUsedMb);
            }
            catch { }
        }
        else if (process != null && process.HasExited)
        {
            if (_trackers.TryRemove(process.Id, out var tracker))
            {
                tracker.Dispose();
            }
        }

        // 清理已结束进程的残留 Tracker
        foreach (var pid in _trackers.Keys)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    if (_trackers.TryRemove(pid, out var t)) t.Dispose();
                }
            }
            catch
            {
                if (_trackers.TryRemove(pid, out var t)) t.Dispose();
            }
        }

        double sysVramTotalMb = _cachedTotalVramMb;
        double sysVramFreeMb = Math.Max(0, sysVramTotalMb - vramUsedMb);

        return new ProcessMetrics(
            cpuPercent,
            ramUsedMb,
            sysRamFreeMb,
            sysRamTotalMb,
            gpuPercent,
            vramUsedMb,
            sysVramFreeMb,
            sysVramTotalMb
        );
    }

    /// <summary>
    /// 每个进程专用的 GPU/VRAM 连续采样跟踪器
    /// </summary>
    private sealed class ProcessGpuTracker : IDisposable
    {
        private readonly int _pid;
        private nint _query = nint.Zero;
        private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<nint> _engineCounters = new();
        private readonly List<nint> _vramCounters = new();
        private DateTime _lastRefresh = DateTime.MinValue;
        private bool _hasInitialSample = false;

        public ProcessGpuTracker(int pid)
        {
            _pid = pid;
            PdhOpenQuery(nint.Zero, nint.Zero, out _query);
            RefreshCounters();
        }

        private void RefreshCounters()
        {
            if (_query == nint.Zero) return;

            string pidPattern = $"pid_{_pid}_";

            // 1. 监测 GPU 算力引擎（Compute、3D、Copy 统一累加）
            AddCountersForPattern($"\\GPU Engine({pidPattern}*)\\Utilization Percentage", _engineCounters);

            // 2. 监测专用显存
            AddCountersForPattern($"\\GPU Process Memory({pidPattern}*)\\Dedicated Usage", _vramCounters);
            if (_vramCounters.Count == 0)
            {
                AddCountersForPattern($"\\GPU Process Memory({pidPattern}*)\\Local Usage", _vramCounters);
            }

            _lastRefresh = DateTime.UtcNow;
        }

        private void AddCountersForPattern(string wildcardPath, List<nint> targetList)
        {
            uint bufferSize = 0;
            PdhExpandWildCardPath(nint.Zero, wildcardPath, nint.Zero, ref bufferSize, 0);
            if (bufferSize == 0) return;

            nint buffer = Marshal.AllocHGlobal((int)(bufferSize * 2));
            try
            {
                if (PdhExpandWildCardPath(nint.Zero, wildcardPath, buffer, ref bufferSize, 0) == 0)
                {
                    string expanded = Marshal.PtrToStringUni(buffer) ?? string.Empty;
                    var paths = expanded.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var path in paths)
                    {
                        if (!_knownPaths.Contains(path))
                        {
                            if (PdhAddEnglishCounter(_query, path, nint.Zero, out nint counter) == 0)
                            {
                                _knownPaths.Add(path);
                                targetList.Add(counter);
                            }
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Sample(out double gpuPercent, out double vramMb)
        {
            gpuPercent = 0;
            vramMb = 0;

            if (_query == nint.Zero) return;

            // 每隔 3 秒自动探测是否有新建的 GPU 计算上下文
            if (_engineCounters.Count == 0 || (DateTime.UtcNow - _lastRefresh).TotalSeconds >= 3)
            {
                RefreshCounters();
            }

            if (PdhCollectQueryData(_query) != 0) return;

            // 第一次采样只作为基准线，第 2 次开始计算真实的差值百分比
            if (!_hasInitialSample)
            {
                _hasInitialSample = true;
                return;
            }

            // 累加该进程所有 GPU 引擎的利用率（Compute / 3D / Copy）
            double engineSum = 0;
            foreach (var counter in _engineCounters)
            {
                if (PdhGetFormattedCounterValue(counter, 0x00000200 /* PDH_FMT_DOUBLE */, out _, out var val) == 0)
                {
                    engineSum += val.doubleValue;
                }
            }
            gpuPercent = Math.Clamp(engineSum, 0.0, 100.0);

            // 统计显存占用
            double vramBytes = 0;
            foreach (var counter in _vramCounters)
            {
                if (PdhGetFormattedCounterValue(counter, 0x00000200 /* PDH_FMT_DOUBLE */, out _, out var val) == 0)
                {
                    vramBytes += val.doubleValue;
                }
            }
            vramMb = vramBytes / (1024.0 * 1024.0);
        }

        public void Dispose()
        {
            if (_query != nint.Zero)
            {
                PdhCloseQuery(_query);
                _query = nint.Zero;
            }
            _engineCounters.Clear();
            _vramCounters.Clear();
            _knownPaths.Clear();
        }
    }

    /// <summary>
    /// 通过 Windows DXGI 原生获取主显卡显存
    /// </summary>
    private static double QueryTotalVramDxgi()
    {
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out nint pFactory) == 0 && pFactory != nint.Zero)
            {
                var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(pFactory);
                double maxVramMb = 0;
                uint index = 0;
                while (factory.EnumAdapters1(index++, out var adapter) == 0 && adapter != null)
                {
                    if (adapter.GetDesc1(out var desc) == 0)
                    {
                        double vramMb = (double)desc.DedicatedVideoMemory / (1024.0 * 1024.0);
                        if (vramMb > maxVramMb)
                        {
                            maxVramMb = vramMb;
                        }
                    }
                    Marshal.ReleaseComObject(adapter);
                }
                Marshal.ReleaseComObject(factory);
                return maxVramMb;
            }
        }
        catch { }
        return 0;
    }

    #region Win32 & DXGI & PDH 导入

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        [PreserveSig]
        int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1 ppAdapter);
        [PreserveSig]
        int IsCurrent();
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint ppFactory);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public long longValue;
        [FieldOffset(8)] public double doubleValue;
    }

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhOpenQuery(nint szDataSource, nint dwUserData, out nint phQuery);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCloseQuery(nint hQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint hQuery, string szFullCounterPath, nint dwUserData, out nint phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCollectQueryData(nint hQuery);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhGetFormattedCounterValue(nint hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "PdhExpandWildCardPathW")]
    private static extern uint PdhExpandWildCardPath(nint szDataSource, string szWildCardPath, nint mszExpandedPathList, ref uint pcchPathListLength, uint dwFlags);

    #endregion
}