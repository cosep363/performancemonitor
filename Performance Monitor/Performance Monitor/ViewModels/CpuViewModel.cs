using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class CpuViewModel : ViewModelBase
{
    private PerformanceCounter? _cpuCounter;
    private ulong _prevCpuIdle;
    private ulong _prevCpuTotal;

    private string _cpuName = "Detecting CPU...";
    public string CpuName
    {
        get => _cpuName;
        set => SetProperty(ref _cpuName, value);
    }

    private string _utilization = "0%";
    public string Utilization
    {
        get => _utilization;
        set => SetProperty(ref _utilization, value);
    }

    private double _utilizationValue = 0;
    public double UtilizationValue
    {
        get => _utilizationValue;
        set => SetProperty(ref _utilizationValue, value);
    }

    private string _speed = "0.00 GHz";
    public string Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, value);
    }

    private string _processes = "0";
    public string Processes
    {
        get => _processes;
        set => SetProperty(ref _processes, value);
    }

    private string _threads = "0";
    public string Threads
    {
        get => _threads;
        set => SetProperty(ref _threads, value);
    }

    private string _handles = "0";
    public string Handles
    {
        get => _handles;
        set => SetProperty(ref _handles, value);
    }

    private string _uptime = "0:00:00:00";
    public string Uptime
    {
        get => _uptime;
        set => SetProperty(ref _uptime, value);
    }

    // Dynamic Hardware Specs
    private string _baseSpeed = "N/A";
    public string BaseSpeed
    {
        get => _baseSpeed;
        set => SetProperty(ref _baseSpeed, value);
    }

    private string _sockets = "1";
    public string Sockets
    {
        get => _sockets;
        set => SetProperty(ref _sockets, value);
    }

    private string _cores = Environment.ProcessorCount.ToString();
    public string Cores
    {
        get => _cores;
        set => SetProperty(ref _cores, value);
    }

    private string _logicalProcessors = Environment.ProcessorCount.ToString();
    public string LogicalProcessors
    {
        get => _logicalProcessors;
        set => SetProperty(ref _logicalProcessors, value);
    }

    private string _virtualization = "Unknown";
    public string Virtualization
    {
        get => _virtualization;
        set => SetProperty(ref _virtualization, value);
    }

    private string _l1Cache = "N/A";
    public string L1Cache
    {
        get => _l1Cache;
        set => SetProperty(ref _l1Cache, value);
    }

    private string _l2Cache = "N/A";
    public string L2Cache
    {
        get => _l2Cache;
        set => SetProperty(ref _l2Cache, value);
    }

    private string _l3Cache = "N/A";
    public string L3Cache
    {
        get => _l3Cache;
        set => SetProperty(ref _l3Cache, value);
    }

    public CpuViewModel()
    {
        InitCounters();
        _ = LoadCpuSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private void InitCounters()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue();
            }
            catch
            {
                _cpuCounter = null;
            }
        }
    }

    private async Task LoadCpuSpecsAsync()
    {
        await Task.Run(() =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LoadWindowsSpecs();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LoadLinuxSpecs();
            }
        });
    }

    private void LoadWindowsSpecs()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                CpuName = obj["Name"]?.ToString()?.Trim() ?? CpuName;

                if (uint.TryParse(obj["MaxClockSpeed"]?.ToString(), out uint maxClock))
                {
                    BaseSpeed = $"{Math.Round(maxClock / 1000.0, 2):F2} GHz";
                    Speed = BaseSpeed;
                }

                if (obj["NumberOfCores"] != null)
                    Cores = obj["NumberOfCores"].ToString()!;

                if (obj["NumberOfLogicalProcessors"] != null)
                    LogicalProcessors = obj["NumberOfLogicalProcessors"].ToString()!;

                if (obj["L2CacheSize"] != null && uint.TryParse(obj["L2CacheSize"].ToString(), out uint l2))
                    L2Cache = l2 >= 1024 ? $"{l2 / 1024.0:F1} MB" : $"{l2} KB";

                if (obj["L3CacheSize"] != null && uint.TryParse(obj["L3CacheSize"].ToString(), out uint l3))
                    L3Cache = l3 >= 1024 ? $"{l3 / 1024.0:F1} MB" : $"{l3} KB";

                if (obj["VirtualizationFirmwareEnabled"] != null)
                    Virtualization = (bool)obj["VirtualizationFirmwareEnabled"] ? "Enabled" : "Disabled";

                break;
            }

            // Detect L1 Cache via Win32_CacheMemory
            using var cacheSearcher = new ManagementObjectSearcher("SELECT Level, MaxCacheSize FROM Win32_CacheMemory");
            uint totalL1KB = 0;

            foreach (var cache in cacheSearcher.Get())
            {
                // Level 3 enum in Win32_CacheMemory corresponds to L1 Cache
                if (ushort.TryParse(cache["Level"]?.ToString(), out ushort level) && level == 3)
                {
                    if (uint.TryParse(cache["MaxCacheSize"]?.ToString(), out uint size))
                    {
                        totalL1KB += size;
                    }
                }
            }

            if (totalL1KB > 0)
            {
                L1Cache = totalL1KB >= 1024 ? $"{totalL1KB / 1024.0:F1} MB" : $"{totalL1KB} KB";
            }

            using var socketSearcher = new ManagementObjectSearcher("SELECT SocketDesignation FROM Win32_Processor");
            Sockets = socketSearcher.Get().Count.ToString();
        }
        catch
        {
            CpuName = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Windows Processor";
        }
    }

    private void LoadLinuxSpecs()
    {
        try
        {
            string lscpu = ExecuteCommand("lscpu", "");
            var lscpuMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lscpu.Split('\n'))
            {
                int idx = line.IndexOf(':');
                if (idx > 0) lscpuMap[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }

            var physicalIds = new System.Collections.Generic.HashSet<string>();
            int logical = 0;
            int coresPerSocket = 0;
            bool hasVirtFlag = false;
            string? modelName = null;

            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    int idx = line.IndexOf(':');
                    if (idx < 0) continue;
                    string key = line.Substring(0, idx).Trim();
                    string val = line.Substring(idx + 1).Trim();

                    if (key.Equals("processor", StringComparison.OrdinalIgnoreCase)) logical++;
                    else if (key.Equals("model name", StringComparison.OrdinalIgnoreCase) && modelName == null) modelName = val;
                    else if (key.Equals("physical id", StringComparison.OrdinalIgnoreCase)) physicalIds.Add(val);
                    else if (key.Equals("cpu cores", StringComparison.OrdinalIgnoreCase) && coresPerSocket == 0)
                        int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out coresPerSocket);
                    else if (key.Equals("flags", StringComparison.OrdinalIgnoreCase) && !hasVirtFlag)
                        hasVirtFlag = (" " + val + " ").Contains(" vmx ") || (" " + val + " ").Contains(" svm ");
                }
            }

            // ARM and some other architectures have no "model name" in /proc/cpuinfo.
            if (string.IsNullOrWhiteSpace(modelName) && lscpuMap.TryGetValue("Model name", out var lm)) modelName = lm;
            if (!string.IsNullOrWhiteSpace(modelName)) CpuName = modelName!;
            else CpuName = "Linux Processor";

            if (logical == 0) logical = Environment.ProcessorCount;
            LogicalProcessors = logical.ToString();

            int sockets = physicalIds.Count > 0 ? physicalIds.Count : 1;
            if (lscpuMap.TryGetValue("Socket(s)", out var sockText) &&
                int.TryParse(sockText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lscpuSockets) && lscpuSockets > 0)
                sockets = lscpuSockets;
            Sockets = sockets.ToString();

            if (coresPerSocket == 0 && lscpuMap.TryGetValue("Core(s) per socket", out var cpsText))
                int.TryParse(cpsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out coresPerSocket);
            Cores = coresPerSocket > 0 ? (coresPerSocket * sockets).ToString() : logical.ToString();

            // Max (base) frequency: sysfs is in kHz; lscpu reports MHz with a locale-dependent decimal.
            double? maxMhz = null;
            string maxFreqPath = "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq";
            if (File.Exists(maxFreqPath) &&
                double.TryParse(File.ReadAllText(maxFreqPath).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double khz))
                maxMhz = khz / 1000.0;
            else if (lscpuMap.TryGetValue("CPU max MHz", out var maxText) &&
                     double.TryParse(maxText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double lmhz))
                maxMhz = lmhz;

            double? curMhz = ReadLinuxCurrentMhz();
            double? shown = maxMhz ?? curMhz;
            if (shown.HasValue) BaseSpeed = $"{shown.Value / 1000.0:F2} GHz";
            if (curMhz.HasValue) Speed = $"{curMhz.Value / 1000.0:F2} GHz";
            else if (shown.HasValue) Speed = BaseSpeed;

            // Virtualization: CPU flag is the most reliable signal; fall back to lscpu.
            if (hasVirtFlag || lscpuMap.ContainsKey("Virtualization"))
                Virtualization = "Enabled";
            else
                Virtualization = "Disabled";

            // lscpu reports cache totals, e.g. "192 KiB (4 instances)".
            string l1d = lscpuMap.TryGetValue("L1d cache", out var a) ? a : "";
            string l1i = lscpuMap.TryGetValue("L1i cache", out var b) ? b : "";
            if (l1d.Length > 0 && l1i.Length > 0) L1Cache = $"{l1d} + {l1i}";
            else if (l1d.Length > 0 || l1i.Length > 0) L1Cache = l1d.Length > 0 ? l1d : l1i;
            if (lscpuMap.TryGetValue("L2 cache", out var l2)) L2Cache = l2;
            if (lscpuMap.TryGetValue("L3 cache", out var l3)) L3Cache = l3;
        }
        catch
        {
            CpuName = "Linux Processor";
        }
    }

    // Average current frequency across cores in MHz (sysfs kHz), falling back to /proc/cpuinfo.
    private static double? ReadLinuxCurrentMhz()
    {
        try
        {
            const string root = "/sys/devices/system/cpu";
            double sum = 0;
            int n = 0;
            if (Directory.Exists(root))
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "cpu*"))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Length < 4 || !char.IsDigit(name[3])) continue;
                    string f = Path.Combine(dir, "cpufreq", "scaling_cur_freq");
                    if (File.Exists(f) &&
                        double.TryParse(File.ReadAllText(f).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double khz))
                    {
                        sum += khz / 1000.0;
                        n++;
                    }
                }
            }
            if (n > 0) return sum / n;

            if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (!line.StartsWith("cpu MHz", StringComparison.OrdinalIgnoreCase)) continue;
                    int idx = line.IndexOf(':');
                    if (idx > 0 && double.TryParse(line.Substring(idx + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz))
                    {
                        sum += mhz;
                        n++;
                    }
                }
            }
            if (n > 0) return sum / n;
        }
        catch { }
        return null;
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        // Resume ticks on a threadpool thread instead of the UI thread, so the
        // (potentially expensive) work below never runs on - and therefore
        // never blocks - the UI thread. This is what was causing the app to
        // stutter whenever a tick fired while the window was being dragged.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;
            try
            {
                // 1. Live CPU Utilization % (cheap - safe to sample every tick)
                float cpuUsage = 0;
                if (_cpuCounter != null)
                {
                    cpuUsage = _cpuCounter.NextValue();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    cpuUsage = GetLinuxCpuUsage();
                }

                int cpuPercent = (int)Math.Round(cpuUsage);

                // 2. System Uptime (cheap)
                TimeSpan uptimeSpan = TimeSpan.FromMilliseconds(Environment.TickCount64);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // /proc/uptime includes time spent suspended; TickCount64 does not.
                    try
                    {
                        var up = File.ReadAllText("/proc/uptime").Split(' ')[0];
                        if (double.TryParse(up, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
                            uptimeSpan = TimeSpan.FromSeconds(secs);
                    }
                    catch { }
                }
                string uptimeText = $"{uptimeSpan.Days}:{uptimeSpan.Hours:D2}:{uptimeSpan.Minutes:D2}:{uptimeSpan.Seconds:D2}";

                // 3. Processes, Threads & Handles - this enumerates every process on the
                // system and, on Windows, opens each one to read its handle count. That's
                // genuinely expensive, so it only needs to run every few seconds rather
                // than every tick; the counts don't meaningfully change second-to-second.
                string? processesText = null;
                string? threadsText = null;
                string? handlesText = null;

                if (tick % 3 == 1)
                {
                    Process[] processList = Process.GetProcesses();
                    try
                    {
                        processesText = processList.Length.ToString();

                        int threadCount = 0;
                        long handleCount = 0;
                        foreach (var proc in processList)
                        {
                            try
                            {
                                threadCount += proc.Threads.Count;
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                    handleCount += proc.HandleCount;
                            }
                            catch { }
                        }

                        threadsText = threadCount.ToString();
                        handlesText = handleCount > 0 ? handleCount.ToString() : "N/A";

                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            // System-wide allocated file handles: first field of /proc/sys/fs/file-nr.
                            try
                            {
                                var fields = File.ReadAllText("/proc/sys/fs/file-nr").Split(new[] { '\t', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                if (fields.Length > 0 && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long fh))
                                    handlesText = fh.ToString();
                            }
                            catch { }
                        }
                    }
                    finally
                    {
                        // Process.GetProcesses() hands back live handles to every process
                        // on the system - previously these were never disposed, leaking a
                        // handle (and a small amount of memory) every single second.
                        foreach (var proc in processList)
                            proc.Dispose();
                    }
                }

                string? speedText = null;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && tick % 2 == 1)
                {
                    double? mhz = ReadLinuxCurrentMhz();
                    if (mhz.HasValue) speedText = $"{mhz.Value / 1000.0:F2} GHz";
                }

                // Only the final, cheap property assignments touch the UI thread.
                Dispatcher.UIThread.Post(() =>
                {
                    UtilizationValue = cpuPercent;
                    Utilization = $"{cpuPercent}%";
                    Uptime = uptimeText;
                    if (speedText != null) Speed = speedText;

                    if (processesText != null) Processes = processesText;
                    if (threadsText != null) Threads = threadsText;
                    if (handlesText != null) Handles = handlesText;
                });
            }
            catch { }
        }
    }

    // /proc/stat counters are cumulative since boot, so usage must be computed from the
    // change between two samples rather than from a single reading.
    private float GetLinuxCpuUsage()
    {
        try
        {
            if (File.Exists("/proc/stat"))
            {
                string firstLine = File.ReadLines("/proc/stat").First();
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // cpu user nice system idle iowait irq softirq steal [guest guest_nice]
                if (parts.Length >= 5)
                {
                    ulong total = 0;
                    // guest/guest_nice are already included in user/nice, so stop at steal (index 8).
                    for (int i = 1; i < parts.Length && i <= 8; i++)
                    {
                        if (ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong val)) total += val;
                    }

                    ulong idle = ulong.Parse(parts[4], CultureInfo.InvariantCulture);
                    if (parts.Length > 5 && ulong.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong iowait))
                        idle += iowait;

                    ulong dTotal = total - _prevCpuTotal;
                    ulong dIdle = idle - _prevCpuIdle;
                    bool first = _prevCpuTotal == 0;
                    _prevCpuTotal = total;
                    _prevCpuIdle = idle;

                    if (first || total < dTotal || dTotal == 0) return 0;
                    return (float)Math.Clamp((1.0 - (double)dIdle / dTotal) * 100.0, 0, 100);
                }
            }
        }
        catch { }
        return 0;
    }

    private string ExecuteCommand(string command, string arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            using (process)
            {
                process.Start();
                // Read asynchronously so a hung tool (e.g. nvidia-smi with a broken driver) can
                // never block the monitoring loop forever.
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(1500))
                {
                    try { process.Kill(true); } catch { }
                    return string.Empty;
                }
                return output.Wait(1000) ? output.Result : string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}