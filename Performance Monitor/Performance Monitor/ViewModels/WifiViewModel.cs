using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
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

    public string AdapterNameLabel { get; } = "Wi-Fi";

    public WifiViewModel()
    {
        _ = StartNetworkMonitoringAsync();
    }

    private async Task StartNetworkMonitoringAsync()
    {
        long oldBytesSent = 0;
        long oldBytesReceived = 0;
        int tick = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        // Resume off the UI thread. UpdateWifiDetails() can spawn 1-3 external
        // processes (netsh / nmcli / iwconfig / iw), each of which is allowed to
        // block for up to 2 seconds waiting for the process to exit - previously
        // that ran synchronously on the UI thread every single second, which was
        // by far the biggest contributor to the app stuttering while its window
        // was being dragged.
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;
            try
            {
                NetworkInterface? wifiInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(nic => (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                            nic.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) ||
                                            nic.Name.StartsWith("wlp", StringComparison.OrdinalIgnoreCase) ||
                                            nic.Name.StartsWith("wlx", StringComparison.OrdinalIgnoreCase))
                                           && nic.OperationalStatus == OperationalStatus.Up);

                wifiInterface ??= NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                           nic.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) ||
                                           nic.Name.StartsWith("wlp", StringComparison.OrdinalIgnoreCase) ||
                                           nic.Name.StartsWith("wlx", StringComparison.OrdinalIgnoreCase));

                if (wifiInterface == null)
                {
                    PostResetToDisconnectedState("Wi-Fi Adapter Not Found");
                    oldBytesSent = 0;
                    oldBytesReceived = 0;
                    continue;
                }

                string adapterName = string.IsNullOrWhiteSpace(wifiInterface.Description)
                    ? wifiInterface.Name
                    : wifiInterface.Description;

                if (wifiInterface.OperationalStatus != OperationalStatus.Up)
                {
                    PostResetToDisconnectedState(adapterName);
                    oldBytesSent = 0;
                    oldBytesReceived = 0;
                    continue;
                }

                // Adapter/SSID/signal details require spawning an external process -
                // that's expensive and doesn't change second-to-second, so refresh
                // it only every 5th tick. Bandwidth (below) still updates every tick,
                // since it's cheap (pure managed NetworkInterface API, no subprocess).
                WifiDetails? details = tick % 5 == 1 ? GetWifiDetails(wifiInterface.Name) : null;

                var ipProps = wifiInterface.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses
                    .FirstOrDefault(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork);
                var ipv6 = ipProps.UnicastAddresses
                    .FirstOrDefault(ip => ip.Address.AddressFamily == AddressFamily.InterNetworkV6);

                string ipv4Text = ipv4?.Address.ToString() ?? "-";
                string ipv6Text = ipv6?.Address.ToString() ?? "-";

                IPv4InterfaceStatistics stats = wifiInterface.GetIPv4Statistics();
                long newBytesSent = stats.BytesSent;
                long newBytesReceived = stats.BytesReceived;

                string? sendSpeedText = null, receiveSpeedText = null;
                double? sendValue = null, receiveValue = null;

                if (oldBytesSent > 0 && oldBytesReceived > 0)
                {
                    long bytesSentPerSec = Math.Max(0, newBytesSent - oldBytesSent);
                    long bytesReceivedPerSec = Math.Max(0, newBytesReceived - oldBytesReceived);

                    double bitsSentPerSec = bytesSentPerSec * 8;
                    double bitsReceivedPerSec = bytesReceivedPerSec * 8;

                    sendValue = bitsSentPerSec / 1000.0;
                    receiveValue = bitsReceivedPerSec / 1000.0;

                    sendSpeedText = FormatBitrate(bitsSentPerSec);
                    receiveSpeedText = FormatBitrate(bitsReceivedPerSec);
                }

                oldBytesSent = newBytesSent;
                oldBytesReceived = newBytesReceived;

                Dispatcher.UIThread.Post(() =>
                {
                    AdapterName = adapterName;
                    Ipv4Address = ipv4Text;
                    Ipv6Address = ipv6Text;

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
                PostResetToDisconnectedState("Wi-Fi");
            }
        }
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
            string status = "Disconnected";
            string ssid = "Not connected";
            string signalStrength = "-";
            bool detected = false;

            string nmcliOutput = ExecuteCommand("nmcli", "-t -f active,ssid,signal dev wifi");
            if (!string.IsNullOrWhiteSpace(nmcliOutput))
            {
                foreach (var line in nmcliOutput.Split('\n'))
                {
                    if (line.StartsWith("yes:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        if (parts.Length >= 3)
                        {
                            status = "Connected";
                            ssid = parts[1];
                            signalStrength = $"📶 {parts[2]}%";
                            detected = true;
                            break;
                        }
                    }
                }
            }

            if (!detected)
            {
                string iwOutput = ExecuteCommand("iwconfig", interfaceName);

                var ssidMatch = Regex.Match(iwOutput, @"ESSID:""([^""]+)""");
                if (ssidMatch.Success)
                {
                    status = "Connected";
                    ssid = ssidMatch.Groups[1].Value;
                }

                var signalMatch = Regex.Match(iwOutput, @"Link Quality=(\d+/\d+)");
                signalStrength = signalMatch.Success ? $"📶 {signalMatch.Groups[1].Value}" : "-";
            }

            string iwDevOutput = ExecuteCommand("iw", $"dev {interfaceName} link");
            var freqMatch = Regex.Match(iwDevOutput, @"freq:\s*(\d+)");
            string connectionType = freqMatch.Success && int.TryParse(freqMatch.Groups[1].Value, out int freq)
                ? (freq > 5000 ? "802.11ac/ax (5GHz)" : "802.11n/ax (2.4GHz)")
                : "802.11 Wireless";

            return new WifiDetails(status, ssid, connectionType, signalStrength);
        }
        else
        {
            return new WifiDetails("Connected", "Connected", "802.11", "📶 Connected");
        }
    }

    private void PostResetToDisconnectedState(string adapter)
    {
        Dispatcher.UIThread.Post(() => ResetToDisconnectedState(adapter));
    }

    private string FormatBitrate(double bitsPerSec)
    {
        double kbps = bitsPerSec / 1000.0;

        if (kbps >= 1000.0)
        {
            double mbps = kbps / 1000.0;
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

            process.Start();
            string result = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return result;
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
    }
}