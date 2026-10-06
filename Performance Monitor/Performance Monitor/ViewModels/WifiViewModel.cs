using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Net;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TaskManager.ViewModels;

public partial class WifiViewModel : ViewModelBase
{
    private string _adapterName = "Wi-Fi";
    public string AdapterName
    {
        get => _adapterName;
        set => SetProperty(ref _adapterName, value);
    }

    private string _status = "Disconnected";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private string _sendSpeed = "0 Kbps";
    public string SendSpeed
    {
        get => _sendSpeed;
        set => SetProperty(ref _sendSpeed, value);
    }

    private string _receiveSpeed = "0 Kbps";
    public string ReceiveSpeed
    {
        get => _receiveSpeed;
        set => SetProperty(ref _receiveSpeed, value);
    }

    private double _sendValue = 0;
    public double SendValue
    {
        get => _sendValue;
        set => SetProperty(ref _sendValue, value);
    }

    private double _receiveValue = 0;
    public double ReceiveValue
    {
        get => _receiveValue;
        set => SetProperty(ref _receiveValue, value);
    }

    private string _ssid = "Not connected";
    public string Ssid
    {
        get => _ssid;
        set => SetProperty(ref _ssid, value);
    }

    private string _connectionType = "-";
    public string ConnectionType
    {
        get => _connectionType;
        set => SetProperty(ref _connectionType, value);
    }

    private string _ipv4Address = "-";
    public string Ipv4Address
    {
        get => _ipv4Address;
        set => SetProperty(ref _ipv4Address, value);
    }

    private string _ipv6Address = "-";
    public string Ipv6Address
    {
        get => _ipv6Address;
        set => SetProperty(ref _ipv6Address, value);
    }

    private string _signalStrength = "-";
    public string SignalStrength
    {
        get => _signalStrength;
        set => SetProperty(ref _signalStrength, value);
    }

    // "Adapter name" row: "Wi-Fi" in Wi-Fi mode, the interface name (eth0, enp4s0...) in Ethernet mode.
    private string _adapterNameLabel = "Wi-Fi";
    public string AdapterNameLabel
    {
        get => _adapterNameLabel;
        set => SetProperty(ref _adapterNameLabel, value);
    }

    // The tab heading switches between Wi-Fi / Ethernet depending on which connection is in use.
    private string _tabTitle = "Wi-Fi";
    public string TabTitle
    {
        get => _tabTitle;
        set => SetProperty(ref _tabTitle, value);
    }

    // SSID / Signal strength make no sense for a cable, so they become Status / Link speed.
    private string _ssidLabel = "SSID:";
    public string SsidLabel
    {
        get => _ssidLabel;
        set => SetProperty(ref _ssidLabel, value);
    }

    private string _signalLabel = "Signal strength:";
    public string SignalLabel
    {
        get => _signalLabel;
        set => SetProperty(ref _signalLabel, value);
    }

    // Throughput graph scale in kbps. Auto-scales to the recent peak so wired speeds don't peg the bars.
    private double _graphMax = 100;
    public double GraphMax
    {
        get => _graphMax;
        set => SetProperty(ref _graphMax, value);
    }

    private string _graphMaxLabel = "100 Kbps";
    public string GraphMaxLabel
    {
        get => _graphMaxLabel;
        set => SetProperty(ref _graphMaxLabel, value);
    }

    private enum NetMode { WiFi, Ethernet }

    // Must be called on the UI thread.
    private void ApplyMode(NetMode mode, string? title = null)
    {
        if (mode == NetMode.Ethernet)
        {
            TabTitle = title ?? "Ethernet";
            SsidLabel = "Status:";
            SignalLabel = "Link speed:";
        }
        else
        {
            TabTitle = title ?? "Wi-Fi";
            SsidLabel = "SSID:";
            SignalLabel = "Signal strength:";
            AdapterNameLabel = "Wi-Fi";
        }
    }

    public WifiViewModel()
    {
        _ = StartNetworkMonitoringAsync();
    }

