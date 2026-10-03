using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TaskManager.Providers;

namespace TaskManager.ViewModels;

public partial class WifiViewModel : ViewModelBase
{
    private string _adapterName = "Wi-Fi";
    public string AdapterName { get => _adapterName; set => SetProperty(ref _adapterName, value); }

    private string _status = "Disconnected";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string _sendSpeed = "0 Kbps";
    public string SendSpeed { get => _sendSpeed; set => SetProperty(ref _sendSpeed, value); }

    private string _receiveSpeed = "0 Kbps";
    public string ReceiveSpeed { get => _receiveSpeed; set => SetProperty(ref _receiveSpeed, value); }

    private double _sendValue = 0;
    public double SendValue { get => _sendValue; set => SetProperty(ref _sendValue, value); }

    private double _receiveValue = 0;
    public double ReceiveValue { get => _receiveValue; set => SetProperty(ref _receiveValue, value); }

    private string _ssid = "Not connected";
    public string Ssid { get => _ssid; set => SetProperty(ref _ssid, value); }

    private string _connectionType = "-";
    public string ConnectionType { get => _connectionType; set => SetProperty(ref _connectionType, value); }

    private string _ipv4Address = "-";
    public string Ipv4Address { get => _ipv4Address; set => SetProperty(ref _ipv4Address, value); }

    private string _ipv6Address = "-";
    public string Ipv6Address { get => _ipv6Address; set => SetProperty(ref _ipv6Address, value); }

    private string _signalStrength = "-";
    public string SignalStrength { get => _signalStrength; set => SetProperty(ref _signalStrength, value); }

    public string AdapterNameLabel { get; } = "Wi-Fi";

    private readonly IWifiProvider _provider;

    public WifiViewModel() : this(ProviderFactory.CreateWifi()) { }

    public WifiViewModel(IWifiProvider provider)
    {
        _provider = provider;
        _ = StartNetworkMonitoringAsync();
    }

    private async Task StartNetworkMonitoringAsync()
    {
        long oldBytesSent = 0;
        long oldBytesReceived = 0;
        int tick = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            tick++;
            try
            {
                var snap = _provider.GetSnapshot();
                if (snap != null)
                {
                    string? sendText = null, receiveText = null;
                    double? sendKbps = null, receiveKbps = null;
                    bool connected = snap.Status == "Connected";

                    if (connected && snap.BytesSent.HasValue && snap.BytesReceived.HasValue)
                    {
                        long sent = snap.BytesSent.Value;
                        long received = snap.BytesReceived.Value;

                        if (oldBytesSent > 0 && oldBytesReceived > 0)
                        {
                            double bitsSent = Math.Max(0, sent - oldBytesSent) * 8.0;
                            double bitsReceived = Math.Max(0, received - oldBytesReceived) * 8.0;
                            sendKbps = bitsSent / 1000.0;
                            receiveKbps = bitsReceived / 1000.0;
                            sendText = FormatBitrate(bitsSent);
                            receiveText = FormatBitrate(bitsReceived);
                        }

                        oldBytesSent = sent;
                        oldBytesReceived = received;
                    }
                    else
                    {
                        oldBytesSent = 0;
                        oldBytesReceived = 0;
                        if (connected)
                        {
                            sendText = receiveText = "Not available on Android";
                            sendKbps = receiveKbps = 0;
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!connected)
                        {
                            ResetToDisconnectedState(snap.AdapterName);
                            return;
                        }

                        AdapterName = snap.AdapterName;
                        Status = snap.Status;
                        Ssid = snap.Ssid;
                        ConnectionType = snap.ConnectionType;
                        SignalStrength = snap.SignalStrength;
                        Ipv4Address = snap.Ipv4Address;
                        Ipv6Address = snap.Ipv6Address;

                        if (sendKbps.HasValue) SendValue = sendKbps.Value;
                        if (receiveKbps.HasValue) ReceiveValue = receiveKbps.Value;
                        if (sendText != null) SendSpeed = sendText;
                        if (receiveText != null) ReceiveSpeed = receiveText;
                    });
                    continue;
                }

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

                WifiDetails? details = tick % 5 == 1 ? _provider.GetDetails(wifiInterface.Name) : null;

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