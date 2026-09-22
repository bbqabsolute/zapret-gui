using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGUI
{
    public class VpnService
    {
        private readonly ZapretCore _core;
        private readonly HttpClient _httpClient;
        private readonly string _profilesFilePath;

        public ObservableCollection<VpnProfile> Profiles { get; } = new();
        public VpnProfile? ActiveProfile { get; private set; }
        public VpnConnectionStatus Status { get; private set; } = VpnConnectionStatus.Disconnected;
        public DateTime? ConnectedTime { get; private set; }
        public bool IsSystemProxyActive { get; private set; }

        private Process? _coreProcess;
        public bool IsCoreRunning => _coreProcess != null && !_coreProcess.HasExited;

        public event Action? StatusChanged;
        public event Action<string>? LogReceived;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public VpnService(ZapretCore core)
        {
            _core = core;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretGUI-VPN/1.10.2 (Windows NT 10.0; Win64; x64)");

            _profilesFilePath = Path.Combine(_core.ZapretRoot, "vpn_profiles.json");
            LoadProfiles();
        }

        public void Log(string message)
        {
            LogReceived?.Invoke(message);
            _core.Log(message);
        }

        #region Helper Utilities

        public static string SafeUnescape(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            try
            {
                return Uri.UnescapeDataString(str.Replace("+", " "));
            }
            catch
            {
                return str;
            }
        }

        public static string DecodeBase64(string base64)
        {
            string s = base64.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "").Replace(" ", "");
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            byte[] bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }

        public static bool TryParseEndpoint(string rawEndpoint, out string host, out int port, out string query, out string remark)
        {
            host = "";
            port = 443;
            query = "";
            remark = "";

            if (string.IsNullOrWhiteSpace(rawEndpoint)) return false;

            string rest = rawEndpoint.Trim();

            // Extract remark (#...)
            int hashIdx = rest.IndexOf('#');
            if (hashIdx >= 0)
            {
                remark = SafeUnescape(rest.Substring(hashIdx + 1).Trim());
                rest = rest.Substring(0, hashIdx).Trim();
            }

            // Extract query (?...)
            int qIdx = rest.IndexOf('?');
            if (qIdx >= 0)
            {
                query = rest.Substring(qIdx + 1).Trim();
                rest = rest.Substring(0, qIdx).Trim();
            }

            // Strip trailing slashes before port parsing (e.g. host:443/)
            rest = rest.TrimEnd('/');

            if (rest.StartsWith("["))
            {
                // IPv6 [2001:db8::1]:port
                int closeBracket = rest.IndexOf(']');
                if (closeBracket < 0) return false;
                host = rest.Substring(1, closeBracket - 1);
                string portPart = rest.Substring(closeBracket + 1).TrimStart(':').TrimEnd('/');
                if (int.TryParse(portPart, out int p)) port = p;
                else return false;
            }
            else
            {
                int colonIdx = rest.LastIndexOf(':');
                if (colonIdx > 0 && colonIdx < rest.Length - 1)
                {
                    host = rest.Substring(0, colonIdx);
                    string portPart = rest.Substring(colonIdx + 1).TrimEnd('/');
                    if (int.TryParse(portPart, out int p)) port = p;
                    else return false;
                }
                else
                {
                    host = rest;
                }
            }

            return !string.IsNullOrEmpty(host) && port > 0 && port <= 65535;
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;

            var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in pairs)
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 2)
                {
                    dict[kv[0].Trim()] = kv[1].Trim();
                }
                else if (kv.Length == 1)
                {
                    dict[kv[0].Trim()] = "";
                }
            }
            return dict;
        }

        #endregion

        #region Link Parsing & Import

        public static List<VpnProfile> ParseInput(string input)
        {
            var results = new List<VpnProfile>();
            if (string.IsNullOrWhiteSpace(input)) return results;

            // Check if input is Clash YAML configuration
            if (input.Contains("proxies:") || (input.Contains("name:") && input.Contains("server:") && input.Contains("type:")))
            {
                var yamlList = ParseClashProxies(input);
                if (yamlList.Count > 0)
                {
                    results.AddRange(yamlList);
                    return results;
                }
            }

            // Split into lines or whitespace-separated tokens
            string[] rawLines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in rawLines)
            {
                string trimmed = line.Trim().Trim('"', '\'', '`', '<', '>', ';', ',');
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith("//"))
                    continue;

                // Try parsing single link
                var profile = ParseSingleLink(trimmed);
                if (profile != null)
                {
                    results.Add(profile);
                }
                else
                {
                    // Check if line is base64 subscription blob
                    var fromBase64 = TryParseBase64Blob(trimmed);
                    if (fromBase64.Count > 0)
                    {
                        results.AddRange(fromBase64);
                    }
                }
            }

            // If line-by-line parsing found nothing, try decoding the whole input as a single base64 blob
            if (results.Count == 0)
            {
                var wholeBlob = TryParseBase64Blob(input.Trim());
                if (wholeBlob.Count > 0)
                {
                    results.AddRange(wholeBlob);
                }
            }

            return results;
        }

        public static VpnProfile? ParseSingleLink(string rawLink)
        {
            if (string.IsNullOrWhiteSpace(rawLink)) return null;
            string link = rawLink.Trim().Trim('"', '\'', '`', '<', '>');

            try
            {
                if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    return ParseVless(link);
                if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                    return ParseVmess(link);
                if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
                    return ParseShadowsocks(link);
                if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                    return ParseTrojan(link);
                if (link.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("wg://", StringComparison.OrdinalIgnoreCase))
                    return ParseWireGuard(link);
                if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
                    return ParseHysteria2(link);
                if (link.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
                    return ParseSocks5(link);
                if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (link.Contains("@") || link.Contains(":80") || link.Contains(":8080") || link.Contains(":3128") || link.Contains(":1080"))
                    {
                        return ParseHttpProxy(link);
                    }
                }
            }
            catch
            {
                // Ignore parse errors on malformed lines
            }

            return null;
        }

        private static List<VpnProfile> TryParseBase64Blob(string candidate)
        {
            var list = new List<VpnProfile>();
            try
            {
                string decoded = DecodeBase64(candidate);
                if (!string.IsNullOrEmpty(decoded) && (decoded.Contains("://") || decoded.Contains("proxies:") || decoded.Contains("{")))
                {
                    return ParseInput(decoded);
                }
            }
            catch { }
            return list;
        }

        private static VpnProfile? ParseVless(string link)
        {
            // Format: vless://uuid@host:port?query#remark
            string body = link.Substring("vless://".Length).Trim();
            int atIdx = body.IndexOf('@');
            if (atIdx <= 0) return null;

            string uuid = body.Substring(0, atIdx);
            string rest = body.Substring(atIdx + 1);

            if (!TryParseEndpoint(rest, out string host, out int port, out string query, out string remark))
                return null;

            var qParams = ParseQueryString(query);
            string security = qParams.GetValueOrDefault("security", "tls");
            string sni = qParams.GetValueOrDefault("sni", qParams.GetValueOrDefault("host", host));
            string type = qParams.GetValueOrDefault("type", "tcp");
            string path = SafeUnescape(qParams.GetValueOrDefault("path", ""));
            string pbk = qParams.GetValueOrDefault("pbk", "");
            string sid = qParams.GetValueOrDefault("sid", "");
            string flow = qParams.GetValueOrDefault("flow", "");
            string fp = qParams.GetValueOrDefault("fp", "");
            string alpn = qParams.GetValueOrDefault("alpn", "");

            return new VpnProfile
            {
                Protocol = VpnProtocol.Vless,
                Name = string.IsNullOrEmpty(remark) ? $"VLESS - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Uuid = uuid,
                Security = security,
                Sni = sni,
                NetworkType = type,
                Path = path,
                PublicKey = pbk,
                ShortId = sid,
                Flow = flow,
                Fingerprint = fp,
                Alpn = alpn,
                RawLink = link
            };
        }

        private static VpnProfile? ParseVmess(string link)
        {
            // Format: vmess://base64(JSON)[#remark]
            string body = link.Substring("vmess://".Length).Trim();
            string hashRemark = "";
            int hashIdx = body.IndexOf('#');
            if (hashIdx >= 0)
            {
                hashRemark = SafeUnescape(body.Substring(hashIdx + 1).Trim());
                body = body.Substring(0, hashIdx).Trim();
            }

            string json = DecodeBase64(body);
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string remark = root.TryGetProperty("ps", out var p1) ? p1.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(remark)) remark = hashRemark;

            string host = root.TryGetProperty("add", out var p2) ? p2.GetString() ?? "" : "";
            int port = 443;
            if (root.TryGetProperty("port", out var p3))
            {
                if (p3.ValueKind == JsonValueKind.Number) port = p3.GetInt32();
                else if (int.TryParse(p3.GetString(), out int p)) port = p;
            }

            string uuid = root.TryGetProperty("id", out var p4) ? p4.GetString() ?? "" : "";
            string net = root.TryGetProperty("net", out var p5) ? p5.GetString() ?? "tcp" : "tcp";
            string tls = root.TryGetProperty("tls", out var p6) ? p6.GetString() ?? "none" : "none";
            string sni = root.TryGetProperty("sni", out var p7) ? p7.GetString() ?? "" : (root.TryGetProperty("host", out var p8) ? p8.GetString() ?? "" : "");
            string path = root.TryGetProperty("path", out var p9) ? p9.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(host)) return null;

            return new VpnProfile
            {
                Protocol = VpnProtocol.Vmess,
                Name = string.IsNullOrEmpty(remark) ? $"VMESS - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Uuid = uuid,
                NetworkType = net,
                Security = tls,
                Sni = sni,
                Path = path,
                RawLink = link
            };
        }

        private static VpnProfile? ParseShadowsocks(string link)
        {
            // SIP002: ss://base64(method:password)@host:port[/?plugin=...]#remark
            // Legacy: ss://base64(method:password@host:port)#remark
            string body = link.Substring("ss://".Length).Trim();
            string remark = "";
            int hashIndex = body.IndexOf('#');
            if (hashIndex >= 0)
            {
                remark = SafeUnescape(body.Substring(hashIndex + 1).Trim());
                body = body.Substring(0, hashIndex).Trim();
            }

            string method = "";
            string password = "";
            string host = "";
            int port = 8388;

            if (body.Contains("@"))
            {
                var atParts = body.Split('@', 2);
                string creds = atParts[0];
                string endpointPart = atParts[1];

                // Check if creds is base64 (e.g. YWVzLTI1Ni1nY206c2VjcmV0)
                if (!creds.Contains(":"))
                {
                    try { creds = DecodeBase64(creds); } catch { }
                }

                var cParts = creds.Split(':', 2);
                if (cParts.Length == 2)
                {
                    method = cParts[0];
                    password = cParts[1];
                }

                if (TryParseEndpoint(endpointPart, out host, out port, out _, out string epRemark))
                {
                    if (string.IsNullOrEmpty(remark)) remark = epRemark;
                }
            }
            else
            {
                // Legacy format: ss://base64(method:password@host:port)
                try
                {
                    string decoded = DecodeBase64(body);
                    if (decoded.Contains("@"))
                    {
                        var atParts = decoded.Split('@', 2);
                        var cParts = atParts[0].Split(':', 2);
                        if (cParts.Length == 2)
                        {
                            method = cParts[0];
                            password = cParts[1];
                        }
                        TryParseEndpoint(atParts[1], out host, out port, out _, out _);
                    }
                }
                catch { return null; }
            }

            if (string.IsNullOrEmpty(host)) return null;

            return new VpnProfile
            {
                Protocol = VpnProtocol.Shadowsocks,
                Name = string.IsNullOrEmpty(remark) ? $"Shadowsocks - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Method = method,
                Password = password,
                Security = method,
                RawLink = link
            };
        }

        private static VpnProfile? ParseTrojan(string link)
        {
            // trojan://password@host:port[?query]#remark
            string body = link.Substring("trojan://".Length).Trim();
            int atIdx = body.IndexOf('@');
            if (atIdx <= 0) return null;

            string password = body.Substring(0, atIdx);
            string rest = body.Substring(atIdx + 1);

            if (!TryParseEndpoint(rest, out string host, out int port, out string query, out string remark))
                return null;

            var qParams = ParseQueryString(query);
            string sni = qParams.GetValueOrDefault("sni", host);
            string security = qParams.GetValueOrDefault("security", "tls");
            string type = qParams.GetValueOrDefault("type", "tcp");

            return new VpnProfile
            {
                Protocol = VpnProtocol.Trojan,
                Name = string.IsNullOrEmpty(remark) ? $"Trojan - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Password = password,
                Security = security,
                Sni = sni,
                NetworkType = type,
                RawLink = link
            };
        }

        private static VpnProfile? ParseWireGuard(string link)
        {
            // wireguard://privatekey@host:port[?query]#remark
            string prefix = link.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase) ? "wireguard://" : "wg://";
            string body = link.Substring(prefix.Length).Trim();
            int atIdx = body.IndexOf('@');
            if (atIdx <= 0) return null;

            string privKey = body.Substring(0, atIdx);
            string rest = body.Substring(atIdx + 1);

            if (!TryParseEndpoint(rest, out string host, out int port, out string query, out string remark))
                return null;

            var qParams = ParseQueryString(query);
            string pubKey = qParams.GetValueOrDefault("publickey", qParams.GetValueOrDefault("pbk", ""));

            return new VpnProfile
            {
                Protocol = VpnProtocol.WireGuard,
                Name = string.IsNullOrEmpty(remark) ? $"WireGuard - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Password = privKey,
                PublicKey = pubKey,
                Security = "wireguard",
                RawLink = link
            };
        }

        private static VpnProfile? ParseHysteria2(string link)
        {
            // hysteria2://password@host:port[?query]#remark
            string prefix = link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ? "hysteria2://" : "hy2://";
            string body = link.Substring(prefix.Length).Trim();
            int atIdx = body.IndexOf('@');
            if (atIdx <= 0) return null;

            string password = body.Substring(0, atIdx);
            string rest = body.Substring(atIdx + 1);

            if (!TryParseEndpoint(rest, out string host, out int port, out string query, out string remark))
                return null;

            var qParams = ParseQueryString(query);
            string sni = qParams.GetValueOrDefault("sni", host);

            return new VpnProfile
            {
                Protocol = VpnProtocol.Hysteria2,
                Name = string.IsNullOrEmpty(remark) ? $"Hysteria2 - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Password = password,
                Sni = sni,
                Security = "tls",
                RawLink = link
            };
        }

        private static VpnProfile? ParseSocks5(string link)
        {
            // socks5://[user:pass@]host:port#remark
            string prefix = link.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ? "socks5://" : "socks://";
            string body = link.Substring(prefix.Length).Trim();
            string user = "";
            string pass = "";

            int atIdx = body.IndexOf('@');
            string rest = body;
            if (atIdx > 0)
            {
                string creds = body.Substring(0, atIdx);
                rest = body.Substring(atIdx + 1);
                var cParts = creds.Split(':', 2);
                user = cParts[0];
                if (cParts.Length > 1) pass = cParts[1];
            }

            if (!TryParseEndpoint(rest, out string host, out int port, out _, out string remark))
                return null;

            return new VpnProfile
            {
                Protocol = VpnProtocol.Socks5,
                Name = string.IsNullOrEmpty(remark) ? $"SOCKS5 - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Uuid = user,
                Password = pass,
                Security = "socks5",
                RawLink = link
            };
        }

        private static VpnProfile? ParseHttpProxy(string link)
        {
            // http(s)://[user:pass@]host:port#remark
            string prefix = link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://" : "http://";
            string body = link.Substring(prefix.Length).Trim();
            string user = "";
            string pass = "";

            int atIdx = body.IndexOf('@');
            string rest = body;
            if (atIdx > 0)
            {
                string creds = body.Substring(0, atIdx);
                rest = body.Substring(atIdx + 1);
                var cParts = creds.Split(':', 2);
                user = cParts[0];
                if (cParts.Length > 1) pass = cParts[1];
            }

            if (!TryParseEndpoint(rest, out string host, out int port, out _, out string remark))
                return null;

            return new VpnProfile
            {
                Protocol = VpnProtocol.Http,
                Name = string.IsNullOrEmpty(remark) ? $"HTTP Proxy - {host}:{port}" : remark,
                Server = host,
                Port = port,
                Uuid = user,
                Password = pass,
                Security = "http",
                RawLink = link
            };
        }

        private static List<VpnProfile> ParseClashProxies(string yaml)
        {
            var list = new List<VpnProfile>();
            try
            {
                // Simple regex-based line/block parser for Clash proxies without heavy 3rd party YAML dependency
                var inlineMatches = Regex.Matches(yaml, @"-\s*\{([^}]+)\}");
                foreach (Match m in inlineMatches)
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var kvs = m.Groups[1].Value.Split(',');
                    foreach (var kv in kvs)
                    {
                        var parts = kv.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim().Trim('"', '\'');
                            string val = parts[1].Trim().Trim('"', '\'');
                            dict[key] = val;
                        }
                    }

                    var prof = CreateProfileFromDict(dict);
                    if (prof != null) list.Add(prof);
                }

                // If no inline proxies found, scan line-by-line block format
                if (list.Count == 0)
                {
                    string[] lines = yaml.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    Dictionary<string, string>? currentDict = null;

                    foreach (var rawLine in lines)
                    {
                        string line = rawLine.Trim();
                        if (line.StartsWith("- name:") || (line.StartsWith("-") && line.Contains("name:")))
                        {
                            if (currentDict != null)
                            {
                                var p = CreateProfileFromDict(currentDict);
                                if (p != null) list.Add(p);
                            }
                            currentDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            string afterDash = line.TrimStart('-', ' ').Trim();
                            var parts = afterDash.Split(':', 2);
                            if (parts.Length == 2) currentDict[parts[0].Trim()] = parts[1].Trim().Trim('"', '\'');
                        }
                        else if (currentDict != null)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length == 2)
                            {
                                string key = parts[0].Trim().Trim('"', '\'');
                                string val = parts[1].Trim().Trim('"', '\'');
                                currentDict[key] = val;
                            }
                        }
                    }

                    if (currentDict != null)
                    {
                        var p = CreateProfileFromDict(currentDict);
                        if (p != null) list.Add(p);
                    }
                }
            }
            catch { }
            return list;
        }

        private static VpnProfile? CreateProfileFromDict(Dictionary<string, string> dict)
        {
            if (!dict.TryGetValue("server", out var server) || string.IsNullOrEmpty(server)) return null;
            int port = 443;
            if (dict.TryGetValue("port", out var portStr) && int.TryParse(portStr, out int p)) port = p;

            string name = dict.GetValueOrDefault("name", $"{server}:{port}");
            string type = dict.GetValueOrDefault("type", "vless").ToLowerInvariant();

            VpnProtocol proto = type switch
            {
                "vless" => VpnProtocol.Vless,
                "vmess" => VpnProtocol.Vmess,
                "ss" or "shadowsocks" => VpnProtocol.Shadowsocks,
                "trojan" => VpnProtocol.Trojan,
                "wireguard" or "wg" => VpnProtocol.WireGuard,
                "hysteria2" or "hy2" => VpnProtocol.Hysteria2,
                "socks5" or "socks" => VpnProtocol.Socks5,
                "http" or "https" => VpnProtocol.Http,
                _ => VpnProtocol.Unknown
            };

            if (proto == VpnProtocol.Unknown) return null;

            return new VpnProfile
            {
                Protocol = proto,
                Name = name,
                Server = server,
                Port = port,
                Uuid = dict.GetValueOrDefault("uuid", dict.GetValueOrDefault("username", "")),
                Password = dict.GetValueOrDefault("password", ""),
                Method = dict.GetValueOrDefault("cipher", ""),
                Security = dict.GetValueOrDefault("tls", "tls") == "true" ? "tls" : dict.GetValueOrDefault("security", "none"),
                Sni = dict.GetValueOrDefault("servername", dict.GetValueOrDefault("sni", server)),
                NetworkType = dict.GetValueOrDefault("network", "tcp"),
                Path = dict.GetValueOrDefault("ws-path", dict.GetValueOrDefault("path", "")),
                PublicKey = dict.GetValueOrDefault("public-key", dict.GetValueOrDefault("pbk", "")),
                ShortId = dict.GetValueOrDefault("short-id", dict.GetValueOrDefault("sid", "")),
                RawLink = $"{type}://{name}@{server}:{port}"
            };
        }

        #endregion

        #region Subscription URL Fetching

        public async Task<(bool success, int importedCount, string message)> FetchSubscriptionAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return (false, 0, "URL подписки пуст.");

            try
            {
                Log($"[VPN-Beta] Загрузка подписки по адресу: {url}");
                using var response = await _httpClient.GetAsync(url.Trim());
                if (!response.IsSuccessStatusCode)
                {
                    return (false, 0, $"Сервер вернул ошибку: {(int)response.StatusCode} {response.ReasonPhrase}");
                }

                string content = await response.Content.ReadAsStringAsync();
                var parsed = ParseInput(content);

                if (parsed.Count == 0)
                {
                    parsed = TryParseBase64Blob(content);
                }

                if (parsed.Count == 0)
                {
                    return (false, 0, "В полученном ответе не найдено поддерживаемых конфигураций VPN/прокси.");
                }

                int added = 0;
                foreach (var profile in parsed)
                {
                    if (!Profiles.Any(p => p.RawLink == profile.RawLink && p.Server == profile.Server && p.Port == profile.Port))
                    {
                        Profiles.Add(profile);
                        added++;
                    }
                }

                SaveProfiles();
                Log($"[VPN-Beta] Загрузка завершена. Добавлено узлов: {added}, всего в списке: {Profiles.Count}");
                return (true, added, $"Успешно импортировано {added} узлов из подписки.");
            }
            catch (Exception ex)
            {
                Log($"[VPN-Beta] Ошибка загрузки подписки: {ex.Message}");
                return (false, 0, $"Ошибка при загрузке: {ex.Message}");
            }
        }

        #endregion

        #region Ping & Latency Checking

        public async Task<int?> MeasurePingAsync(VpnProfile profile, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port <= 0)
            {
                profile.PingMs = -1;
                profile.PingStatus = "Неверный адрес";
                return -1;
            }

            profile.PingStatus = "Проверка...";
            var sw = Stopwatch.StartNew();

            try
            {
                using var client = new TcpClient();
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await client.ConnectAsync(profile.Server, profile.Port, linkedCts.Token);
                sw.Stop();

                int latency = (int)sw.ElapsedMilliseconds;
                profile.PingMs = latency;
                profile.PingStatus = $"{latency} ms";
                return latency;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                profile.PingMs = -1;
                profile.PingStatus = "Таймаут";
                return -1;
            }
            catch
            {
                sw.Stop();
                profile.PingMs = -1;
                profile.PingStatus = "Недоступен";
                return -1;
            }
        }

        public async Task MeasureAllPingsAsync(IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (Profiles.Count == 0) return;

            Log($"[VPN-Beta] Запуск проверки задержки (ping) для {Profiles.Count} узлов...");
            int completed = 0;
            using var semaphore = new SemaphoreSlim(6);

            var tasks = Profiles.Select(async p =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    await MeasurePingAsync(p, ct);
                }
                finally
                {
                    semaphore.Release();
                    int count = Interlocked.Increment(ref completed);
                    progress?.Report(count);
                }
            });

            await Task.WhenAll(tasks);
            Log("[VPN-Beta] Проверка ping завершена для всех узлов.");
        }

        #endregion

        #region Core Detection & Connection Management

        public string? FindCoreExecutable()
        {
            string singBoxInBin = Path.Combine(_core.ZapretRoot, "bin", "sing-box.exe");
            if (File.Exists(singBoxInBin)) return singBoxInBin;

            string xrayInBin = Path.Combine(_core.ZapretRoot, "bin", "xray.exe");
            if (File.Exists(xrayInBin)) return xrayInBin;

            string singBoxRoot = Path.Combine(_core.ZapretRoot, "sing-box.exe");
            if (File.Exists(singBoxRoot)) return singBoxRoot;

            string xrayRoot = Path.Combine(_core.ZapretRoot, "xray.exe");
            if (File.Exists(xrayRoot)) return xrayRoot;

            return null;
        }

        public static int GetAvailablePort(int startingPort = 10819)
        {
            for (int p = startingPort; p < startingPort + 200; p++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, p);
                    listener.Start();
                    listener.Stop();
                    return p;
                }
                catch { }
            }
            return startingPort;
        }

        public async Task<(bool success, string message)> EnsureCoreInstalledAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            string? existing = FindCoreExecutable();
            if (existing != null && File.Exists(existing))
            {
                return (true, $"Ядро уже установлено: {Path.GetFileName(existing)}");
            }

            string binDir = Path.Combine(_core.ZapretRoot, "bin");
            if (!Directory.Exists(binDir)) Directory.CreateDirectory(binDir);
            string targetExe = Path.Combine(binDir, "sing-box.exe");

            Log("[VPN] Автоматическая загрузка ядра sing-box для туннелирования трафика...");
            progress?.Report("Поиск и загрузка sing-box...");

            string downloadUrl = "https://github.com/SagerNet/sing-box/releases/download/v1.14.1/sing-box-1.14.1-windows-amd64.zip";

            try
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/SagerNet/sing-box/releases/latest");
                    req.Headers.UserAgent.ParseAdd("ZapretGUI-Installer");
                    using var resp = await _httpClient.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        string apiJson = await resp.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(apiJson);
                        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in assets.EnumerateArray())
                            {
                                string name = item.GetProperty("name").GetString() ?? "";
                                if (name.Contains("windows-amd64.zip", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = item.GetProperty("browser_download_url").GetString() ?? downloadUrl;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                Log($"[VPN] Загрузка архива sing-box: {downloadUrl}");
                progress?.Report("Загрузка архива sing-box...");

                string tempZip = Path.Combine(binDir, $"sing-box-dl-{Guid.NewGuid():N}.zip");
                string tempExtract = Path.Combine(binDir, $"sb-extract-{Guid.NewGuid():N}");

                try
                {
                    using (var resp = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                        await resp.Content.CopyToAsync(fs, ct);
                    }

                    progress?.Report("Распаковка sing-box.exe...");
                    Directory.CreateDirectory(tempExtract);
                    ZipFile.ExtractToDirectory(tempZip, tempExtract, true);

                    string[] found = Directory.GetFiles(tempExtract, "sing-box.exe", SearchOption.AllDirectories);
                    if (found.Length > 0)
                    {
                        if (File.Exists(targetExe)) File.Delete(targetExe);
                        File.Move(found[0], targetExe);
                        Log($"[VPN] ✅ Ядро sing-box успешно установлено в {targetExe}!");
                        progress?.Report("Ядро успешно установлено!");
                        return (true, "Ядро sing-box успешно установлено!");
                    }
                    else
                    {
                        return (false, "Файл sing-box.exe не найден в архиве.");
                    }
                }
                finally
                {
                    try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"[VPN] ❌ Ошибка загрузки sing-box: {ex.Message}");
                return (false, $"Не удалось загрузить sing-box: {ex.Message}");
            }
        }

        public static string FormatHostForUri(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return host;
            host = host.Trim();
            if (host.Contains(':') && !host.StartsWith("["))
            {
                return $"[{host}]";
            }
            return host;
        }

        public static void StopOrphanCoreProcesses()
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("sing-box"))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(400);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static async Task<bool> WaitForPortListeningAsync(string host, int port, int timeoutMs = 2500, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var probeCts = new CancellationTokenSource(100);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);
                    await tcp.ConnectAsync(host, port, linked.Token);
                    return true;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(50, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                }
            }
            return false;
        }

        public static object BuildSingBoxOutbound(VpnProfile profile)
        {
            switch (profile.Protocol)
            {
                case VpnProtocol.Vless:
                    var vlessOut = new Dictionary<string, object?>
                    {
                        ["type"] = "vless",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port,
                        ["uuid"] = profile.Uuid
                    };
                    bool isTlsOrReality = profile.Security != "none" && !string.IsNullOrWhiteSpace(profile.Security);
                    if (isTlsOrReality && !string.IsNullOrWhiteSpace(profile.Flow))
                    {
                        vlessOut["flow"] = profile.Flow;
                    }
                    if (isTlsOrReality)
                    {
                        var tlsDict = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["server_name"] = !string.IsNullOrWhiteSpace(profile.Sni) ? profile.Sni : profile.Server
                        };
                        if (profile.Security == "reality")
                        {
                            tlsDict["utls"] = new Dictionary<string, object?>
                            {
                                ["enabled"] = true,
                                ["fingerprint"] = !string.IsNullOrWhiteSpace(profile.Fingerprint) ? profile.Fingerprint : "chrome"
                            };
                            if (!string.IsNullOrWhiteSpace(profile.PublicKey))
                            {
                                var realityDict = new Dictionary<string, object?>
                                {
                                    ["enabled"] = true,
                                    ["public_key"] = profile.PublicKey
                                };
                                if (!string.IsNullOrWhiteSpace(profile.ShortId))
                                {
                                    realityDict["short_id"] = profile.ShortId;
                                }
                                tlsDict["reality"] = realityDict;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(profile.Fingerprint))
                        {
                            tlsDict["utls"] = new Dictionary<string, object?>
                            {
                                ["enabled"] = true,
                                ["fingerprint"] = profile.Fingerprint
                            };
                        }
                        vlessOut["tls"] = tlsDict;
                    }
                    if (profile.NetworkType == "ws")
                    {
                        var wsDict = new Dictionary<string, object?>
                        {
                            ["type"] = "ws",
                            ["path"] = !string.IsNullOrWhiteSpace(profile.Path) ? profile.Path : "/"
                        };
                        if (!string.IsNullOrWhiteSpace(profile.Sni))
                        {
                            wsDict["headers"] = new Dictionary<string, string>
                            {
                                ["Host"] = profile.Sni
                            };
                        }
                        vlessOut["transport"] = wsDict;
                    }
                    else if (profile.NetworkType == "grpc")
                    {
                        vlessOut["transport"] = new Dictionary<string, object?>
                        {
                            ["type"] = "grpc",
                            ["service_name"] = !string.IsNullOrWhiteSpace(profile.Path) ? profile.Path : ""
                        };
                    }
                    else if (profile.NetworkType == "http" || profile.NetworkType == "httpupgrade")
                    {
                        vlessOut["transport"] = new Dictionary<string, object?>
                        {
                            ["type"] = profile.NetworkType,
                            ["path"] = !string.IsNullOrWhiteSpace(profile.Path) ? profile.Path : "/"
                        };
                    }
                    return vlessOut;

                case VpnProtocol.Vmess:
                    var vmessOut = new Dictionary<string, object?>
                    {
                        ["type"] = "vmess",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port,
                        ["uuid"] = profile.Uuid,
                        ["security"] = "auto"
                    };
                    if (profile.Security == "tls")
                    {
                        vmessOut["tls"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["server_name"] = !string.IsNullOrWhiteSpace(profile.Sni) ? profile.Sni : profile.Server
                        };
                    }
                    if (profile.NetworkType == "ws")
                    {
                        var wsDict = new Dictionary<string, object?>
                        {
                            ["type"] = "ws",
                            ["path"] = !string.IsNullOrWhiteSpace(profile.Path) ? profile.Path : "/"
                        };
                        if (!string.IsNullOrWhiteSpace(profile.Sni))
                        {
                            wsDict["headers"] = new Dictionary<string, string>
                            {
                                ["Host"] = profile.Sni
                            };
                        }
                        vmessOut["transport"] = wsDict;
                    }
                    else if (profile.NetworkType == "grpc")
                    {
                        vmessOut["transport"] = new Dictionary<string, object?>
                        {
                            ["type"] = "grpc",
                            ["service_name"] = !string.IsNullOrWhiteSpace(profile.Path) ? profile.Path : ""
                        };
                    }
                    else if (profile.NetworkType == "http" || profile.NetworkType == "httpupgrade")
                    {
                        vmessOut["transport"] = new Dictionary<string, object?>
                        {
                            ["type"] = profile.NetworkType,
                            ["path"] = !string.IsNullOrWhiteSpace(profile.Path) ? profile.Path : "/"
                        };
                    }
                    return vmessOut;

                case VpnProtocol.Shadowsocks:
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "shadowsocks",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port,
                        ["method"] = profile.Method,
                        ["password"] = profile.Password
                    };

                case VpnProtocol.Trojan:
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "trojan",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port,
                        ["password"] = profile.Password,
                        ["tls"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["server_name"] = !string.IsNullOrWhiteSpace(profile.Sni) ? profile.Sni : profile.Server
                        }
                    };

                case VpnProtocol.WireGuard:
                    string privKey = !string.IsNullOrWhiteSpace(profile.Password) && profile.Password.Length >= 40
                        ? profile.Password
                        : "yAnz5TF+lXXJysqhZKGweG0tGuy8PfZq8qVU4KjyVnc=";
                    string pubKey = !string.IsNullOrWhiteSpace(profile.PublicKey) && profile.PublicKey.Length >= 40
                        ? profile.PublicKey
                        : "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=";
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "wireguard",
                        ["tag"] = "proxy",
                        ["address"] = new[] { "172.16.0.2/32" },
                        ["private_key"] = privKey,
                        ["peers"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["address"] = profile.Server,
                                ["port"] = profile.Port,
                                ["public_key"] = pubKey,
                                ["allowed_ips"] = new[] { "0.0.0.0/0" }
                            }
                        }
                    };

                case VpnProtocol.Hysteria2:
                    return new Dictionary<string, object?>
                    {
                        ["type"] = "hysteria2",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port,
                        ["password"] = profile.Password,
                        ["tls"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["server_name"] = !string.IsNullOrWhiteSpace(profile.Sni) ? profile.Sni : profile.Server
                        }
                    };

                case VpnProtocol.Socks5:
                    var socksOut = new Dictionary<string, object?>
                    {
                        ["type"] = "socks",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port
                    };
                    if (!string.IsNullOrEmpty(profile.Uuid)) socksOut["username"] = profile.Uuid;
                    if (!string.IsNullOrEmpty(profile.Password)) socksOut["password"] = profile.Password;
                    return socksOut;

                default:
                    var httpOut = new Dictionary<string, object?>
                    {
                        ["type"] = "http",
                        ["tag"] = "proxy",
                        ["server"] = profile.Server,
                        ["server_port"] = profile.Port
                    };
                    if (!string.IsNullOrEmpty(profile.Uuid)) httpOut["username"] = profile.Uuid;
                    if (!string.IsNullOrEmpty(profile.Password)) httpOut["password"] = profile.Password;
                    return httpOut;
            }
        }

        public static string BuildSingBoxRuntimeConfigJson(VpnProfile profile, int listenPort = 10808)
        {
            if (profile.Protocol == VpnProtocol.WireGuard)
            {
                object wgEndpoint = BuildSingBoxOutbound(profile);
                var wgConfig = new Dictionary<string, object>
                {
                    ["log"] = new Dictionary<string, object>
                    {
                        ["level"] = "warn"
                    },
                    ["inbounds"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "mixed",
                            ["tag"] = "mixed-in",
                            ["listen"] = "127.0.0.1",
                            ["listen_port"] = listenPort
                        }
                    },
                    ["endpoints"] = new object[]
                    {
                        wgEndpoint
                    },
                    ["outbounds"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "direct",
                            ["tag"] = "direct"
                        }
                    },
                    ["route"] = new Dictionary<string, object>
                    {
                        ["auto_detect_interface"] = true,
                        ["final"] = "proxy"
                    }
                };
                return JsonSerializer.Serialize(wgConfig, new JsonSerializerOptions { WriteIndented = true });
            }

            object outboundObj = BuildSingBoxOutbound(profile);

            var config = new Dictionary<string, object>
            {
                ["log"] = new Dictionary<string, object>
                {
                    ["level"] = "warn"
                },
                ["inbounds"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "mixed",
                        ["tag"] = "mixed-in",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = listenPort
                    }
                },
                ["outbounds"] = new object[]
                {
                    outboundObj,
                    new Dictionary<string, object>
                    {
                        ["type"] = "direct",
                        ["tag"] = "direct"
                    }
                },
                ["route"] = new Dictionary<string, object>
                {
                    ["auto_detect_interface"] = true,
                    ["final"] = "proxy"
                }
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        }

        public async Task<bool> ConnectAsync(VpnProfile profile, bool enableSystemProxy = false)
        {
            if (profile == null) return false;

            // Disconnect and cleanly terminate any previous connection or orphan sing-box processes
            await DisconnectAsync();
            StopOrphanCoreProcesses();

            Status = VpnConnectionStatus.Connecting;
            ActiveProfile = profile;
            StatusChanged?.Invoke();
            Log($"[VPN] Подключение к узлу '{profile.Name}' ({profile.ProtocolBadge} - {profile.Server}:{profile.Port})...");

            await Task.Delay(100);

            // If direct SOCKS5/HTTP without core requested and no core present:
            string? coreExe = FindCoreExecutable();
            if (coreExe == null && (profile.Protocol == VpnProtocol.Socks5 || profile.Protocol == VpnProtocol.Http))
            {
                if (enableSystemProxy)
                {
                    string hostStr = FormatHostForUri(profile.Server);
                    // Windows WinINet/WinHTTP proxy format:
                    // SOCKS5: "socks=host:port" or full format "socks5=host:port;socks=host:port"
                    // HTTP:   "http=host:port;https=host:port"
                    // For SOCKS5 we use socks=host:port (works for both SOCKS4 and SOCKS5 in WinINet)
                    // The "socks5=" prefix is NOT standard WinINet - use plain "socks=" which covers SOCKS5
                    string proxyArg = profile.Protocol == VpnProtocol.Socks5
                        ? $"socks={hostStr}:{profile.Port};http={hostStr}:{profile.Port};https={hostStr}:{profile.Port}"
                        : $"http={hostStr}:{profile.Port};https={hostStr}:{profile.Port}";

                    bool ok = SetWindowsSystemProxy(true, proxyArg);
                    if (ok)
                    {
                        IsSystemProxyActive = true;
                        Log($"[VPN] ✅ Системный прокси Windows активирован: {proxyArg}");
                        Log($"[VPN] Браузеры и системные приложения будут использовать прокси {profile.Protocol} на {hostStr}:{profile.Port}");
                    }
                    else
                    {
                        Log("[VPN] ⚠️ Не удалось установить системный прокси. Попробуйте запустить приложение с правами администратора.");
                    }
                }
                else
                {
                    Log($"[VPN] ℹ️ Системный прокси не активирован (отключен в настройках). Прокси {profile.Server}:{profile.Port} доступен для ручной настройки браузера.");
                }

                Status = VpnConnectionStatus.Connected;
                ConnectedTime = DateTime.Now;
                StatusChanged?.Invoke();
                Log($"[VPN] ✅ Подключено к прокси: {profile.Name} ({profile.ProtocolBadge} — {profile.Server}:{profile.Port})");
                return true;
            }

            // Ensure core exists for tunnel protocols
            if (coreExe == null)
            {
                Log("[VPN] Ядро sing-box не обнаружено в bin\\. Запуск автоматической установки...");
                var (installed, msg) = await EnsureCoreInstalledAsync();
                if (installed)
                {
                    coreExe = FindCoreExecutable();
                }
                else
                {
                    Log($"[VPN] ❌ Не удалось автоматически установить ядро sing-box: {msg}");
                    Status = VpnConnectionStatus.Error;
                    StatusChanged?.Invoke();
                    return false;
                }
            }

            try
            {
                string configDir = Path.Combine(_core.ZapretRoot, "bin");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
                string configPath = Path.Combine(configDir, "vpn_core_config.json");

                string jsonConfig = BuildSingBoxRuntimeConfigJson(profile, listenPort: 10808);
                await File.WriteAllTextAsync(configPath, jsonConfig, new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = coreExe!,
                    Arguments = $"run -c \"{configPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(coreExe) ?? _core.ZapretRoot
                };

                _coreProcess = new Process { StartInfo = psi };
                _coreProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Log($"[VPN-Core] {e.Data}");
                };
                _coreProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Log($"[VPN-Core] {e.Data}");
                };

                _coreProcess.Start();
                _coreProcess.BeginOutputReadLine();
                _coreProcess.BeginErrorReadLine();

                // Actively probe loopback port 10808 until sing-box mixed inbound is ready
                bool portReady = await WaitForPortListeningAsync("127.0.0.1", 10808, 2500);

                if (_coreProcess.HasExited)
                {
                    Log($"[VPN] ❌ Ядро {Path.GetFileName(coreExe)} завершило работу с ошибкой (код: {_coreProcess.ExitCode}). Проверьте журнал.");
                    Status = VpnConnectionStatus.Error;
                    StatusChanged?.Invoke();
                    return false;
                }

                if (!portReady)
                {
                    Log("[VPN] ⚠️ Предупреждение: порт 10808 ядра sing-box не ответил на первичную пробу, ожидание завершено.");
                }

                if (enableSystemProxy)
                {
                    bool ok = SetWindowsSystemProxy(true, "127.0.0.1:10808");
                    if (ok)
                    {
                        IsSystemProxyActive = true;
                        Log("[VPN] Системный прокси Windows активирован через локальное ядро (127.0.0.1:10808).");
                    }
                }

                Status = VpnConnectionStatus.Connected;
                ConnectedTime = DateTime.Now;
                StatusChanged?.Invoke();
                Log($"[VPN] ✅ Успешно подключено к узлу '{profile.Name}' ({profile.ProtocolBadge}) через ядро sing-box!");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[VPN] Ошибка запуска ядра: {ex.Message}");
                Status = VpnConnectionStatus.Error;
                StatusChanged?.Invoke();
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (Status == VpnConnectionStatus.Disconnected && _coreProcess == null) return;

            Status = VpnConnectionStatus.Disconnecting;
            StatusChanged?.Invoke();
            Log("[VPN] Отключение от VPN...");

            if (IsSystemProxyActive)
            {
                SetWindowsSystemProxy(false, "");
                IsSystemProxyActive = false;
                Log("[VPN] Системный прокси Windows отключен.");
            }

            if (_coreProcess != null)
            {
                try
                {
                    if (!_coreProcess.HasExited)
                    {
                        _coreProcess.Kill(entireProcessTree: true);
                        await _coreProcess.WaitForExitAsync();
                    }
                    _coreProcess.Dispose();
                }
                catch { }
                _coreProcess = null;
            }

            StopOrphanCoreProcesses();
            await Task.Delay(100);

            Status = VpnConnectionStatus.Disconnected;
            ActiveProfile = null;
            ConnectedTime = null;
            StatusChanged?.Invoke();

            Log("[VPN] ⏹ VPN отключен.");
        }

        #endregion

        #region HTTP GET Proxy Verification

        public async Task<(bool success, int latencyMs, string ip, string country, string city, string org, string message)> VerifyProxyViaHttpGetAsync(VpnProfile profile, CancellationToken ct = default)
        {
            if (profile == null) return (false, -1, "", "", "", "", "Профиль не задан");

            profile.HttpCheckStatus = "Проверка (GET)...";
            Log($"[VPN-Verify] Проверка узла '{profile.Name}' через HTTP GET запрос...");

            int localPort = 0;
            Process? transientProcess = null;
            string? tempConfigFile = null;

            try
            {
                if (ActiveProfile?.Id == profile.Id && Status == VpnConnectionStatus.Connected && IsCoreRunning)
                {
                    // Active running proxy session via sing-box
                    localPort = 10808;
                }
                else if (profile.Protocol == VpnProtocol.Socks5 || profile.Protocol == VpnProtocol.Http)
                {
                    // Direct SOCKS5 or HTTP proxy
                    string hostStr = FormatHostForUri(profile.Server);
                    string scheme = profile.Protocol == VpnProtocol.Socks5 ? "socks5" : "http";
                    return await ExecuteHttpGetCheckAsync(
                        new Uri($"{scheme}://{hostStr}:{profile.Port}"),
                        profile,
                        profile.Uuid,
                        profile.Password,
                        ct);
                }
                else
                {
                    // Protocols requiring sing-box (VLESS, VMess, SS, Trojan, WG, HY2)
                    string? coreExe = FindCoreExecutable();
                    if (coreExe == null)
                    {
                        var (installed, msg) = await EnsureCoreInstalledAsync(ct: ct);
                        if (!installed)
                        {
                            profile.HttpCheckStatus = "Нет sing-box";
                            profile.HttpPingMs = -1;
                            return (false, -1, "", "", "", "", "Ядро sing-box не установлено.");
                        }
                        coreExe = FindCoreExecutable();
                    }

                    localPort = GetAvailablePort(10820);
                    string configDir = Path.Combine(_core.ZapretRoot, "bin");
                    if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
                    tempConfigFile = Path.Combine(configDir, $"vpn_verify_{localPort}.json");

                    string runtimeConfig = BuildSingBoxRuntimeConfigJson(profile, listenPort: localPort);
                    await File.WriteAllTextAsync(tempConfigFile, runtimeConfig, new UTF8Encoding(false), ct);

                    var psi = new ProcessStartInfo
                    {
                        FileName = coreExe!,
                        Arguments = $"run -c \"{tempConfigFile}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(coreExe) ?? _core.ZapretRoot
                    };

                    transientProcess = Process.Start(psi);
                    if (transientProcess == null)
                    {
                        profile.HttpCheckStatus = "Ошибка ядра";
                        profile.HttpPingMs = -1;
                        return (false, -1, "", "", "", "", "Не удалось запустить sing-box для теста.");
                    }

                    // Actively probe loopback port until sing-box mixed inbound is ready
                    bool portReady = await WaitForPortListeningAsync("127.0.0.1", localPort, 2500, ct);

                    if (transientProcess.HasExited)
                    {
                        string err = await transientProcess.StandardError.ReadToEndAsync(ct);
                        Log($"[VPN-Verify] ❌ Тестовое ядро sing-box завершилось с ошибкой: {err}");
                        profile.HttpCheckStatus = "Ошибка конфигурации";
                        profile.HttpPingMs = -1;
                        return (false, -1, "", "", "", "", $"Ошибка sing-box: {err}");
                    }
                }

                return await ExecuteHttpGetCheckAsync(new Uri($"http://127.0.0.1:{localPort}"), profile, null, null, ct);
            }
            catch (Exception ex)
            {
                profile.HttpCheckStatus = "Ошибка";
                profile.HttpPingMs = -1;
                Log($"[VPN-Verify] ❌ Ошибка проверки узла '{profile.Name}': {ex.Message}");
                return (false, -1, "", "", "", "", ex.Message);
            }
            finally
            {
                if (transientProcess != null)
                {
                    try
                    {
                        if (!transientProcess.HasExited)
                        {
                            transientProcess.Kill(entireProcessTree: true);
                            await transientProcess.WaitForExitAsync();
                        }
                        transientProcess.Dispose();
                    }
                    catch { }
                }

                if (tempConfigFile != null)
                {
                    try { if (File.Exists(tempConfigFile)) File.Delete(tempConfigFile); } catch { }
                }
            }
        }

        private async Task<(bool success, int latencyMs, string ip, string country, string city, string org, string message)> ExecuteHttpGetCheckAsync(
            Uri proxyUri,
            VpnProfile profile,
            string? username,
            string? password,
            CancellationToken ct)
        {
            var webProxy = new WebProxy(proxyUri);
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
            {
                webProxy.Credentials = new NetworkCredential(username ?? "", password ?? "");
            }

            var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(6)
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            var sw = Stopwatch.StartNew();
            string ip = "";
            string country = "";
            string countryCode = "";
            string city = "";
            string org = "";

            // Tier 1: ipwho.is - rich metadata
            try
            {
                using var response = await client.GetAsync("https://ipwho.is/", ct);
                sw.Stop();

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(responseBody);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("success", out var succ) && succ.GetBoolean())
                    {
                        if (root.TryGetProperty("ip", out var ipProp)) ip = ipProp.GetString() ?? "";
                        if (root.TryGetProperty("country", out var cProp)) country = cProp.GetString() ?? "";
                        if (root.TryGetProperty("country_code", out var ccProp)) countryCode = ccProp.GetString() ?? "";
                        if (root.TryGetProperty("city", out var cityProp)) city = cityProp.GetString() ?? "";
                        if (root.TryGetProperty("connection", out var conn) && conn.TryGetProperty("org", out var orgProp))
                        {
                            org = orgProp.GetString() ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[VPN-Verify] ipwho.is не ответил через прокси ({ex.Message}), опрос резервного сервиса Cloudflare...");
            }

            // Tier 2: Cloudflare CDN-CGI trace - unblockable, ultrafast, zero rate-limit
            if (string.IsNullOrEmpty(ip))
            {
                try
                {
                    sw.Restart();
                    using var cfResponse = await client.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", ct);
                    sw.Stop();
                    if (cfResponse.IsSuccessStatusCode)
                    {
                        string traceText = await cfResponse.Content.ReadAsStringAsync(ct);
                        foreach (var line in traceText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (line.StartsWith("ip=")) ip = line.Substring(3).Trim();
                            if (line.StartsWith("loc=")) countryCode = line.Substring(4).Trim();
                        }
                        if (!string.IsNullOrEmpty(countryCode) && string.IsNullOrEmpty(country))
                        {
                            country = VpnProfile.GetCountryName(countryCode);
                        }
                    }
                }
                catch { }
            }

            // Tier 3: ipapi.co
            if (string.IsNullOrEmpty(ip))
            {
                try
                {
                    sw.Restart();
                    using var apiResponse = await client.GetAsync("https://ipapi.co/json/", ct);
                    sw.Stop();
                    if (apiResponse.IsSuccessStatusCode)
                    {
                        string apiJson = await apiResponse.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(apiJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ip", out var ipProp)) ip = ipProp.GetString() ?? "";
                        if (root.TryGetProperty("country_name", out var cProp)) country = cProp.GetString() ?? "";
                        if (root.TryGetProperty("country_code", out var ccProp)) countryCode = ccProp.GetString() ?? "";
                        if (root.TryGetProperty("city", out var cityProp)) city = cityProp.GetString() ?? "";
                        if (root.TryGetProperty("org", out var orgProp)) org = orgProp.GetString() ?? "";
                    }
                }
                catch { }
            }

            // Tier 4: api.ipify.org
            if (string.IsNullOrEmpty(ip))
            {
                try
                {
                    sw.Restart();
                    using var fbResponse = await client.GetAsync("https://api.ipify.org?format=json", ct);
                    sw.Stop();
                    if (fbResponse.IsSuccessStatusCode)
                    {
                        string fbJson = await fbResponse.Content.ReadAsStringAsync(ct);
                        using var doc = JsonDocument.Parse(fbJson);
                        if (doc.RootElement.TryGetProperty("ip", out var ipProp))
                        {
                            ip = ipProp.GetString() ?? "";
                            country = "Интернет";
                        }
                    }
                }
                catch { }
            }

            int latency = (int)sw.ElapsedMilliseconds;

            if (!string.IsNullOrEmpty(ip))
            {
                profile.HttpPingMs = latency;
                profile.ExitIp = ip;
                profile.ExitCountry = country;
                profile.ExitCountryCode = countryCode;
                profile.ExitCity = city;
                profile.ExitOrg = org;
                profile.HttpCheckStatus = "Доступен";

                string locSummary = !string.IsNullOrEmpty(country)
                    ? $"{VpnProfile.GetFlagEmoji(countryCode)} {country}{(string.IsNullOrEmpty(city) ? "" : $", {city}")}"
                    : ip;

                Log($"[VPN-Verify] ✅ Проверка узла '{profile.Name}' успешна! Пинг GET: {latency} ms | Выход: {locSummary} ({ip}) {(string.IsNullOrEmpty(org) ? "" : $"• {org}")}");
                return (true, latency, ip, country, city, org, "Успешно");
            }
            else
            {
                profile.HttpPingMs = -1;
                profile.HttpCheckStatus = "Таймаут / Ошибка";
                Log($"[VPN-Verify] ❌ Проверка узла '{profile.Name}' не удалась (прокси не пропускает HTTP GET трафик).");
                return (false, -1, "", "", "", "", "Таймаут или ошибка соединения через прокси");
            }
        }

        public async Task VerifyAllProfilesViaHttpGetAsync(IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (Profiles.Count == 0) return;

            Log($"[VPN-Verify] Запуск проверки через HTTP GET для {Profiles.Count} узлов...");
            int completed = 0;
            using var semaphore = new SemaphoreSlim(2);

            var tasks = Profiles.Select(async p =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    await VerifyProxyViaHttpGetAsync(p, ct);
                }
                finally
                {
                    semaphore.Release();
                    int count = Interlocked.Increment(ref completed);
                    progress?.Report(count);
                }
            });

            await Task.WhenAll(tasks);
            Log("[VPN-Verify] Проверка через HTTP GET завершена для всех узлов.");
        }

        #endregion

        #region System Proxy & Profile Export

        public static bool SetWindowsSystemProxy(bool enable, string proxyServer)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (key == null) return false;

                if (enable && !string.IsNullOrWhiteSpace(proxyServer))
                {
                    key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                    key.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;192.168.*", RegistryValueKind.String);
                }
                else
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                }

                // Notify Windows subsystems and browsers of proxy change
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CleanupOnAppExit()
        {
            try
            {
                SetWindowsSystemProxy(false, "");
                StopOrphanCoreProcesses();
            }
            catch { }
        }

        public static string ExportProfileConfigJson(VpnProfile profile)
        {
            // Build outbound depending on protocol
            object outboundObj = profile.Protocol switch
            {
                VpnProtocol.Vless => new
                {
                    type = "vless",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    uuid = profile.Uuid,
                    flow = string.IsNullOrEmpty(profile.Flow) ? null : profile.Flow,
                    tls = new
                    {
                        enabled = profile.Security != "none",
                        server_name = string.IsNullOrEmpty(profile.Sni) ? profile.Server : profile.Sni,
                        reality = profile.Security == "reality" ? new
                        {
                            enabled = true,
                            public_key = profile.PublicKey,
                            short_id = profile.ShortId
                        } : null
                    }
                },
                VpnProtocol.Vmess => new
                {
                    type = "vmess",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    uuid = profile.Uuid,
                    security = "auto",
                    tls = new
                    {
                        enabled = profile.Security == "tls",
                        server_name = string.IsNullOrEmpty(profile.Sni) ? profile.Server : profile.Sni
                    },
                    transport = !string.IsNullOrEmpty(profile.Path) ? new
                    {
                        type = profile.NetworkType,
                        path = profile.Path
                    } : null
                },
                VpnProtocol.Shadowsocks => new
                {
                    type = "shadowsocks",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    method = profile.Method,
                    password = profile.Password
                },
                VpnProtocol.Trojan => new
                {
                    type = "trojan",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    password = profile.Password,
                    tls = new
                    {
                        enabled = true,
                        server_name = string.IsNullOrEmpty(profile.Sni) ? profile.Server : profile.Sni
                    }
                },
                VpnProtocol.WireGuard => new
                {
                    type = "wireguard",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    system_interface = false,
                    private_key = profile.Password,
                    peer_public_key = profile.PublicKey
                },
                VpnProtocol.Socks5 => new
                {
                    type = "socks",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    username = profile.Uuid,
                    password = profile.Password
                },
                _ => new
                {
                    type = "http",
                    tag = "proxy",
                    server = profile.Server,
                    server_port = profile.Port,
                    username = profile.Uuid,
                    password = profile.Password
                }
            };

            var config = new
            {
                client = "ZapretGUI-VPN-Beta",
                version = "1.10.2",
                profile = new
                {
                    name = profile.Name,
                    protocol = profile.Protocol.ToString().ToLowerInvariant(),
                    server = profile.Server,
                    port = profile.Port,
                    uuid = profile.Uuid,
                    password = profile.Password,
                    security = profile.Security,
                    sni = profile.Sni,
                    network = profile.NetworkType,
                    path = profile.Path,
                    publicKey = profile.PublicKey,
                    shortId = profile.ShortId,
                    flow = profile.Flow,
                    rawLink = profile.RawLink
                },
                inbounds = new object[]
                {
                    new
                    {
                        type = "mixed",
                        tag = "mixed-in",
                        listen = "127.0.0.1",
                        listen_port = 10808,
                        sniff = true
                    }
                },
                outbounds = new object[]
                {
                    outboundObj,
                    new
                    {
                        type = "direct",
                        tag = "direct"
                    }
                },
                outbound = outboundObj // Backwards compatibility with test suite
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        }

        #endregion

        #region Profile Storage

        public void LoadProfiles()
        {
            try
            {
                Profiles.Clear();
                if (File.Exists(_profilesFilePath))
                {
                    string json = File.ReadAllText(_profilesFilePath, Encoding.UTF8);
                    var items = JsonSerializer.Deserialize<List<VpnProfile>>(json);
                    if (items != null)
                    {
                        foreach (var item in items)
                        {
                            Profiles.Add(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[VPN-Beta] Ошибка чтения vpn_profiles.json: {ex.Message}");
            }
        }

        public void SaveProfiles()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(Profiles.ToList(), options);
                File.WriteAllText(_profilesFilePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log($"[VPN-Beta] Ошибка сохранения vpn_profiles.json: {ex.Message}");
            }
        }

        public void AddProfiles(IEnumerable<VpnProfile> newProfiles)
        {
            int added = 0;
            foreach (var p in newProfiles)
            {
                if (!Profiles.Any(existing => existing.RawLink == p.RawLink && existing.Server == p.Server && existing.Port == p.Port))
                {
                    Profiles.Add(p);
                    added++;
                }
            }
            if (added > 0)
            {
                SaveProfiles();
                Log($"[VPN-Beta] Добавлено узлов: {added}. Всего в списке: {Profiles.Count}");
            }
        }

        public void RemoveProfile(VpnProfile profile)
        {
            if (Profiles.Remove(profile))
            {
                if (ActiveProfile == profile)
                {
                    _ = DisconnectAsync();
                }
                SaveProfiles();
                Log($"[VPN-Beta] Удален узел: {profile.Name}");
            }
        }

        public void ClearProfiles()
        {
            _ = DisconnectAsync();
            Profiles.Clear();
            SaveProfiles();
            Log("[VPN-Beta] Список узлов VPN очищен.");
        }

        #endregion
    }
}
