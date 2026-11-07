using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace SchafVPN;

/// <summary>
/// vless:// paylaşım linkini çözümler ve iki config üretir:
///  - Xray: VLESS bağlantısını kurar (ML-KEM şifreleme, Reality, xhttp... hepsi Xray'de)
///  - sing-box: TUN arayüzü + rota + DNS; trafiği yerel SOCKS üzerinden Xray'e verir
/// v2rayN'in TUN modundaki yapıyla aynı.
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

    public string BuildXrayConfig(string logPath, int socksPort, string socksUser, string socksPass)
    {
        var user = new JsonObject { ["id"] = Uuid, ["encryption"] = Encryption };
        if (!string.IsNullOrEmpty(Flow)) user["flow"] = Flow;

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning", ["error"] = logPath, ["access"] = "none" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject
                    {
                        ["auth"] = "password",
                        ["accounts"] = new JsonArray { new JsonObject { ["user"] = socksUser, ["pass"] = socksPass } },
                        ["udp"] = true,
                        ["ip"] = "127.0.0.1",
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

    // ================================================================ sing-box (TUN)

    public string BuildSingBoxConfig(string logPath, int clashPort, string clashSecret,
                                     int socksPort, string socksUser, string socksPass)
    {
        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            // Xray'in sunucuya giden kendi trafiği tünele geri girmesin
            new JsonObject { ["process_name"] = Arr(["xray.exe"]), ["outbound"] = "direct" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
        };
        var dnsRules = new JsonArray
        {
            new JsonObject { ["process_name"] = Arr(["xray.exe"]), ["server"] = "dns-direct" },
        };

        if (IPAddress.TryParse(Host, out var ip))
        {
            var cidr = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"{ip}/128" : $"{ip}/32";
            routeRules.Insert(3, new JsonObject { ["ip_cidr"] = Arr([cidr]), ["outbound"] = "direct" });
        }
        else
        {
            dnsRules.Add(new JsonObject { ["domain"] = Arr([Host]), ["server"] = "dns-direct" });
            routeRules.Insert(3, new JsonObject { ["domain"] = Arr([Host]), ["outbound"] = "direct" });
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true, ["output"] = logPath },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{clashPort}",
                    ["secret"] = clashSecret,
                },
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "https", ["tag"] = "dns-remote", ["server"] = "1.1.1.1", ["detour"] = "proxy" },
                    new JsonObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = "1.1.1.1" },
                },
                ["rules"] = dnsRules,
                ["final"] = "dns-remote",
                ["strategy"] = "ipv4_only",
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "SchafVPN",
                    ["address"] = Arr(["172.19.0.1/30", "fdfe:dcba:9876::1/126"]),
                    ["mtu"] = 9000,
                    ["auto_route"] = true,
                    ["strict_route"] = true,
                    ["stack"] = "mixed",
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "proxy",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = socksPort,
                    ["version"] = "5",
                    ["username"] = socksUser,
                    ["password"] = socksPass,
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = routeRules,
                ["final"] = "proxy",
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "dns-direct",
            },
        };
        return config.ToJsonString(Indented);
    }
}
