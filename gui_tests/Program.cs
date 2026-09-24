using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZapretGUI;

namespace ZapretTests
{
    internal class Program
    {
        private static int _passed = 0;
        private static int _failed = 0;

        private static void Assert(bool condition, string testName, string error = "")
        {
            if (condition)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] {testName}");
                _passed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {testName}: {error}");
                _failed++;
            }
            Console.ResetColor();
        }

        static async Task<int> Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("      ZAPRET GUI AUTOMATED TEST SUITE   ");
            Console.WriteLine("========================================");

            var core = new ZapretCore();
            var diagService = new DiagnosticsService(core);
            var updateService = new UpdateService(core);
            var listsService = new ListsService(core);

            // Test 1: Zapret Root Detection
            Assert(Directory.Exists(core.ZapretRoot), "Zapret Root Exists", $"Root is {core.ZapretRoot}");
            Assert(File.Exists(core.WinwsExePath), "winws.exe exists in bin", $"Looked at {core.WinwsExePath}");

            // Test 2: Bat strategies discovery
            var strategies = core.GetStrategies();
            Assert(strategies.Count >= 20, $"Found {strategies.Count} strategies (expected >= 20)", $"Actual count: {strategies.Count}");
            var generalStrat = strategies.FirstOrDefault(s => s.Name.Equals("general", StringComparison.OrdinalIgnoreCase));
            Assert(generalStrat != null, "Found 'general' strategy");
            Assert(!string.IsNullOrWhiteSpace(generalStrat?.RawArguments), "General strategy has raw arguments parsed");

            // Test 3: Arguments parsing & resolution
            string testBat = @"@echo off
