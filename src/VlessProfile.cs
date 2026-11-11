using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace SchafVPN;

/// <summary>
/// vless:// paylaşım linkini çözümler ve Xray config'ini üretir. TUN arayüzünü, sistem
/// rotalarını ve DNS'i de Xray'in kendi TUN girişi yönetir (Android sürümüyle aynı yapı).
/// </summary>
public sealed class VlessProfile
{
    public required string Uuid { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string Name { get; init; } = "";
    public string Encryption { get; init; } = "none";
    public string Security { get; init; } = "none";   // none | tls | reality
    public string Transport { get; init; } = "tcp";
    public string Flow { get; init; } = "";
    public string Sni { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string PublicKey { get; init; } = "";
    public string ShortId { get; init; } = "";
    public string SpiderX { get; init; } = "";
    public string Mldsa65Verify { get; init; } = "";
    public string Alpn { get; init; } = "";
    public bool AllowInsecure { get; init; }
    public string Path { get; init; } = "";
    public string HostHeader { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string HeaderType { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Extra { get; init; } = "";

    public static VlessProfile Parse(string link)
    {
        link = link.Trim();
        if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Profil 'vless://' ile başlamalı.");

        var uri = new Uri(link);
        var q = HttpUtility.ParseQueryString(uri.Query);
        string P(string key) => q[key]?.Trim() ?? "";

        var host = uri.Host.Trim('[', ']');
        if (string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(host) || uri.Port <= 0)
            throw new FormatException("VLESS linkinde UUID, adres veya port eksik.");

        var type = P("type").ToLowerInvariant();
        if (type is "kcp" or "mkcp" or "quic" or "h2" or "http")
            throw new NotSupportedException($"'{type}' taşıma türü desteklenmiyor.");

        return new VlessProfile
        {
            Uuid = Uri.UnescapeDataString(uri.UserInfo),
            Host = host,
            Port = uri.Port,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Encryption = string.IsNullOrEmpty(P("encryption")) ? "none" : P("encryption"),
            Security = string.IsNullOrEmpty(P("security")) ? "none" : P("security").ToLowerInvariant(),
            Transport = type switch { "" or "tcp" or "raw" => "raw", "splithttp" => "xhttp", _ => type },
            Flow = P("flow"),
            Sni = P("sni"),
            Fingerprint = P("fp"),
            PublicKey = P("pbk"),
            ShortId = P("sid"),
            SpiderX = P("spx"),
            Mldsa65Verify = P("pqv"),
            Alpn = P("alpn"),
            AllowInsecure = P("allowInsecure") is "1" or "true",
            Path = P("path"),
            HostHeader = P("host"),
            ServiceName = P("serviceName"),
            HeaderType = P("headerType"),
            Mode = P("mode"),
            Extra = P("extra"),
        };
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Host}:{Port}" : Name;

    public string Detail
    {
        get
        {
            var parts = new List<string> { "VLESS" };
            if (Security == "reality") parts.Add("Reality");
            else if (Security == "tls") parts.Add("TLS");
            parts.Add(Transport switch
            {
                "ws" => "WebSocket",
                "grpc" => "gRPC",
                "httpupgrade" => "HTTPUpgrade",
                "xhttp" => "XHTTP",
                _ => "TCP",
            });
            if (Encryption.StartsWith("mlkem", StringComparison.OrdinalIgnoreCase)) parts.Add("ML-KEM");
            return string.Join(" · ", parts);
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => (JsonNode)s!).ToArray());
    private string[] AlpnList => Alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ================================================================ Xray

    /// <summary>TUN arayüzünün adı (ağ bağlantılarında görünür; trafik sayaçları buradan okunur).</summary>
    public const string TunName = "SchafVPN";

    /// <summary>
    /// Xray'in Windows TUN girişi + VLESS çıkışı.
    /// - gateway / dns / autoSystemRoutingTable: adresi, DNS'i ve varsayılan rotaları Xray kurar ve kaldırır.
    /// - Xray'in kendi bağlantıları varsayılan olarak fiziksel arayüze bağlanır (tünele geri girmez).
    /// - autoSystemWfpBlockLeak "dns": Windows'un DNS sorgularını diğer arayüzlerden sızdırmasını engeller.
    /// <paramref name="ipv6"/> false ise (sistemde IPv6 kapalı) IPv6 girdileri eklenmez; yoksa TUN başlamaz.
    /// </summary>
    public string BuildXrayConfig(string logPath, bool ipv6)
    {
        var user = new JsonObject { ["id"] = Uuid, ["encryption"] = Encryption };
        if (!string.IsNullOrEmpty(Flow)) user["flow"] = Flow;

        var gateway = new List<string> { "172.19.0.1/30" };
        var routes = new List<string> { "0.0.0.0/0" };
        if (ipv6) { gateway.Add("fdfe:dcba:9876::1/126"); routes.Add("::/0"); }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning", ["error"] = logPath, ["access"] = "none" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "tun-in",
                    ["port"] = 0,
                    ["protocol"] = "tun",
                    ["settings"] = new JsonObject
                    {
                        ["name"] = TunName,
                        ["mtu"] = 1500,
                        ["gateway"] = Arr(gateway),
                        ["dns"] = Arr(["1.1.1.1"]),
                        ["autoSystemRoutingTable"] = Arr(routes),
                        ["autoSystemWfpBlockLeak"] = Arr(["dns"]),
                    },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = Arr(["http", "tls", "quic"]),
                        ["routeOnly"] = true,
                    },
                },
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["ip"] = Arr(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8",
                                      "169.254.0.0/16", "fc00::/7", "fe80::/10"]),
                        ["outboundTag"] = "direct",
                    },
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "vless",
                    ["settings"] = new JsonObject
                    {
                        ["vnext"] = new JsonArray
                        {
                            new JsonObject { ["address"] = Host, ["port"] = Port, ["users"] = new JsonArray { user } },
                        },
                    },
                    ["streamSettings"] = BuildXrayStream(),
                },
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
            },
        };
        return config.ToJsonString(Indented);
    }

    private JsonObject BuildXrayStream()
    {
        var s = new JsonObject { ["network"] = Transport, ["security"] = Security };

        switch (Security)
        {
            case "reality":
            {
                if (string.IsNullOrEmpty(PublicKey))
                    throw new FormatException("Reality profili için 'pbk' (public key) eksik.");
                var r = new JsonObject
                {
                    ["serverName"] = Sni,
                    ["fingerprint"] = string.IsNullOrEmpty(Fingerprint) ? "chrome" : Fingerprint,
                    ["publicKey"] = PublicKey,
                    ["shortId"] = ShortId,
                    ["spiderX"] = SpiderX,
                };
                if (!string.IsNullOrEmpty(Mldsa65Verify)) r["mldsa65Verify"] = Mldsa65Verify;
                s["realitySettings"] = r;
                break;
            }
            case "tls":
            {
                var t = new JsonObject { ["serverName"] = string.IsNullOrEmpty(Sni) ? Host : Sni };
                if (!string.IsNullOrEmpty(Fingerprint)) t["fingerprint"] = Fingerprint;
                if (AlpnList.Length > 0) t["alpn"] = Arr(AlpnList);
                if (AllowInsecure) t["allowInsecure"] = true;
                s["tlsSettings"] = t;
                break;
            }
        }

        switch (Transport)
        {
            case "raw" when HeaderType == "http":
            {
                var req = new JsonObject { ["path"] = Arr([string.IsNullOrEmpty(Path) ? "/" : Path]) };
                if (!string.IsNullOrEmpty(HostHeader))
                    req["headers"] = new JsonObject { ["Host"] = Arr(HostHeader.Split(',')) };
                s["rawSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = req } };
                break;
            }
            case "ws":
                s["wsSettings"] = new JsonObject { ["path"] = OrSlash(Path), ["host"] = HostHeader };
                break;
            case "httpupgrade":
                s["httpupgradeSettings"] = new JsonObject { ["path"] = OrSlash(Path), ["host"] = HostHeader };
                break;
            case "grpc":
                s["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = ServiceName,
                    ["authority"] = HostHeader,
                    ["multiMode"] = Mode == "multi",
                };
                break;
            case "xhttp":
            {
                var x = new JsonObject
                {
                    ["path"] = OrSlash(Path),
                    ["host"] = HostHeader,
                    ["mode"] = string.IsNullOrEmpty(Mode) ? "auto" : Mode,
                };
                if (!string.IsNullOrEmpty(Extra))
                {
                    try { x["extra"] = JsonNode.Parse(Extra); }
                    catch { throw new FormatException("xhttp 'extra' parametresi geçerli JSON değil."); }
                }
                s["xhttpSettings"] = x;
                break;
            }
        }
        return s;
    }

    private static string OrSlash(string p) => string.IsNullOrEmpty(p) ? "/" : p;
}
