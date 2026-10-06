using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Hardware.Info;
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

public partial class DiskViewModel : ViewModelBase
{
    private readonly HardwareInfo _hardwareInfo = new();
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _idleTimeCounter;
    private PerformanceCounter? _responseTimeCounter;

    private string _diskName = "Disk";
    public string DiskName
    {
        get => _diskName;
        set => SetProperty(ref _diskName, value);
    }

    private string _modelName = "Detecting Disk...";
    public string ModelName
    {
        get => _modelName;
        set => SetProperty(ref _modelName, value);
    }

    // Dynamic Live Stats
    private string _activeTime = "0%";
    public string ActiveTime
    {
        get => _activeTime;
        set => SetProperty(ref _activeTime, value);
    }

    private double _activeTimeValue = 0;
    public double ActiveTimeValue
    {
        get => _activeTimeValue;
        set => SetProperty(ref _activeTimeValue, value);
    }

    private string _averageResponseTime = "0.0 ms";
    public string AverageResponseTime
    {
        get => _averageResponseTime;
        set => SetProperty(ref _averageResponseTime, value);
    }

    private string _readSpeed = "0 KB/s";
    public string ReadSpeed
    {
        get => _readSpeed;
        set => SetProperty(ref _readSpeed, value);
    }

    private string _writeSpeed = "0 KB/s";
    public string WriteSpeed
    {
        get => _writeSpeed;
        set => SetProperty(ref _writeSpeed, value);
    }

    // Hardware Technical Specs
    private string _capacity = "-";
    public string Capacity
    {
        get => _capacity;
        set => SetProperty(ref _capacity, value);
    }

    private string _formatted = "-";
    public string Formatted
    {
        get => _formatted;
        set => SetProperty(ref _formatted, value);
    }

    private string _systemDisk = "No";
    public string SystemDisk
    {
        get => _systemDisk;
        set => SetProperty(ref _systemDisk, value);
    }

    private string _pageFile = "No";
    public string PageFile
    {
        get => _pageFile;
        set => SetProperty(ref _pageFile, value);
    }

