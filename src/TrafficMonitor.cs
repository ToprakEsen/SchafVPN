using System.Net.Http.Headers;
using System.Text.Json;

namespace SchafVPN;

/// <summary>sing-box Clash API'sinin /traffic akışından anlık hızları okur.</summary>
public static class TrafficMonitor
{
    public static async Task RunAsync(int port, string secret, Action<long, long> onSample, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var url = $"http://127.0.0.1:{port}/traffic";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    if (line.Length == 0) continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    onSample(root.GetProperty("up").GetInt64(), root.GetProperty("down").GetInt64());
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
