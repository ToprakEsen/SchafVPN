using System.Net.NetworkInformation;

namespace SchafVPN;

/// <summary>TUN arayüzünün bayt sayaçlarından saniyelik indirme/yükleme hızını okur.</summary>
public static class TrafficMonitor
{
    public static async Task RunAsync(string interfaceName, Action<long, long> onSample, CancellationToken ct)
    {
        long lastRx = -1, lastTx = -1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == interfaceName);
                if (nic != null)
                {
                    // Sistemin arayüze gönderdiği = yükleme, arayüzden aldığı = indirme
                    var s = nic.GetIPStatistics();
                    long rx = s.BytesReceived, tx = s.BytesSent;
                    if (lastRx >= 0 && rx >= lastRx && tx >= lastTx) onSample(tx - lastTx, rx - lastRx);
                    lastRx = rx; lastTx = tx;
                }
                else { lastRx = lastTx = -1; }
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
