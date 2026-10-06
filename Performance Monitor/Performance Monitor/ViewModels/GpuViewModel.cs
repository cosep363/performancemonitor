using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class GpuViewModel : ViewModelBase
{
    private readonly List<PerformanceCounter> _winGpuCounters = new();

    private string _gpuName = "Detecting GPU...";
    public string GpuName
    {
        get => _gpuName;
        set => SetProperty(ref _gpuName, value);
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

    private string _temperature = "N/A";
    public string Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, value);
    }

    private string _driverVersion = "Unknown";
    public string DriverVersion
    {
        get => _driverVersion;
        set => SetProperty(ref _driverVersion, value);
    }

    private string _driverDate = "Unknown";
    public string DriverDate
    {
        get => _driverDate;
        set => SetProperty(ref _driverDate, value);
    }

    private string _directXVersion = "N/A";
    public string DirectXVersion
    {
        get => _directXVersion;
        set => SetProperty(ref _directXVersion, value);
    }

    private string _physicalLocation = "Unknown";
    public string PhysicalLocation
    {
        get => _physicalLocation;
        set => SetProperty(ref _physicalLocation, value);
    }

    // Fixed: Added missing properties expected by MainWindow.axaml
    private string _memoryUsage = "N/A";
    public string MemoryUsage
    {
        get => _memoryUsage;
        set => SetProperty(ref _memoryUsage, value);
    }

    private string _sharedMemoryUsage = "N/A";
    public string SharedMemoryUsage
    {
        get => _sharedMemoryUsage;
        set => SetProperty(ref _sharedMemoryUsage, value);
    }

    public GpuViewModel()
    {
        InitCounters();
        _ = LoadGpuSpecsAsync();
        _ = StartMonitoringAsync();
    }

    private void InitCounters()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var instance in category.GetInstanceNames())
                {
                    if (instance.EndsWith("engtype_3D"))
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance);
                        counter.NextValue();
                        _winGpuCounters.Add(counter);
                    }
                }
            }
            catch { }
        }
    }

    private async Task LoadGpuSpecsAsync()
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
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                LoadMacSpecs();
            }
        });
    }

    private async Task StartMonitoringAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int tick = 0;

        // Resume off the UI thread: on Windows/Linux this path can spawn an
        // nvidia-smi (or lspci) subprocess, which previously ran synchronously
        // on the UI thread every second - a major source of the drag-lag.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;

            // Spawning an external process every single second is wasteful even
            // off the UI thread; every other tick is plenty for a live readout.
            if (tick % 2 != 1) continue;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    UpdateWindowsMetrics();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    UpdateLinuxMetrics();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    UpdateMacMetrics();
                }
            }
            catch { }
        }
    }

    private void LoadWindowsSpecs()
    {
        string nvidiaName = ExecuteCommand("nvidia-smi", "--query-gpu=name,driver_version --format=csv,noheader");
        if (!string.IsNullOrWhiteSpace(nvidiaName) && nvidiaName.Contains(","))
        {
            var parts = nvidiaName.Split(',');
            GpuName = parts[0].Trim();
            DriverVersion = parts[1].Trim();
            DirectXVersion = "DirectX 12";
            PhysicalLocation = "PCI bus 1, device 0, function 0";
            return;
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            string selectedName = string.Empty;

            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Generic GPU";

                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    AssignWmiDetails(obj);
                    break;
                }

                if (string.IsNullOrEmpty(selectedName))
                {
                    selectedName = name;
                    AssignWmiDetails(obj);
                }
            }
        }
        catch
        {
            GpuName = "Windows Graphics Device";
        }
    }

    private void AssignWmiDetails(System.Management.ManagementObject obj)
    {
        GpuName = obj["Name"]?.ToString() ?? "Generic GPU";
        DriverVersion = obj["DriverVersion"]?.ToString() ?? "N/A";
        DirectXVersion = "DirectX 12";
        PhysicalLocation = obj["PNPDeviceID"]?.ToString() ?? "PCI Bus";

        if (DateTime.TryParse(obj["DriverDate"]?.ToString(), out var date))
            DriverDate = date.ToShortDateString();
    }

    private void UpdateWindowsMetrics()
    {
        string smiOutput = ExecuteCommand("nvidia-smi", "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits");

        if (!string.IsNullOrWhiteSpace(smiOutput) && smiOutput.Contains(","))
        {
            var parts = smiOutput.Split(',');
            double? util = parts.Length >= 2 && double.TryParse(parts[0].Trim(), out double u) ? u : null;
            double? temp = parts.Length >= 2 && double.TryParse(parts[1].Trim(), out double t) ? t : null;
            string? memoryText = parts.Length >= 4 ? $"{parts[2].Trim()} MB / {parts[3].Trim()} MB" : null;

            Dispatcher.UIThread.Post(() =>
            {
                if (util.HasValue)
                {
                    UtilizationValue = util.Value;
                    Utilization = $"{util.Value}%";
                }
                if (temp.HasValue) Temperature = $"{temp.Value} °C";
                if (memoryText != null) MemoryUsage = memoryText;
            });
            return;
        }

        if (_winGpuCounters.Count > 0)
        {
            try
            {
                float totalUtil = 0;
                foreach (var counter in _winGpuCounters)
                {
                    totalUtil += counter.NextValue();
                }

                double usage = Math.Min(Math.Round(totalUtil, 1), 100);
                Dispatcher.UIThread.Post(() =>
                {
                    UtilizationValue = usage;
                    Utilization = $"{usage}%";
                });
            }
            catch { }
        }
    }

    private string? _drmDevicePath;   // e.g. /sys/class/drm/card1/device
    private bool _useNvidiaSmi;

    private static string? ReadSysfs(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static bool TryParseDouble(string? text, out double value)
        => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // nvidia-smi prints one line per GPU; only the first GPU is shown.
    private static string FirstLine(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    // Picks the DRM card to monitor: the one with the most dedicated VRAM (so a discrete GPU wins
    // over an integrated one), otherwise the first card. Avoids assuming the GPU is "card0".
    private static string? FindDrmDevice()
    {
        try
        {
            const string root = "/sys/class/drm";
            if (!Directory.Exists(root)) return null;

            string? best = null;
            long bestScore = -1;
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(dir);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^card\d+$")) continue;

                string dev = Path.Combine(dir, "device");
                if (!Directory.Exists(dev)) continue;

                long score = 0;
                if (long.TryParse(ReadSysfs(Path.Combine(dev, "mem_info_vram_total")),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out long vram))
                    score = vram;

                if (best == null || score > bestScore)
                {
                    best = dev;
                    bestScore = score;
                }
            }
            return best;
        }
        catch { return null; }
    }

    private static double? ReadDrmTemperatureC(string devicePath)
    {
        try
        {
            string hwmonRoot = Path.Combine(devicePath, "hwmon");
            if (!Directory.Exists(hwmonRoot)) return null;
            foreach (var hw in Directory.EnumerateDirectories(hwmonRoot))
            {
                string? raw = ReadSysfs(Path.Combine(hw, "temp1_input"));
                if (TryParseDouble(raw, out double milli)) return milli / 1000.0;
            }
        }
        catch { }
        return null;
    }

    private static string FormatMb(double bytes) => $"{Math.Round(bytes / (1024.0 * 1024.0))}";

    // lspci names are long ("Advanced Micro Devices, Inc. [AMD/ATI] Picasso/Raven 2 ... (rev c9)") and get cut
    // off in the UI, so shorten the vendor prefix and drop the revision suffix.
    private static string CleanGpuName(string name)
    {
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(rev [0-9a-fA-F]+\)\s*$", "");
        name = name.Replace("Advanced Micro Devices, Inc. [AMD/ATI]", "AMD")
                   .Replace("Advanced Micro Devices, Inc.", "AMD")
                   .Replace("NVIDIA Corporation", "NVIDIA")
                   .Replace("Intel Corporation", "Intel");
        return name.Trim();
    }

    private void LoadLinuxSpecs()
    {
        // 1. NVIDIA proprietary driver.
        string nameOutput = FirstLine(ExecuteCommand("nvidia-smi",
            "--query-gpu=name,driver_version,pci.bus_id --format=csv,noheader"));
        if (!string.IsNullOrWhiteSpace(nameOutput) && nameOutput.Contains(","))
        {
            var parts = nameOutput.Split(',');
            GpuName = parts[0].Trim();
            DriverVersion = parts.Length > 1 ? parts[1].Trim() : "N/A";
            DirectXVersion = "Vulkan / OpenGL";
            PhysicalLocation = parts.Length > 2 ? parts[2].Trim() : "/dev/nvidia0";
            _useNvidiaSmi = true;
            return;
        }

        // 2. Generic DRM path: AMD (amdgpu), Intel (i915/xe), Nouveau, etc.
        _drmDevicePath = FindDrmDevice();
        if (_drmDevicePath != null)
        {
            string? slot = null;
            string? driver = null;
            try { slot = new DirectoryInfo(_drmDevicePath).ResolveLinkTarget(true)?.Name; } catch { }
            try { driver = new DirectoryInfo(Path.Combine(_drmDevicePath, "driver")).ResolveLinkTarget(true)?.Name; } catch { }

            string? name = null;
            if (!string.IsNullOrEmpty(slot))
            {
                string lspciLine = FirstLine(ExecuteCommand("lspci", $"-s {slot}"));
                // "00:02.0 VGA compatible controller: Intel Corporation ..." -> text after the class.
                int idx = lspciLine.IndexOf(": ", StringComparison.Ordinal);
                if (idx >= 0) name = CleanGpuName(lspciLine.Substring(idx + 2));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = ReadSysfs(Path.Combine(_drmDevicePath, "vendor")) switch
                {
                    "0x1002" => "AMD Graphics",
                    "0x8086" => "Intel Graphics",
                    "0x10de" => "NVIDIA Graphics",
                    _ => "Graphics Device"
                };
            }

            GpuName = name!;
            DirectXVersion = "Vulkan / OpenGL";
            PhysicalLocation = slot ?? "PCI";

            string kernel = ReadSysfs("/proc/sys/kernel/osrelease") ?? "";
            string? moduleVersion = driver != null ? ReadSysfs($"/sys/module/{driver}/version") : null;
            if (!string.IsNullOrEmpty(moduleVersion)) DriverVersion = $"{driver} {moduleVersion}";
            else if (driver != null) DriverVersion = string.IsNullOrEmpty(kernel) ? driver : $"{driver} (kernel {kernel})";
            return;
        }

        // 3. Last resort: parse lspci directly.
        string lspciOutput = ExecuteCommand("lspci", "");
        foreach (var line in lspciOutput.Split('\n'))
        {
            if (line.Contains("VGA compatible controller") || line.Contains("3D controller"))
            {
                // Name is the text after "<class>: ", not after the first ':' (which is in the PCI address).
                int idx = line.IndexOf(": ", StringComparison.Ordinal);
                GpuName = idx >= 0 ? CleanGpuName(line.Substring(idx + 2)) : line.Trim();
                DirectXVersion = "Vulkan / OpenGL";
                PhysicalLocation = line.Split(' ')[0];
                break;
            }
        }

        if (GpuName.Contains("Detecting", StringComparison.OrdinalIgnoreCase))
            GpuName = "No GPU detected";
    }

    private void UpdateLinuxMetrics()
    {
        if (_useNvidiaSmi)
        {
            string smiResult = FirstLine(ExecuteCommand("nvidia-smi",
                "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits"));
            if (!string.IsNullOrWhiteSpace(smiResult) && smiResult.Contains(","))
            {
                var parts = smiResult.Split(',');
                bool hasUtil = TryParseDouble(parts[0], out double util);
                double temp = 0;
                bool hasTemp = parts.Length > 1 && TryParseDouble(parts[1], out temp);
                string? memText = parts.Length >= 4 && TryParseDouble(parts[2], out _) && TryParseDouble(parts[3], out _)
                    ? $"{parts[2].Trim()} MB / {parts[3].Trim()} MB"
                    : null;

                Dispatcher.UIThread.Post(() =>
                {
                    if (hasUtil)
                    {
                        UtilizationValue = util;
                        Utilization = $"{util}%";
                    }
                    if (hasTemp) Temperature = $"{temp} °C";
                    if (memText != null) MemoryUsage = memText;
                });
                return;
            }
        }

        try
        {
            _drmDevicePath ??= FindDrmDevice();
            if (_drmDevicePath == null) return;

            // amdgpu exposes busy %, VRAM and GTT (shared) usage; Intel/Nouveau expose little or none of it.
            bool hasUtil = TryParseDouble(ReadSysfs(Path.Combine(_drmDevicePath, "gpu_busy_percent")), out double util);
            double? temp = ReadDrmTemperatureC(_drmDevicePath);

            string? vramText = null;
            if (TryParseDouble(ReadSysfs(Path.Combine(_drmDevicePath, "mem_info_vram_used")), out double vramUsed) &&
                TryParseDouble(ReadSysfs(Path.Combine(_drmDevicePath, "mem_info_vram_total")), out double vramTotal) &&
                vramTotal > 0)
                vramText = $"{FormatMb(vramUsed)} MB / {FormatMb(vramTotal)} MB";

            string? gttText = null;
            if (TryParseDouble(ReadSysfs(Path.Combine(_drmDevicePath, "mem_info_gtt_used")), out double gttUsed) &&
                TryParseDouble(ReadSysfs(Path.Combine(_drmDevicePath, "mem_info_gtt_total")), out double gttTotal) &&
                gttTotal > 0)
                gttText = $"{FormatMb(gttUsed)} MB / {FormatMb(gttTotal)} MB";

            Dispatcher.UIThread.Post(() =>
            {
                if (hasUtil)
                {
                    UtilizationValue = util;
                    Utilization = $"{util}%";
                }
                if (temp.HasValue) Temperature = $"{Math.Round(temp.Value)} °C";
                if (vramText != null) MemoryUsage = vramText;
                if (gttText != null) SharedMemoryUsage = gttText;
            });
        }
        catch { }
    }

    private void LoadMacSpecs()
    {
        string profilerOutput = ExecuteCommand("system_profiler", "SPDisplaysDataType");
        foreach (var line in profilerOutput.Split('\n'))
        {
            if (line.Contains("Chipset Model:"))
            {
                GpuName = line.Replace("Chipset Model:", "").Trim();
                DirectXVersion = "Metal";
                PhysicalLocation = "Integrated / Apple Silicon";
                break;
            }
        }
    }

    private void UpdateMacMetrics()
    {
        Dispatcher.UIThread.Post(() => Utilization = "Active");
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