    private async Task StartNetworkMonitoringAsync()
    {
        string? lastIface = null;
        long oldBytesSent = 0;
        long oldBytesReceived = 0;
        bool havePrev = false;
        long prevMs = 0;
        var clock = Stopwatch.StartNew();
        var history = new Queue<double>(); // last 60 samples of max(send, receive) in kbps, for graph scaling
        int tick = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // Resume off the UI thread. GetWifiDetails() can spawn 1-3 external processes
        // (netsh / nmcli / iw / iwconfig), each allowed to block for a while, and that
        // used to run on the UI thread every second and make the window stutter.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;
            try
            {
                var allNics = NetworkInterface.GetAllNetworkInterfaces();
                var wifiUp = allNics.FirstOrDefault(nic => IsWirelessInterface(nic) && nic.OperationalStatus == OperationalStatus.Up);
                var wiredUp = PickWiredInterface(allNics);

                NetworkInterface active;
                NetMode mode;

                if (wifiUp != null && wiredUp != null)
                {
                    // Both connected: show the one that carries the default route. Wi-Fi wins a tie.
                    bool wiredOnly = HasIPv4Gateway(wiredUp) && !HasIPv4Gateway(wifiUp);
                    active = wiredOnly ? wiredUp : wifiUp;
                    mode = wiredOnly ? NetMode.Ethernet : NetMode.WiFi;
                }
                else if (wifiUp != null) { active = wifiUp; mode = NetMode.WiFi; }
                else if (wiredUp != null) { active = wiredUp; mode = NetMode.Ethernet; }
                else
                {
                    // Nothing connected: describe whichever kind of adapter exists but is down.
                    var wifiAny = allNics.FirstOrDefault(IsWirelessInterface);
                    var wiredAny = allNics.FirstOrDefault(IsWiredCandidate);
                    if (wifiAny != null) PostResetToDisconnectedState(DescribeAdapter(wifiAny), NetMode.WiFi, null);
                    else if (wiredAny != null) PostResetToDisconnectedState(DescribeAdapter(wiredAny), NetMode.Ethernet, null);
                    else PostResetToDisconnectedState("No network adapter found", NetMode.WiFi, "Network");

                    lastIface = null;
                    havePrev = false;
                    history.Clear();
                    continue;
                }

                string adapterName = DescribeAdapter(active);

                // Restart sampling when the monitored interface changes, so switching between
                // Wi-Fi and Ethernet doesn't produce a bogus throughput spike.
                bool ifaceChanged = active.Name != lastIface;
                if (ifaceChanged)
                {
                    havePrev = false;
                    history.Clear();
                    lastIface = active.Name;
                }

                // Wi-Fi details need a subprocess, so refresh them every 5th tick. Ethernet details
                // are read from the managed API and are cheap.
                WifiDetails? details = null;
                if (mode == NetMode.WiFi)
                {
                    if (tick % 5 == 1 || ifaceChanged) details = GetWifiDetails(active.Name);
                }
                else
                {
                    details = GetEthernetDetails(active);
                }

                var ipProps = active.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork);
                var ipv6 = ipProps.UnicastAddresses.FirstOrDefault(ip => ip.Address.AddressFamily == AddressFamily.InterNetworkV6);
                string ipv4Text = ipv4?.Address.ToString() ?? "-";
                string ipv6Text = ipv6?.Address.ToString() ?? "-";

                IPv4InterfaceStatistics stats = active.GetIPv4Statistics();
                long newBytesSent = stats.BytesSent;
                long newBytesReceived = stats.BytesReceived;
                long nowMs = clock.ElapsedMilliseconds;

                string? sendSpeedText = null, receiveSpeedText = null;
                double? sendValue = null, receiveValue = null;

                if (havePrev)
                {
                    double elapsedSec = Math.Max((nowMs - prevMs) / 1000.0, 0.001);
                    double bitsSentPerSec = Math.Max(0, newBytesSent - oldBytesSent) * 8 / elapsedSec;
                    double bitsReceivedPerSec = Math.Max(0, newBytesReceived - oldBytesReceived) * 8 / elapsedSec;

                    sendValue = bitsSentPerSec / 1000.0;
                    receiveValue = bitsReceivedPerSec / 1000.0;
                    sendSpeedText = FormatBitrate(bitsSentPerSec);
                    receiveSpeedText = FormatBitrate(bitsReceivedPerSec);

                    history.Enqueue(Math.Max(sendValue.Value, receiveValue.Value));
                    while (history.Count > 60) history.Dequeue();
                }

                oldBytesSent = newBytesSent;
                oldBytesReceived = newBytesReceived;
                prevMs = nowMs;
                havePrev = true;

                double graphMax = GraphScaleFor(history.Count > 0 ? history.Max() : 0);
                string graphMaxText = FormatBitrate(graphMax * 1000.0);
                bool wired = mode == NetMode.Ethernet;
                string ifaceName = active.Name;

                Dispatcher.UIThread.Post(() =>
                {
                    if (ifaceChanged) ApplyMode(mode);
                    if (wired) AdapterNameLabel = ifaceName;

                    AdapterName = adapterName;
                    Ipv4Address = ipv4Text;
                    Ipv6Address = ipv6Text;
                    GraphMax = graphMax;
                    GraphMaxLabel = graphMaxText;

                    if (sendValue.HasValue) SendValue = sendValue.Value;
                    if (receiveValue.HasValue) ReceiveValue = receiveValue.Value;
                    if (sendSpeedText != null) SendSpeed = sendSpeedText;
                    if (receiveSpeedText != null) ReceiveSpeed = receiveSpeedText;

                    if (details != null)
                    {
                        Status = details.Status;
                        Ssid = details.Ssid;
                        ConnectionType = details.ConnectionType;
                        SignalStrength = details.SignalStrength;
                    }
                });
            }
            catch
            {
                PostResetToDisconnectedState("Wi-Fi", NetMode.WiFi, null);
            }
        }
    }

    private static string DescribeAdapter(NetworkInterface nic)
        => string.IsNullOrWhiteSpace(nic.Description) ? nic.Name : nic.Description;

    // Smallest round scale (kbps) that fits the recent peak.
    private static double GraphScaleFor(double peakKbps)
    {
        double[] steps = { 100, 500, 1_000, 5_000, 10_000, 50_000, 100_000, 500_000, 1_000_000, 5_000_000, 10_000_000 };
        foreach (double step in steps)
            if (peakKbps <= step) return step;
        return Math.Ceiling(peakKbps / 1_000_000.0) * 1_000_000.0;
    }

    private sealed record WifiDetails(string Status, string Ssid, string ConnectionType, string SignalStrength);

    private WifiDetails GetWifiDetails(string interfaceName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string output = ExecuteCommand("netsh", "wlan show interfaces");

            var stateMatch = Regex.Match(output, @"^\s*State\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string state = stateMatch.Success ? stateMatch.Groups[1].Value.Trim() : "Disconnected";

            if (state.Equals("connected", StringComparison.OrdinalIgnoreCase))
            {
                var ssidMatch = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                string ssid = ssidMatch.Success && !string.IsNullOrWhiteSpace(ssidMatch.Groups[1].Value)
                    ? ssidMatch.Groups[1].Value.Trim()
                    : "Connected";

                var radioMatch = Regex.Match(output, @"^\s*Radio type\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                string connectionType = radioMatch.Success ? radioMatch.Groups[1].Value.Trim() : "Unknown";

                var signalMatch = Regex.Match(output, @"^\s*Signal\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                string signal = signalMatch.Success ? $"📶 {signalMatch.Groups[1].Value.Trim()}" : "-";

                return new WifiDetails("Connected", ssid, connectionType, signal);
            }

            return new WifiDetails("Disconnected", "Not connected", "Unknown", "-");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return GetLinuxWifiDetails(interfaceName);
        }
        else
        {
            return new WifiDetails("Connected", "Connected", "802.11", "📶 Connected");
        }
    }

    // Recognises wireless NICs across naming schemes (wlan0, wlp3s0, wlx<mac>, wlo1, ...).
    // On Linux the kernel also marks wireless devices with a /sys/class/net/<name>/wireless directory.
    private static bool IsWirelessInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return true;
        if (nic.Name.StartsWith("wl", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                Directory.Exists($"/sys/class/net/{nic.Name}/wireless"))
                return true;
        }
        catch { }
        return false;
    }

    private static string BandLabel(int freqMhz) =>
        freqMhz > 5900 ? "802.11ax (6GHz)" :
        freqMhz > 4900 ? "802.11ac/ax (5GHz)" :
        freqMhz > 0 ? "802.11n/ax (2.4GHz)" :
        "802.11 Wireless";

    // Rough dBm -> % conversion (the same mapping NetworkManager uses).
    private static int DbmToPercent(double dbm) => (int)Math.Clamp(2 * (dbm + 100), 0, 100);

    // nmcli's terse mode separates fields with ':' and escapes a literal ':' or '\' with a backslash,
    // so an SSID such as "Cafe:Guest" must not be split on its own colon.
    private static System.Collections.Generic.List<string> SplitTerse(string line)
    {
        var fields = new System.Collections.Generic.List<string>();
        var cur = new System.Text.StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length) { cur.Append(line[++i]); }
            else if (c == ':') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    private WifiDetails GetLinuxWifiDetails(string iface)
    {
        // 1. NetworkManager, restricted to this interface.
        string nm = ExecuteCommand("nmcli", $"-t -f active,ssid,signal,freq dev wifi list ifname {iface}");
        foreach (var raw in nm.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = SplitTerse(raw.Trim());
            if (f.Count >= 4 && f[0].Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                string ssid = string.IsNullOrWhiteSpace(f[1]) ? "Connected" : f[1];
                string signal = int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct) ? $"📶 {pct}%" : "-";
                var freqMatch = Regex.Match(f[3], @"\d+");
                int freq = freqMatch.Success ? int.Parse(freqMatch.Value, CultureInfo.InvariantCulture) : 0;
                return new WifiDetails("Connected", ssid, BandLabel(freq), signal);
            }
        }

        // 2. iw (no NetworkManager needed).
        string iw = ExecuteCommand("iw", $"dev {iface} link");
        if (!string.IsNullOrWhiteSpace(iw) && !iw.Contains("Not connected", StringComparison.OrdinalIgnoreCase))
        {
            var ssidM = Regex.Match(iw, @"^\s*SSID:\s*(.+)$", RegexOptions.Multiline);
            var freqM = Regex.Match(iw, @"freq:\s*(\d+)");
            var sigM = Regex.Match(iw, @"signal:\s*(-?\d+(?:\.\d+)?)\s*dBm");
            if (ssidM.Success || freqM.Success)
            {
                int freq = freqM.Success ? int.Parse(freqM.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                string signal = sigM.Success &&
                                double.TryParse(sigM.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbm)
                    ? $"📶 {DbmToPercent(dbm)}%" : "-";
                return new WifiDetails("Connected", ssidM.Success ? ssidM.Groups[1].Value.Trim() : "Connected", BandLabel(freq), signal);
            }
        }

        // 3. Legacy wireless-tools.
        string iwc = ExecuteCommand("iwconfig", iface);
        var essid = Regex.Match(iwc, "ESSID:\"([^\"]+)\"");
        if (essid.Success)
        {
            var q = Regex.Match(iwc, @"Link Quality=(\d+)/(\d+)");
            string signal = "-";
            if (q.Success &&
                double.TryParse(q.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                double.TryParse(q.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0)
                signal = $"📶 {(int)Math.Round(num / den * 100)}%";

            var fr = Regex.Match(iwc, @"Frequency[:=](\d+(?:\.\d+)?)\s*GHz");
            int freq = fr.Success &&
                       double.TryParse(fr.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ghz)
                ? (int)Math.Round(ghz * 1000) : 0;

            return new WifiDetails("Connected", essid.Groups[1].Value, BandLabel(freq), signal);
        }

        return new WifiDetails("Disconnected", "Not connected", "-", "-");
    }

    private void PostResetToDisconnectedState(string adapter, NetMode mode, string? title)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyMode(mode, title);
            ResetToDisconnectedState(adapter);
            if (mode == NetMode.Ethernet) AdapterNameLabel = adapter;
        });
    }

    // Real wired NICs only - never loopback, tunnels, VPNs, bridges, container/VM virtual adapters or
    // Bluetooth PAN, which operating systems also report as "Ethernet".
    private static bool IsWiredCandidate(NetworkInterface nic)
    {
        if (IsWirelessInterface(nic)) return false;

        switch (nic.NetworkInterfaceType)
        {
            case NetworkInterfaceType.Ethernet:
            case NetworkInterfaceType.GigabitEthernet:
            case NetworkInterfaceType.FastEthernetT:
            case NetworkInterfaceType.FastEthernetFx:
            case NetworkInterfaceType.Ethernet3Megabit:
                break;
            default:
                return false;
        }

        try
        {
            // Linux: everything virtual (docker0, veth*, br-*, virbr*, tun*, wg*, lo, ifb*...) lives here.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                Directory.Exists($"/sys/devices/virtual/net/{nic.Name}"))
                return false;
        }
        catch { }

        string text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        string[] virtualHints =
        {
            "virtual", "vethernet", "vmware", "vmnet", "virtualbox", "vboxnet", "hyper-v", "docker",
            "veth", "bluetooth", "loopback", "tap-", "tailscale", "wireguard", "zerotier", "vpn",
            "pseudo", "tunnel", "utun", "awdl", "llw", "bridge"
        };
        foreach (var hint in virtualHints)
            if (text.Contains(hint)) return false;

        return true;
    }

    private static bool HasIPv4Gateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
        }
        catch { return false; }
    }

    // Best connected wired adapter: one with a default gateway first, then the fastest link.
    private static NetworkInterface? PickWiredInterface(NetworkInterface[] all)
    {
        return all
            .Where(nic => IsWiredCandidate(nic) && nic.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(nic => HasIPv4Gateway(nic))
            .ThenByDescending(nic => { try { return nic.Speed; } catch { return 0L; } })
            .FirstOrDefault();
    }

    private static string FormatLinkSpeed(long bitsPerSec)
    {
        if (bitsPerSec <= 0) return "-";
        if (bitsPerSec >= 1_000_000_000L) return $"{Math.Round(bitsPerSec / 1_000_000_000.0, 1)} Gbps";
        if (bitsPerSec >= 1_000_000L) return $"{Math.Round(bitsPerSec / 1_000_000.0)} Mbps";
        return $"{Math.Round(bitsPerSec / 1_000.0)} Kbps";
    }

    // Reuses WifiDetails: "Ssid" carries the Status row and "SignalStrength" carries the Link speed row
    // (their labels are switched by ApplyMode).
    private WifiDetails GetEthernetDetails(NetworkInterface nic)
    {
        long speed = 0;

        // Linux: sysfs reports the negotiated speed in Mbps (-1 when unknown, e.g. virtual NICs).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                string path = $"/sys/class/net/{nic.Name}/speed";
                if (File.Exists(path) &&
                    long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mbps) &&
                    mbps > 0)
                    speed = mbps * 1_000_000L;
            }
            catch { }
        }

        if (speed <= 0)
        {
            try { speed = nic.Speed; } catch { }
        }

        // Virtual or unplugged adapters report -1 / 0xFFFFFFFF Mbps; anything above 400 Gbps isn't a real link.
        if (speed <= 0 || speed > 400_000_000_000L) speed = 0;

        return new WifiDetails("Connected", "Connected", "Ethernet", FormatLinkSpeed(speed));
    }

    private string FormatBitrate(double bitsPerSec)
    {
        double kbps = bitsPerSec / 1000.0;

        if (kbps >= 1000.0)
        {
            double mbps = kbps / 1000.0;
            if (mbps >= 1000.0) return $"{Math.Round(mbps / 1000.0, 2)} Gbps";
            return $"{Math.Round(mbps, 1)} Mbps";
        }

        return $"{Math.Round(kbps, 0)} Kbps";
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
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            using (process)
            {
                process.Start();
                // Read asynchronously so a hung tool (e.g. nvidia-smi with a broken driver) can
                // never block the monitoring loop forever.
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(2000))
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

    private void ResetToDisconnectedState(string adapter)
    {
        AdapterName = adapter;
        Status = "Disconnected";
        Ssid = "Not connected";
        ConnectionType = "-";
        Ipv4Address = "-";
        Ipv6Address = "-";
        SignalStrength = "-";
        SendSpeed = "0 Kbps";
        ReceiveSpeed = "0 Kbps";
        SendValue = 0;
        ReceiveValue = 0;
        GraphMax = 100;
        GraphMaxLabel = "100 Kbps";
    }
}