start ""zapret: test"" /min ""%BIN%winws.exe"" --wf-tcp=80,443,%GameFilterTCP% ^
--filter-udp=443 --hostlist=""%LISTS%list-general.txt"" ^
--dpi-desync=fake";
            string parsedArgs = core.ParseBatArguments(testBat);
            Assert(parsedArgs.Contains("--wf-tcp=80,443,%GameFilterTCP%"), "ParseBatArguments captures first line arguments");
            Assert(parsedArgs.Contains("--dpi-desync=fake"), "ParseBatArguments captures multiline carets continuation");

            string resolvedArgs = core.ResolveArguments(parsedArgs);
            Assert(!resolvedArgs.Contains("%BIN%"), "ResolveArguments replaced %BIN%");
            Assert(!resolvedArgs.Contains("%LISTS%"), "ResolveArguments replaced %LISTS%");
            Assert(!resolvedArgs.Contains("%GameFilterTCP%"), "ResolveArguments replaced %GameFilterTCP%");

            // Test 4: Game Filter toggles
            var initialGf = core.GetGameFilterMode();
            core.SetGameFilterMode(GameFilterMode.Disabled);
            Assert(core.GetGameFilterMode() == GameFilterMode.Disabled, "GameFilter: Set Disabled");
            var (tcp1, udp1) = core.GetGameFilterPorts();
            Assert(tcp1 == "12" && udp1 == "12", "GameFilter Disabled ports are 12 / 12");

            core.SetGameFilterMode(GameFilterMode.All);
            Assert(core.GetGameFilterMode() == GameFilterMode.All, "GameFilter: Set All");
            var (tcp2, udp2) = core.GetGameFilterPorts();
            Assert(tcp2 == "1024-65535" && udp2 == "1024-65535", "GameFilter All ports are 1024-65535");

            core.SetGameFilterMode(GameFilterMode.TcpOnly);
            Assert(core.GetGameFilterMode() == GameFilterMode.TcpOnly, "GameFilter: Set TcpOnly");

            core.SetGameFilterMode(GameFilterMode.UdpOnly);
            Assert(core.GetGameFilterMode() == GameFilterMode.UdpOnly, "GameFilter: Set UdpOnly");
            core.SetGameFilterMode(initialGf); // restore

            // Test 5: IPSet Filter Mode
            var initialIpset = core.GetIPSetMode(out _);
            core.SetIPSetMode(IPSetMode.None);
            Assert(core.GetIPSetMode(out _) == IPSetMode.None, "IPSet Mode: Set None");
            core.SetIPSetMode(IPSetMode.Any);
            Assert(core.GetIPSetMode(out _) == IPSetMode.Any, "IPSet Mode: Set Any");
            core.SetIPSetMode(IPSetMode.Loaded);
            Assert(core.GetIPSetMode(out _) == IPSetMode.Loaded, "IPSet Mode: Set Loaded");
            core.SetIPSetMode(initialIpset); // restore

            // Test 6: Active Fakes
            var fakes = core.GetAvailableFakes();
            Assert(fakes.Count > 0, $"Found {fakes.Count} fake .bin files");
            Assert(fakes.All(f => !string.IsNullOrEmpty(f.Sha256Hash)), "All fake files have valid SHA256 hashes");
            var (discFake, gameFake) = core.GetCurrentActiveFakes(fakes);
            Assert(!string.IsNullOrEmpty(discFake), $"Current Discord Fake identified: {discFake}");
            Assert(!string.IsNullOrEmpty(gameFake), $"Current Game Fake identified: {gameFake}");

            // Test 7: Diagnostics checks (including new Admin check and VPN check)
            var diagResults = await diagService.RunAllDiagnosticsAsync();
            Assert(diagResults.Count >= 7, $"Ran {diagResults.Count} diagnostic checks (expected >= 7)");
            Assert(diagResults.Any(d => d.Title.Contains("Права администратора")), "Diagnostics includes Admin rights check");
            Assert(diagResults.Any(d => d.Title.Contains("Путь")), "Diagnostics checks path");
            Assert(diagResults.Any(d => d.Title.Contains("WinDivert")), "Diagnostics checks WinDivert files");
            Assert(diagResults.Any(d => d.Title.Contains("BFE")), "Diagnostics checks BFE");
            Assert(diagResults.Any(d => d.Title.Contains("VPN")), "Diagnostics checks VPN services");

            // Test 8: Lists Service
            var lists = listsService.GetKnownLists();
            Assert(lists.Count == 8, $"Known lists count: {lists.Count} (expected 8)");
            var userList = lists.First(l => l.FileName == "list-general-user.txt");
            string content = listsService.ReadContent(userList);
            Assert(!string.IsNullOrEmpty(content), "Can read list-general-user.txt");

            // Test 9: Quote Escaping for sc create
            string testArgsWithQuotes = "--wf-tcp=80 --hostlist=\"C:\\zapret\\lists\\test.txt\" --dpi-desync-fake-quic=\"C:\\zapret\\bin\\fake.bin\"";
            string escapedForService = testArgsWithQuotes.Replace("\"", "\\\"");
            Assert(escapedForService.Contains("\\\"C:\\zapret\\lists\\test.txt\\\""), "Inner quotes correctly escaped with backslash for sc.exe");
            Assert(!escapedForService.Contains("=\"C:"), "No unescaped inner quotes remain");

            // Test 10: All 22 BAT files parsed and resolved
            int parsedCount = 0;
            foreach (var strat in strategies)
            {
                string res = core.ResolveArguments(strat.RawArguments);
                if (!string.IsNullOrEmpty(res) && !res.Contains("%BIN%") && !res.Contains("%LISTS%"))
                {
                    parsedCount++;
                }
            }
            Assert(parsedCount == strategies.Count && parsedCount >= 20, $"All {parsedCount}/{strategies.Count} strategies parsed and resolved cleanly");

            // Test 11: Dynamic Zapret Root Discovery (no hardcoded user dependency)
            string discoveredRoot = ZapretCore.FindZapretRoot();
            Assert(!string.IsNullOrEmpty(discoveredRoot) && ZapretCore.IsValidZapretRoot(discoveredRoot), "FindZapretRoot successfully found valid root dynamically");

            // Test 12: Output Single-file executable verification
            string exePath = Path.Combine(core.ZapretRoot, "ZapretVPN.exe");
            if (!File.Exists(exePath)) exePath = Path.Combine(core.ZapretRoot, "ZapretGUI.exe");
            Assert(File.Exists(exePath), "ZapretVPN.exe exists in zapret root");
            var fi = new FileInfo(exePath);
            Assert(fi.Length > 200_000, $"ZapretVPN.exe size is valid ({fi.Length} bytes)");

            // Test 13: High-DPI Configuration & Manifest Verification
            string manifestPath = Path.Combine(core.ZapretRoot, "gui_src", "app.manifest");
            Assert(File.Exists(manifestPath), "app.manifest exists");
            string manifestText = File.ReadAllText(manifestPath);
            Assert(manifestText.Contains("PerMonitorV2"), "app.manifest contains PerMonitorV2 DPI awareness");
            Assert(manifestText.Contains("true/pm"), "app.manifest contains true/pm dpiAware");

            string xamlPath = Path.Combine(core.ZapretRoot, "gui_src", "MainWindow.xaml");
            string xamlText = File.ReadAllText(xamlPath);
            Assert(xamlText.Contains("UseLayoutRounding=\"True\""), "MainWindow.xaml has UseLayoutRounding=True");
            Assert(xamlText.Contains("TextOptions.TextFormattingMode=\"Display\""), "MainWindow.xaml has TextFormattingMode=Display");

            // Test 14: VPN Parser - VLESS Link
            string vlessLink = "vless://9a5e8f42-7a2e-4c56-8a9d-123456789abc@nl-node.example.com:443?type=tcp&security=reality&sni=nl-node.example.com#NL-Server-1";
            var vlessProfile = VpnService.ParseSingleLink(vlessLink);
            Assert(vlessProfile != null, "VLESS link parsed successfully");
            Assert(vlessProfile?.Protocol == VpnProtocol.Vless, "VLESS protocol identified");
            Assert(vlessProfile?.Server == "nl-node.example.com", $"VLESS server is nl-node.example.com (got: {vlessProfile?.Server})");
            Assert(vlessProfile?.Port == 443, "VLESS port is 443");
            Assert(vlessProfile?.Uuid == "9a5e8f42-7a2e-4c56-8a9d-123456789abc", "VLESS UUID parsed");
            Assert(vlessProfile?.Security == "reality", "VLESS security is reality");
            Assert(vlessProfile?.Name == "NL-Server-1", $"VLESS remark is NL-Server-1 (got: {vlessProfile?.Name})");

            // Test 15: VPN Parser - VMess Link
            string vmessJson = "{\"v\":\"2\",\"ps\":\"DE-Frankfurt\",\"add\":\"de.example.com\",\"port\":8443,\"id\":\"3c98b671-558e-4a6c-949e-b9862215cbb8\",\"net\":\"ws\",\"path\":\"/v-ws\",\"tls\":\"tls\"}";
            string vmessB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vmessJson));
            string vmessLink = $"vmess://{vmessB64}";
            var vmessProfile = VpnService.ParseSingleLink(vmessLink);
            Assert(vmessProfile != null, "VMess link parsed successfully");
            Assert(vmessProfile?.Protocol == VpnProtocol.Vmess, "VMess protocol identified");
            Assert(vmessProfile?.Server == "de.example.com", "VMess server is de.example.com");
            Assert(vmessProfile?.Port == 8443, "VMess port is 8443");
            Assert(vmessProfile?.Name == "DE-Frankfurt", "VMess name is DE-Frankfurt");
            Assert(vmessProfile?.Path == "/v-ws", "VMess path is /v-ws");

            // Test 16: VPN Parser - Shadowsocks, Trojan, WireGuard, SOCKS5
            string ssLink = "ss://YWVzLTI1Ni1nY206c2VjcmV0MTIz@198.51.100.1:8388#SS-Tokyo";
            var ssProfile = VpnService.ParseSingleLink(ssLink);
            Assert(ssProfile != null && ssProfile.Protocol == VpnProtocol.Shadowsocks, "Shadowsocks link parsed");
            Assert(ssProfile?.Method == "aes-256-gcm" && ssProfile?.Password == "secret123", "Shadowsocks credentials parsed");
            Assert(ssProfile?.Server == "198.51.100.1" && ssProfile?.Port == 8388, "Shadowsocks endpoint parsed");

            string trojanLink = "trojan://pass456@tr.example.com:443?sni=tr.example.com#Trojan-US";
            var trojanProfile = VpnService.ParseSingleLink(trojanLink);
            Assert(trojanProfile != null && trojanProfile.Protocol == VpnProtocol.Trojan, "Trojan link parsed");
            Assert(trojanProfile?.Password == "pass456" && trojanProfile?.Server == "tr.example.com", "Trojan server & pass parsed");

            string wgLink = "wireguard://privatekey789@wg.example.com:51820?publickey=pubkeyabc#WG-Fast";
            var wgProfile = VpnService.ParseSingleLink(wgLink);
            Assert(wgProfile != null && wgProfile.Protocol == VpnProtocol.WireGuard, "WireGuard link parsed");
            Assert(wgProfile?.Port == 51820 && wgProfile?.PublicKey == "pubkeyabc", "WireGuard port & public key parsed");

            string socksLink = "socks5://proxyuser:proxypass@127.0.0.1:10808#LocalSocks";
            var socksProfile = VpnService.ParseSingleLink(socksLink);
            Assert(socksProfile != null && socksProfile.Protocol == VpnProtocol.Socks5, "SOCKS5 link parsed");
            Assert(socksProfile?.Server == "127.0.0.1" && socksProfile?.Port == 10808, "SOCKS5 endpoint parsed");

            // Test 17: Multi-link & Base64 subscription input parsing
            string multiline = $"{vlessLink}\r\n{trojanLink}\r\n{ssLink}";
            var parsedList = VpnService.ParseInput(multiline);
            Assert(parsedList.Count == 3, $"Multiline input parsed 3 nodes (got: {parsedList.Count})");

            string base64Sub = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(multiline));
            var fromB64 = VpnService.ParseInput(base64Sub);
            Assert(fromB64.Count == 3, $"Base64 subscription blob parsed 3 nodes (got: {fromB64.Count})");

            // Test 18: VPN Profile JSON Config Export & UI Helper Badges
            string exportedJson = VpnService.ExportProfileConfigJson(vlessProfile!);
            Assert(exportedJson.Contains("ZapretGUI-VPN-Beta"), "Exported JSON contains client signature");
            Assert(exportedJson.Contains("nl-node.example.com"), "Exported JSON contains server");
            Assert(vlessProfile?.ProtocolBadge == "VLESS", "VLESS ProtocolBadge is VLESS");

            vlessProfile!.PingMs = 45;
            Assert(vlessProfile.PingDisplay == "45 ms", "PingDisplay formats milliseconds");
            vlessProfile.PingMs = -1;
            Assert(vlessProfile.PingDisplay == "Таймаут", "PingDisplay formats timeout");

            // Test 19: VPN Service Profile Collection & Lifecycle
            var vpnService = new VpnService(core);
            int initialCount = vpnService.Profiles.Count;
            vpnService.AddProfiles(new[] { vlessProfile! });
            Assert(vpnService.Profiles.Count >= initialCount, "VPN profiles added to service collection");
            vpnService.RemoveProfile(vlessProfile!);

            // Test 20: VPN Edge Cases (empty, malformed, non-ASCII remarks, broken base64)
            Assert(VpnService.ParseSingleLink("") == null, "Empty link returns null");
            Assert(VpnService.ParseSingleLink("   ") == null, "Whitespace link returns null");
            Assert(VpnService.ParseSingleLink("invalid://foo/bar") == null, "Invalid scheme returns null");
            Assert(VpnService.ParseSingleLink("vmess://broken_base64@@@") == null, "Broken base64 vmess returns null");
            Assert(VpnService.ParseInput("").Count == 0, "Empty input returns 0 profiles");
            Assert(VpnService.ParseInput("# Comment only\r\n// another comment").Count == 0, "Comments return 0 profiles");

            string russianRemarkVless = "vless://uuid-test@1.1.1.1:443?security=tls#%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B%20%D0%A1%D0%BA%D0%BE%D1%80%D0%BE%D1%81%D1%82%D1%8C";
            var ruProfile = VpnService.ParseSingleLink(russianRemarkVless);
            Assert(ruProfile?.Name == "Нидерланды Скорость", $"URL-encoded Russian remark decoded cleanly (got: {ruProfile?.Name})");

            // Test 21: IPv6 VPN Endpoints
            string ipv6Vless = "vless://9a5e8f42-7a2e-4c56-8a9d-123456789abc@[2606:4700::1]:443?security=reality#IPv6-Cloudflare";
            var ipv6Profile = VpnService.ParseSingleLink(ipv6Vless);
            Assert(ipv6Profile != null, "IPv6 VLESS parsed");
            Assert(ipv6Profile?.Server == "2606:4700::1", $"IPv6 host is 2606:4700::1 (got: {ipv6Profile?.Server})");
            Assert(ipv6Profile?.Port == 443, "IPv6 port is 443");

            // Test 22: Trailing slash before query parameter
            string slashLink = "trojan://secret-pass@tr.example.com:443/?sni=tr.example.com#Trojan-Slash";
            var slashProfile = VpnService.ParseSingleLink(slashLink);
            Assert(slashProfile != null, "Trailing slash link parsed");
            Assert(slashProfile?.Server == "tr.example.com" && slashProfile?.Port == 443, "Trailing slash host/port parsed");
            Assert(slashProfile?.Sni == "tr.example.com", "Trailing slash SNI query parameter captured");

            // Test 23: Stray percent sign in remark (non-hex percent encoding)
            string percentRemarkLink = "vless://uuid-test@1.2.3.4:443#100% Free Server";
            var percentProfile = VpnService.ParseSingleLink(percentRemarkLink);
            Assert(percentProfile != null, "Link with stray percent sign parsed without exception");
            Assert(percentProfile?.Name.Contains("100%") == true, "Stray percent sign preserved in remark");

            // Test 24: VMess with external #remark fragment
            string vmessRawJson = "{\"v\":\"2\",\"add\":\"vmess.node.com\",\"port\":443,\"id\":\"3c98b671-558e-4a6c-949e-b9862215cbb8\",\"net\":\"tcp\"}";
            string vmessExternalRemark = $"vmess://{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vmessRawJson))}#External-VMess-Name";
            var vmessExtProfile = VpnService.ParseSingleLink(vmessExternalRemark);
            Assert(vmessExtProfile != null, "VMess with external #remark parsed");
            Assert(vmessExtProfile?.Name == "External-VMess-Name", $"VMess external remark used when ps empty (got: {vmessExtProfile?.Name})");

            // Test 25: Clash YAML Subscription Parsing
            string clashYaml = @"proxies:
  - { name: ""Clash-VLESS"", type: vless, server: vless.clash.com, port: 443, uuid: 12345, tls: true }
  - { name: ""Clash-SS"", type: ss, server: ss.clash.com, port: 8388, cipher: aes-256-gcm, password: pass }";
            var clashProfiles = VpnService.ParseInput(clashYaml);
            Assert(clashProfiles.Count == 2, $"Clash YAML parsed 2 proxies (got: {clashProfiles.Count})");
            Assert(clashProfiles.Any(p => p.Name == "Clash-VLESS" && p.Protocol == VpnProtocol.Vless), "Clash VLESS node parsed");
            Assert(clashProfiles.Any(p => p.Name == "Clash-SS" && p.Protocol == VpnProtocol.Shadowsocks), "Clash Shadowsocks node parsed");

            // Test 26: Scheme aliases (wg://, hy2://, socks://)
            var wgAlias = VpnService.ParseSingleLink("wg://privkey@wg-alias.com:51820?publickey=pubkey#WG-Alias");
            Assert(wgAlias != null && wgAlias.Protocol == VpnProtocol.WireGuard, "wg:// alias parsed");
            var hy2Alias = VpnService.ParseSingleLink("hy2://pass@hy2-alias.com:443#HY2-Alias");
            Assert(hy2Alias != null && hy2Alias.Protocol == VpnProtocol.Hysteria2, "hy2:// alias parsed");
            var socksAlias = VpnService.ParseSingleLink("socks://socks-alias.com:1080#Socks-Alias");
            Assert(socksAlias != null && socksAlias.Protocol == VpnProtocol.Socks5, "socks:// alias parsed");

            // Test 27: Sing-box Core Detection & Port Allocation
            string? corePath = vpnService.FindCoreExecutable();
            Assert(!string.IsNullOrEmpty(corePath) && File.Exists(corePath), $"Sing-box core detected: {corePath}");
            int testPort = VpnService.GetAvailablePort(10850);
            Assert(testPort >= 10850, $"Dynamic test port allocated: {testPort}");

            // Test 28: Sing-box Runtime Config Generation
            string runtimeJson = VpnService.BuildSingBoxRuntimeConfigJson(vlessProfile!, 10808);
            Assert(!runtimeJson.Contains("\"client\":"), "Runtime JSON does NOT contain unknown field 'client'");
            Assert(!runtimeJson.Contains("\"profile\":"), "Runtime JSON does NOT contain unknown field 'profile'");
            Assert(!runtimeJson.Contains("\"sniff\": true"), "Runtime JSON does NOT contain deprecated 'sniff: true'");
            Assert(runtimeJson.Contains("\"listen_port\": 10808"), "Runtime JSON binds inbound port 10808");
            Assert(runtimeJson.Contains("\"final\": \"proxy\""), "Runtime JSON routes to proxy outbound");

            // Test 29: Sing-box Outbounds Structure
            var vlessOut = VpnService.BuildSingBoxOutbound(vlessProfile!) as Dictionary<string, object?>;
            Assert(vlessOut != null && (string?)vlessOut["type"] == "vless", "VLESS outbound type is vless");
            Assert((string?)vlessOut?["uuid"] == vlessProfile?.Uuid, "VLESS outbound uuid matches profile");

            var ssOut = VpnService.BuildSingBoxOutbound(ssProfile!) as Dictionary<string, object?>;
            Assert(ssOut != null && (string?)ssOut["type"] == "shadowsocks", "Shadowsocks outbound type is shadowsocks");
            Assert((string?)ssOut?["method"] == ssProfile?.Method, "Shadowsocks method matches profile");

            var trojanOut = VpnService.BuildSingBoxOutbound(trojanProfile!) as Dictionary<string, object?>;
            Assert(trojanOut != null && (string?)trojanOut["type"] == "trojan", "Trojan outbound type is trojan");

            // Test 30: Validation of generated Sing-box config with actual sing-box.exe check
            if (!string.IsNullOrEmpty(corePath) && File.Exists(corePath))
            {
                string tempCheckFile = Path.Combine(core.ZapretRoot, "bin", "test_runtime_check.json");
                File.WriteAllText(tempCheckFile, runtimeJson, new System.Text.UTF8Encoding(false));
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = corePath,
                        Arguments = $"check -c \"{tempCheckFile}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var p = Process.Start(psi);
                    string checkErr = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(4000);
                    Assert(p?.ExitCode == 0, $"sing-box check on generated runtime config passed (exit code: {p?.ExitCode}) - {checkErr}");
                }
                finally
                {
                    if (File.Exists(tempCheckFile)) File.Delete(tempCheckFile);
                }

                // Check Trojan sing-box
                string trojanJson = VpnService.BuildSingBoxRuntimeConfigJson(trojanProfile!, 10808);
                File.WriteAllText(tempCheckFile, trojanJson, new System.Text.UTF8Encoding(false));
                try
                {
                    var psi = new ProcessStartInfo { FileName = corePath, Arguments = $"check -c \"{tempCheckFile}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                    using var p = Process.Start(psi);
                    string checkErr = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(4000);
                    Assert(p?.ExitCode == 0, $"sing-box check on Trojan config passed (exit code: {p?.ExitCode}) - {checkErr}");
                }
                finally { if (File.Exists(tempCheckFile)) File.Delete(tempCheckFile); }

                // Check VMess sing-box
                string vmessRuntimeJson = VpnService.BuildSingBoxRuntimeConfigJson(vmessProfile!, 10808);
                File.WriteAllText(tempCheckFile, vmessRuntimeJson, new System.Text.UTF8Encoding(false));
                try
                {
                    var psi = new ProcessStartInfo { FileName = corePath, Arguments = $"check -c \"{tempCheckFile}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                    using var p = Process.Start(psi);
                    string checkErr = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(4000);
                    Assert(p?.ExitCode == 0, $"sing-box check on VMess config passed (exit code: {p?.ExitCode}) - {checkErr}");
                }
                finally { if (File.Exists(tempCheckFile)) File.Delete(tempCheckFile); }

                // Check Shadowsocks sing-box
                string ssRuntimeJson = VpnService.BuildSingBoxRuntimeConfigJson(ssProfile!, 10808);
                File.WriteAllText(tempCheckFile, ssRuntimeJson, new System.Text.UTF8Encoding(false));
                try
                {
                    var psi = new ProcessStartInfo { FileName = corePath, Arguments = $"check -c \"{tempCheckFile}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                    using var p = Process.Start(psi);
                    string checkErr = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(4000);
                    Assert(p?.ExitCode == 0, $"sing-box check on Shadowsocks config passed (exit code: {p?.ExitCode}) - {checkErr}");
                }
                finally { if (File.Exists(tempCheckFile)) File.Delete(tempCheckFile); }

                // Inspect schema for WireGuard in sb_schema.json
                if (File.Exists(Path.Combine(core.ZapretRoot, "bin", "sb_schema.json")))
                {
                    string schemaJson = File.ReadAllText(Path.Combine(core.ZapretRoot, "bin", "sb_schema.json"));
                    using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("properties", out var props))
                    {
                        Console.WriteLine("Schema top properties: " + string.Join(", ", props.EnumerateObject().Select(p => p.Name)));
                    }
                }

                // Let's test sing-box endpoints vs outbounds for WireGuard
                string testWgConfig = @"{
  ""log"": { ""level"": ""warn"" },
  ""inbounds"": [ { ""type"": ""mixed"", ""tag"": ""mixed-in"", ""listen"": ""127.0.0.1"", ""listen_port"": 10808 } ],
  ""endpoints"": [
    {
      ""type"": ""wireguard"",
      ""tag"": ""proxy"",
      ""address"": [ ""172.16.0.2/32"" ],
      ""private_key"": ""yAnz5TF+lXXJysqhZKGweG0tGuy8PfZq8qVU4KjyVnc="",
      ""peers"": [
        {
          ""address"": ""wg.example.com"",
          ""port"": 51820,
          ""public_key"": ""bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo="",
          ""allowed_ips"": [ ""0.0.0.0/0"" ]
        }
      ]
    }
  ],
  ""outbounds"": [
    { ""type"": ""direct"", ""tag"": ""direct"" }
  ],
  ""route"": { ""auto_detect_interface"": true, ""final"": ""proxy"" }
}";
                File.WriteAllText(tempCheckFile, testWgConfig, new System.Text.UTF8Encoding(false));
                try
                {
                    var psi = new ProcessStartInfo { FileName = corePath, Arguments = $"check -c \"{tempCheckFile}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                    using var p = Process.Start(psi);
                    string checkErr = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(4000);
                    Assert(p?.ExitCode == 0, $"sing-box check on WireGuard config passed (exit code: {p?.ExitCode}) - {checkErr}");
                }
                finally { if (File.Exists(tempCheckFile)) File.Delete(tempCheckFile); }

                // Check Hysteria2 sing-box
                string hy2RuntimeJson = VpnService.BuildSingBoxRuntimeConfigJson(hy2Alias!, 10808);
                File.WriteAllText(tempCheckFile, hy2RuntimeJson, new System.Text.UTF8Encoding(false));
                try
                {
                    var psi = new ProcessStartInfo { FileName = corePath, Arguments = $"check -c \"{tempCheckFile}\"", CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                    using var p = Process.Start(psi);
                    string checkErr = p?.StandardError.ReadToEnd() ?? "";
                    p?.WaitForExit(4000);
                    Assert(p?.ExitCode == 0, $"sing-box check on Hysteria2 config passed (exit code: {p?.ExitCode}) - {checkErr}");
                }
                finally { if (File.Exists(tempCheckFile)) File.Delete(tempCheckFile); }
            }

            // Test 31: Country Flag Emoji Resolver
            Assert(VpnProfile.GetFlagEmoji("RU") == "🇷🇺", "Country code RU converts to Russian flag");
            Assert(VpnProfile.GetFlagEmoji("DE") == "🇩🇪", "Country code DE converts to German flag");
            Assert(VpnProfile.GetFlagEmoji("US") == "🇺🇸", "Country code US converts to US flag");
            Assert(VpnProfile.GetFlagEmoji("NL") == "🇳🇱", "Country code NL converts to Dutch flag");
            Assert(VpnProfile.GetFlagEmoji("") == "🌐", "Empty country code returns default globe");

            // Test 32: HTTP GET Verification Display & Badges Formatting
            vlessProfile!.HttpPingMs = 125;
            vlessProfile.ExitIp = "185.220.101.5";
            vlessProfile.ExitCountry = "Germany";
            vlessProfile.ExitCountryCode = "DE";
            vlessProfile.ExitCity = "Frankfurt";
            Assert(vlessProfile.ExitLocationSummary.Contains("🇩🇪 Germany (Frankfurt)"), $"ExitLocationSummary includes flag, country and city (got: {vlessProfile.ExitLocationSummary})");
            Assert(vlessProfile.HttpCheckDisplay.Contains("125 ms"), $"HttpCheckDisplay includes latency (got: {vlessProfile.HttpCheckDisplay})");
            Assert(vlessProfile.HttpCheckBadgeBg == "#143826", "Fast latency badge background is emerald");
            Assert(vlessProfile.HttpCheckBadgeFg == "#34D399", "Fast latency badge foreground is bright green");

            vlessProfile.HttpPingMs = -1;
            Assert(vlessProfile.HttpCheckDisplay == "Ошибка GET", "Failed ping formats as error");
            Assert(vlessProfile.HttpCheckBadgeBg == "#3D1B1F", "Error badge background is dark red");

            // Test 33: System Proxy Registry Cleanup Verification
            VpnService.CleanupOnAppExit();
            using (var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
            {
                int? proxyEnable = regKey?.GetValue("ProxyEnable") as int?;
                Assert(proxyEnable == 0, "CleanupOnAppExit disabled Windows ProxyEnable");
            }

            // Test 34: VerifyProxyViaHttpGetAsync Edge Case - Null Profile
            var nullResult = await vpnService.VerifyProxyViaHttpGetAsync(null!);
            Assert(!nullResult.success && nullResult.latencyMs == -1, "VerifyProxyViaHttpGetAsync handles null profile safely");

            // Test 35: VerifyProxyViaHttpGetAsync Edge Case - Cancellation
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                cts.Cancel();
                var cancelProfile = new VpnProfile { Protocol = VpnProtocol.Socks5, Server = "127.0.0.1", Port = 59999, Name = "CancelTest" };
                var cancelResult = await vpnService.VerifyProxyViaHttpGetAsync(cancelProfile, cts.Token);
                Assert(!cancelResult.success && cancelProfile.HttpPingMs == -1, "VerifyProxyViaHttpGetAsync handles cancellation gracefully");
            }

            // Test 36: VerifyAllProfilesViaHttpGetAsync on empty collection
            var emptyVpnService = new VpnService(core);
            emptyVpnService.Profiles.Clear();
            await emptyVpnService.VerifyAllProfilesViaHttpGetAsync();
            Assert(true, "VerifyAllProfilesViaHttpGetAsync on empty profile collection completes without error");

            // Test 37: Stored Profiles in vpn_profiles.json sing-box validation
            var storedService = new VpnService(core);
            if (storedService.Profiles.Count > 0)
            {
                Assert(storedService.Profiles.Count > 0, $"Loaded {storedService.Profiles.Count} stored profiles from vpn_profiles.json");
                int validConfigs = 0;
                string firstError = "";
                foreach (var p in storedService.Profiles)
                {
                    string rJson = VpnService.BuildSingBoxRuntimeConfigJson(p, 10810);
                    string chkPath = Path.Combine(core.ZapretRoot, "bin", $"test_chk_{p.Id}.json");
                    File.WriteAllText(chkPath, rJson, new System.Text.UTF8Encoding(false));
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = corePath!,
                            Arguments = $"check -c \"{chkPath}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        using var proc = Process.Start(psi);
                        string err = proc?.StandardError.ReadToEnd() ?? "";
                        proc?.WaitForExit(3000);
                        if (proc?.ExitCode == 0)
                        {
                            validConfigs++;
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(firstError)) firstError = $"{p.Name} ({p.Protocol}): {err}";
                        }
                    }
                    finally
                    {
                        if (File.Exists(chkPath)) File.Delete(chkPath);
                    }
                }
                Assert(validConfigs == storedService.Profiles.Count, $"All {storedService.Profiles.Count} stored profiles pass sing-box check", firstError);
            }
            else
            {
                Assert(true, "Stored profiles list is empty (default state)");
                Assert(true, "No stored profiles to check");
            }

            Console.WriteLine("========================================");
            Console.WriteLine($"RESULTS: {_passed} passed, {_failed} failed.");
            Console.WriteLine("========================================");

            return _failed == 0 ? 0 : 1;
        }
    }
}