    private string _type = "Unknown";
    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public DiskViewModel()
    {
        InitPerformanceCounters();
        _ = LoadDiskSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private void InitPerformanceCounters()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                _idleTimeCounter = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
                _responseTimeCounter = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");

                _diskReadCounter.NextValue();
                _diskWriteCounter.NextValue();
                _idleTimeCounter.NextValue();
                _responseTimeCounter.NextValue();
            }
            catch { }
        }
    }

    private async Task LoadDiskSpecsAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // Drive Info & Capacity
                DriveInfo rootDrive = DriveInfo.GetDrives()
                    .FirstOrDefault(d => d.IsReady && (d.Name.StartsWith("C") || d.Name == "/"))
                    ?? DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady)!;

                if (rootDrive != null)
                {
                    double totalGB = Math.Round(rootDrive.TotalSize / (1024.0 * 1024.0 * 1024.0), 0);
                    Capacity = $"{totalGB} GB";
                    Formatted = $"{totalGB} GB";

                    string osDriveLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                    SystemDisk = rootDrive.Name.StartsWith(osDriveLetter, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";

                    // The drive-letter check above is Windows-specific; on Linux the system disk is the one holding "/".
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        SystemDisk = rootDrive.Name == "/" ? "Yes" : "No";

                        // DriveInfo can pick the wrong "/" entry (several mounts can be named "/"), which
                        // produced "0 GB". Use the physical disk size from sysfs for Capacity, and the
                        // size of the filesystems actually living on that disk for Formatted.
                        string? disk = ResolveLinuxDisk();
                        if (disk != null &&
                            long.TryParse(ReadSysfs($"/sys/block/{disk}/size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sectors512) &&
                            sectors512 > 0)
                        {
                            double diskGB = Math.Round(sectors512 * 512.0 / (1024.0 * 1024.0 * 1024.0), 0);
                            Capacity = $"{diskGB} GB";

                            long formattedBytes = GetLinuxFormattedBytes(disk);
                            Formatted = formattedBytes > 0
                                ? $"{Math.Round(formattedBytes / (1024.0 * 1024.0 * 1024.0), 0)} GB"
                                : Capacity;
                        }
                    }
                }

                _hardwareInfo.RefreshDriveList();
                if (_hardwareInfo.DriveList.Count > 0)
                {
                    ModelName = _hardwareInfo.DriveList[0].Model;
                }
                else
                {
                    ModelName = GetFallbackModelName();
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    LoadWindowsDiskDetails();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    LoadLinuxDiskDetails();
                }
            }
            catch
            {
                ModelName = "Generic Storage Device";
            }
        });
    }

    private void LoadWindowsDiskDetails()
    {
        try
        {
            // Dynamic Disk Index & System Disk Check
            using var driveSearcher = new ManagementObjectSearcher("SELECT Index, DeviceID FROM Win32_DiskDrive");
            foreach (var drive in driveSearcher.Get())
            {
                string index = drive["Index"]?.ToString() ?? "0";
                DiskName = $"Disk {index}";
                break;
            }

            // Media Type Detection (SSD vs HDD)
            using var mediaSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var media in mediaSearcher.Get())
            {
                ushort mediaType = Convert.ToUInt16(media["MediaType"]);
                ushort busType = Convert.ToUInt16(media["BusType"]);

                if (busType == 17) // NVMe Bus Type
                    Type = "NVMe SSD";
                else if (mediaType == 4)
                    Type = "SSD";
                else if (mediaType == 3)
                    Type = "HDD";
                else
                    Type = "SSD/HDD";
                break;
            }

            // Dynamic PageFile Detection
            using var pageFileSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileSetting");
            PageFile = pageFileSearcher.Get().Count > 0 ? "Yes" : "No";
        }
        catch
        {
            Type = "SSD";
        }
    }

    private string? _linuxDisk;

    // Finds the physical block device (e.g. "nvme0n1", "sda", "vda", "mmcblk0") that backs "/",
    // following partitions and device-mapper / LVM / LUKS / RAID layers down to the real disk.
    private string? ResolveLinuxDisk()
    {
        if (_linuxDisk != null) return _linuxDisk;

        try
        {
            string? name = null;

            if (File.Exists("/proc/mounts"))
            {
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var f = line.Split(' ');
                    if (f.Length > 1 && f[1] == "/" && f[0].StartsWith("/dev/"))
                    {
                        string dev = f[0];
                        try
                        {
                            var target = new FileInfo(dev).ResolveLinkTarget(true);
                            name = target != null ? target.Name : Path.GetFileName(dev);
                        }
                        catch { name = Path.GetFileName(dev); }
                        // keep scanning: the last "/" entry is the effective one (e.g. overlay/bind setups)
                    }
                }
            }

            // Walk down device-mapper / md layers to an underlying device.
            for (int i = 0; i < 6 && name != null; i++)
            {
                string slaves = $"/sys/class/block/{name}/slaves";
                if (!Directory.Exists(slaves)) break;
                var first = Directory.EnumerateDirectories(slaves).Select(d => Path.GetFileName(d)).FirstOrDefault();
                if (first == null) break;
                name = first;
            }

            // Partition -> parent disk.
            if (name != null && File.Exists($"/sys/class/block/{name}/partition"))
            {
                string? parent = null;
                try
                {
                    var real = new DirectoryInfo($"/sys/class/block/{name}").ResolveLinkTarget(true);
                    parent = real != null ? Path.GetFileName(Path.GetDirectoryName(real.FullName)) : null;
                }
                catch { }

                if (string.IsNullOrEmpty(parent) || !Directory.Exists($"/sys/block/{parent}"))
                {
                    // Fallback: nvme0n1p2 / mmcblk0p1 -> strip "pN"; sda1 / vda2 -> strip digits.
                    parent = System.Text.RegularExpressions.Regex.Replace(
                        name, name.Length > 0 && char.IsDigit(name[name.Length - 1]) &&
                              (name.StartsWith("nvme") || name.StartsWith("mmcblk") || name.StartsWith("loop"))
                            ? @"p\d+$" : @"\d+$", "");
                }
                name = parent;
            }

            if (name != null && Directory.Exists($"/sys/block/{name}")) return _linuxDisk = name;

            // Fallback: first real (non-virtual) disk.
            if (Directory.Exists("/sys/block"))
            {
                string? pick = Directory.EnumerateDirectories("/sys/block")
                    .Select(d => Path.GetFileName(d))
                    .Where(n => n != null &&
                                !n.StartsWith("loop") && !n.StartsWith("ram") && !n.StartsWith("zram") &&
                                !n.StartsWith("dm-") && !n.StartsWith("sr") && !n.StartsWith("fd") && !n.StartsWith("md"))
                    .OrderBy(n => n)
                    .FirstOrDefault();
                if (pick != null) return _linuxDisk = pick;
            }
        }
        catch { }

        return null;
    }

    private static string? ReadSysfs(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    // Total size of the filesystems mounted from partitions of the given disk. A device mounted several
    // times (e.g. btrfs subvolumes) is counted once.
    private long GetLinuxFormattedBytes(string disk)
    {
        long total = 0;
        try
        {
            var bestPerDevice = new System.Collections.Generic.Dictionary<string, long>();
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var f = line.Split(' ');
                if (f.Length < 2 || !f[0].StartsWith("/dev/")) continue;

                string dev;
                try
                {
                    var target = new FileInfo(f[0]).ResolveLinkTarget(true);
                    dev = target != null ? target.Name : Path.GetFileName(f[0]);
                }
                catch { dev = Path.GetFileName(f[0]); }

                // Only partitions (or the disk itself) belonging to this disk.
                if (dev != disk && !Directory.Exists($"/sys/block/{disk}/{dev}")) continue;

                try
                {
                    string mountPoint = f[1].Replace("\\040", " ");
                    long size = new DriveInfo(mountPoint).TotalSize;
                    if (!bestPerDevice.TryGetValue(dev, out long prev) || size > prev)
                        bestPerDevice[dev] = size;
                }
                catch { }
            }
            total = bestPerDevice.Values.Sum();
        }
        catch { }
        return total;
    }

    private void LoadLinuxDiskDetails()
    {
        try
        {
            string? disk = ResolveLinuxDisk();
            DiskName = disk != null ? $"Disk 0 ({disk})" : "Disk 0";

            if (disk != null)
            {
                string? vendor = ReadSysfs($"/sys/block/{disk}/device/vendor");
                string? model = ReadSysfs($"/sys/block/{disk}/device/model");
                if (!string.IsNullOrWhiteSpace(model))
                    ModelName = string.IsNullOrWhiteSpace(vendor) || vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase)
                        ? model
                        : $"{vendor} {model}";

                // SSD vs HDD via the rotational flag (works for sd*, vd*, nvme*, mmcblk*).
                string? rota = ReadSysfs($"/sys/block/{disk}/queue/rotational");
                if (disk.StartsWith("nvme")) Type = "NVMe SSD";
                else if (disk.StartsWith("vd") || disk.StartsWith("xvd")) Type = "Virtual Disk"; // rotational flag is unreliable in VMs
                else if (disk.StartsWith("mmcblk")) Type = "Flash (eMMC/SD)";
                else if (rota == "0") Type = "SSD";
                else if (rota == "1") Type = "HDD";
            }

            // Swap/PageFile Check
            if (File.Exists("/proc/swaps"))
            {
                string[] swaps = File.ReadAllLines("/proc/swaps");
                PageFile = swaps.Length > 1 ? "Yes" : "No";
            }
        }
        catch { }
    }

    private async Task StartMonitoringAsync()
    {
        long lastReadBytes = 0;
        long lastWriteBytes = 0;
        bool havePrev = false;
        ulong prevIoTicksMs = 0, prevReadMs = 0, prevWriteMs = 0, prevReads = 0, prevWrites = 0;
        var clock = Stopwatch.StartNew();
        long prevClockMs = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // Resume off the UI thread - perf-counter reads and /proc/diskstats file IO
        // have no business blocking window/input handling.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (_diskReadCounter != null && _diskWriteCounter != null && _idleTimeCounter != null && _responseTimeCounter != null)
                    {
                        float readBytesPerSec = _diskReadCounter.NextValue();
                        float writeBytesPerSec = _diskWriteCounter.NextValue();
                        float idlePercent = _idleTimeCounter.NextValue();
                        float avgResponseSeconds = _responseTimeCounter.NextValue();

                        double activePercent = Math.Clamp(Math.Round(100 - idlePercent), 0, 100);
                        string activeTimeText = $"{activePercent}%";
                        string responseText = $"{avgResponseSeconds * 1000.0:F1} ms";
                        string readText = FormatSpeed(readBytesPerSec);
                        string writeText = FormatSpeed(writeBytesPerSec);

                        Dispatcher.UIThread.Post(() =>
                        {
                            ActiveTimeValue = activePercent;
                            ActiveTime = activeTimeText;
                            AverageResponseTime = responseText;
                            ReadSpeed = readText;
                            WriteSpeed = writeText;
                        });
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string? disk = ResolveLinuxDisk();
                    if (disk != null && File.Exists("/proc/diskstats"))
                    {
                        // Match the device name exactly (field 3) - not a substring, so that
                        // partitions like "sda1" or unrelated devices are never picked up.
                        string? targetLine = null;
                        foreach (var l in File.ReadLines("/proc/diskstats"))
                        {
                            var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (f.Length > 2 && f[2] == disk) { targetLine = l; break; }
                        }

                        if (targetLine != null)
                        {
                            var p = targetLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            // 3 reads, 5 sectors read, 6 ms reading, 7 writes, 9 sectors written,
                            // 10 ms writing, 12 ms spent doing I/O
                            if (p.Length >= 13)
                            {
                                ulong reads = ulong.Parse(p[3], CultureInfo.InvariantCulture);
                                ulong readSectors = ulong.Parse(p[5], CultureInfo.InvariantCulture);
                                ulong readMs = ulong.Parse(p[6], CultureInfo.InvariantCulture);
                                ulong writes = ulong.Parse(p[7], CultureInfo.InvariantCulture);
                                ulong writeSectors = ulong.Parse(p[9], CultureInfo.InvariantCulture);
                                ulong writeMs = ulong.Parse(p[10], CultureInfo.InvariantCulture);
                                ulong ioTicksMs = ulong.Parse(p[12], CultureInfo.InvariantCulture);

                                // /proc/diskstats always counts in 512-byte units, regardless of the
                                // device's physical sector size.
                                long currentReadBytes = (long)(readSectors * 512UL);
                                long currentWriteBytes = (long)(writeSectors * 512UL);
                                long nowMs = clock.ElapsedMilliseconds;

                                if (havePrev)
                                {
                                    double elapsedSec = Math.Max((nowMs - prevClockMs) / 1000.0, 0.001);
                                    double readBps = Math.Max(0, currentReadBytes - lastReadBytes) / elapsedSec;
                                    double writeBps = Math.Max(0, currentWriteBytes - lastWriteBytes) / elapsedSec;

                                    double activePercent = Math.Clamp(
                                        Math.Round((ioTicksMs - prevIoTicksMs) / (elapsedSec * 1000.0) * 100.0), 0, 100);

                                    ulong dOps = (reads - prevReads) + (writes - prevWrites);
                                    double avgMs = dOps > 0
                                        ? ((readMs - prevReadMs) + (writeMs - prevWriteMs)) / (double)dOps
                                        : 0;

                                    string readText = FormatSpeed(readBps);
                                    string writeText = FormatSpeed(writeBps);
                                    string activeText = $"{activePercent}%";
                                    string responseText = $"{avgMs:F1} ms";

                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        ReadSpeed = readText;
                                        WriteSpeed = writeText;
                                        ActiveTimeValue = activePercent;
                                        ActiveTime = activeText;
                                        AverageResponseTime = responseText;
                                    });
                                }

                                lastReadBytes = currentReadBytes;
                                lastWriteBytes = currentWriteBytes;
                                prevReads = reads; prevWrites = writes;
                                prevReadMs = readMs; prevWriteMs = writeMs;
                                prevIoTicksMs = ioTicksMs;
                                prevClockMs = nowMs;
                                havePrev = true;
                            }
                        }
                    }
                }
            }
            catch { }
        }
    }

    private string GetFallbackModelName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string? disk = ResolveLinuxDisk();
            if (disk != null)
            {
                string? model = ReadSysfs($"/sys/block/{disk}/device/model");
                if (!string.IsNullOrWhiteSpace(model)) return model;
            }
        }
        return "System Storage Device";
    }

    private string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
        {
            return $"{Math.Round(bytesPerSec / (1024.0 * 1024.0), 1)} MB/s";
        }
        return $"{Math.Round(bytesPerSec / 1024.0, 0)} KB/s";
    }
}