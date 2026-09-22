using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGUI
{
    public class DiagnosticsService
    {
        private readonly ZapretCore _core;

        public DiagnosticsService(ZapretCore core)
        {
            _core = core;
        }

        public async Task<List<DiagnosticItem>> RunAllDiagnosticsAsync()
        {
            return await Task.Run(() =>
            {
                var list = new List<DiagnosticItem>();

                // 0. Administrator rights check
                bool isAdmin = App.IsAdministrator();
                if (isAdmin)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Права администратора",
                        Description = "Приложение запущено с правами Администратора (UAC). Доступно управление службами и драйвером.",
                        Severity = DiagnosticSeverity.Pass
                    });
                }
                else
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Права администратора отсутствуют",
                        Description = "Приложение запущено без повышенных привилегий. Управление службой zapret и драйвером WinDivert требует прав администратора.",
                        Severity = DiagnosticSeverity.Warning,
                        FixActionName = "RestartAsAdmin"
                    });
                }

                // 1. Path check
                string zapretPath = _core.ZapretRoot;
                bool hasCyrillic = Regex.IsMatch(zapretPath, @"[\u0430-\u044F\u0410-\u042F\u0451\u0401]");
                string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
                bool inOneDrive = !string.IsNullOrEmpty(oneDrive) && zapretPath.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase);

                if (hasCyrillic)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Путь к Zapret содержит кириллицу",
                        Description = $"Путь '{zapretPath}' содержит русские буквы. Если WinDivert или zapret не запускаются, перенесите папку, например, в C:\\zapret.",
                        Severity = DiagnosticSeverity.Warning
                    });
                }
                else if (inOneDrive)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Zapret находится в папке OneDrive",
                        Description = "Расположение в облачной папке OneDrive может приводить к ошибкам доступа к файлам и блокировке драйвера.",
                        Severity = DiagnosticSeverity.Warning
                    });
                }
                else
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Путь установки Zapret",
                        Description = $"Корректный путь: '{zapretPath}'",
                        Severity = DiagnosticSeverity.Pass
                    });
                }

                // 2. WinDivert files check
                string sysPath = Path.Combine(_core.BinPath, "WinDivert64.sys");
                string dllPath = Path.Combine(_core.BinPath, "WinDivert.dll");
                string winwsPath = Path.Combine(_core.BinPath, "winws.exe");

                if (!File.Exists(sysPath) || !File.Exists(dllPath) || !File.Exists(winwsPath))
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Файлы WinDivert / winws.exe",
                        Description = "Отсутствуют ключевые файлы в папке bin! Проверьте, распакован ли архив и не заблокировал ли их антивирус.",
                        Severity = DiagnosticSeverity.Error
                    });
                }
                else
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Файлы WinDivert и winws.exe",
                        Description = "Файлы winws.exe, WinDivert64.sys и WinDivert.dll присутствуют.",
                        Severity = DiagnosticSeverity.Pass
                    });
                }

                // 3. Base Filtering Engine (BFE)
                var (bfeCode, bfeOut, _) = ZapretCore.RunCommandSync("sc.exe", "query BFE");
                if (bfeOut.Contains("RUNNING"))
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Служба BFE (Base Filtering Engine)",
                        Description = "Служба BFE работает нормально (необходима для сетевых фильтров).",
                        Severity = DiagnosticSeverity.Pass
                    });
                }
                else
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Служба BFE (Base Filtering Engine) не запущена",
                        Description = "Служба базовой фильтрации отключена! Без нее фильтрация трафика через WinDivert невозможна.",
                        Severity = DiagnosticSeverity.Error
                    });
                }

                // 4. System Proxy Check
                try
                {
                    using var reg = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                    int proxyEnabled = (int)(reg?.GetValue("ProxyEnable") ?? 0);
                    string? proxyServer = reg?.GetValue("ProxyServer")?.ToString();

                    if (proxyEnabled == 1)
                    {
                        list.Add(new DiagnosticItem
                        {
                            Title = "Системный прокси включен",
                            Description = $"В системе активен системный прокси: '{proxyServer}'. Убедитесь, что он настроен правильно, иначе трафик может идти мимо zapret.",
                            Severity = DiagnosticSeverity.Warning
                        });
                    }
                    else
                    {
                        list.Add(new DiagnosticItem
                        {
                            Title = "Системный прокси",
                            Description = "Системный прокси отключен (нормальный режим).",
                            Severity = DiagnosticSeverity.Pass
                        });
                    }
                }
                catch { }

                // 5. TCP Timestamps Check
                var (tcpCode, tcpOut, _) = ZapretCore.RunCommandSync("netsh.exe", "interface tcp show global");
                bool tcpOk = tcpOut.IndexOf("timestamps", StringComparison.OrdinalIgnoreCase) >= 0 &&
                             (tcpOut.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              tcpOut.IndexOf("включен", StringComparison.OrdinalIgnoreCase) >= 0);

                if (tcpOk)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "TCP Timestamps",
                        Description = "Временные метки TCP включены (рекомендуется для zapret).",
                        Severity = DiagnosticSeverity.Pass
                    });
                }
                else
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "TCP Timestamps отключены",
                        Description = "Для корректной работы некоторых алгоритмов zapret требуются включенные TCP timestamps.",
                        Severity = DiagnosticSeverity.Warning,
                        FixActionName = "EnableTimestamps"
                    });
                }

                // 6. Conflicting Process: Adguard
                var adguard = Process.GetProcessesByName("AdguardSvc");
                if (adguard.Length > 0)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Обнаружен сервис Adguard",
                        Description = "Служба AdguardSvc.exe может конфликтовать с захватом трафика Discord и фильтрацией zapret.",
                        Severity = DiagnosticSeverity.Warning
                    });
                }

                // 7. Conflicting Services (sc query)
                var (scCode, scOut, _) = ZapretCore.RunCommandSync("sc.exe", "query state= all");

                // Conflicting bypasses
                string[] bypasses = { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };
                var foundBypasses = bypasses.Where(b => scOut.Contains(b, StringComparison.OrdinalIgnoreCase)).ToList();

                if (foundBypasses.Count > 0)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Конфликтующие службы обхода блокировок",
                        Description = $"Найдены службы: {string.Join(", ", foundBypasses)}. Одновременная работа нескольких обходов приводит к конфликтам драйвера WinDivert!",
                        Severity = DiagnosticSeverity.Error,
                        FixActionName = "FixConflicts"
                    });
                }

                // Check other conflict software: Killer, Intel, Check Point, SmartByte
                var otherConflicts = new List<string>();
                if (scOut.Contains("Killer", StringComparison.OrdinalIgnoreCase)) otherConflicts.Add("Killer Network Service");
                if (scOut.Contains("TracSrvWrapper", StringComparison.OrdinalIgnoreCase) || scOut.Contains("EPWD", StringComparison.OrdinalIgnoreCase)) otherConflicts.Add("Check Point VPN");
                if (scOut.Contains("SmartByte", StringComparison.OrdinalIgnoreCase)) otherConflicts.Add("SmartByte Service");

                if (otherConflicts.Count > 0)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Обнаружены потенциально конфликтующие службы",
                        Description = $"Найдены службы сетевых оптимизаторов: {string.Join(", ", otherConflicts)}. Они могут перехватывать и сбрасывать сокеты WinDivert.",
                        Severity = DiagnosticSeverity.Warning
                    });
                }

                // VPN Services check (matches service.bat)
                if (scOut.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Обнаружены службы VPN",
                        Description = "В системе обнаружены сторонние службы VPN. Убедитесь, что VPN отключен, иначе трафик может идти мимо zapret.",
                        Severity = DiagnosticSeverity.Warning
                    });
                }
                else
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Службы VPN",
                        Description = "Конфликтующих служб VPN не обнаружено.",
                        Severity = DiagnosticSeverity.Pass
                    });
                }

                // 8. Orphaned WinDivert Check
                bool winwsRunning = Process.GetProcessesByName("winws").Length > 0;
                var (wdCode, wdOut, _) = ZapretCore.RunCommandSync("sc.exe", "query WinDivert");
                bool windivertActive = wdOut.Contains("RUNNING") || wdOut.Contains("STOP_PENDING");

                if (!winwsRunning && windivertActive)
                {
                    list.Add(new DiagnosticItem
                    {
                        Title = "Зависшая служба WinDivert",
                        Description = "winws.exe не запущен, однако драйвер WinDivert активен или завис в STOP_PENDING. Требуется очистка.",
                        Severity = DiagnosticSeverity.Warning,
                        FixActionName = "FixConflicts"
                    });
                }

                // 9. Hosts file check for youtube.com
                try
                {
                    string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                    if (File.Exists(hostsPath))
                    {
                        string hostsContent = File.ReadAllText(hostsPath);
                        if (hostsContent.Contains("youtube.com") || hostsContent.Contains("youtu.be"))
                        {
                            list.Add(new DiagnosticItem
                            {
                                Title = "Записи YouTube в файле hosts",
                                Description = "Файл hosts содержит статические записи для youtube.com или youtu.be. Это может препятствовать нормальной работе обхода.",
                                Severity = DiagnosticSeverity.Warning
                            });
                        }
                    }
                }
                catch { }

                // 10. DoH (Encrypted DNS) check
                try
                {
                    bool dohConfigured = false;
                    using var dnsKey = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
                    if (dnsKey != null)
                    {
                        foreach (var subName in dnsKey.GetSubKeyNames())
                        {
                            using var sub = dnsKey.OpenSubKey(subName);
                            object? val = sub?.GetValue("DohFlags");
                            if (val is int i && i > 0)
                            {
                                dohConfigured = true;
                                break;
                            }
                        }
                    }

                    if (dohConfigured)
                    {
                        list.Add(new DiagnosticItem
                        {
                            Title = "Зашифрованный DNS (DoH)",
                            Description = "Обнаружена настройка шифрованного DNS в Windows (DoH).",
                            Severity = DiagnosticSeverity.Pass
                        });
                    }
                    else
                    {
                        list.Add(new DiagnosticItem
                        {
                            Title = "Безопасный DNS (DoH)",
                            Description = "Рекомендуется включить Secure DNS (DoH) в браузере или параметрах сети Windows для надежного обхода DNS-блокировок.",
                            Severity = DiagnosticSeverity.Info
                        });
                    }
                }
                catch { }

                return list;
            });
        }

        public async Task<string> ClearDiscordCacheAsync()
        {
            return await Task.Run(() =>
            {
                var sb = new StringBuilder();
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                var discordVariations = new (string ProcName, string DisplayName, string FolderName)[]
                {
                    ("Discord", "Discord", "discord"),
                    ("DiscordPTB", "Discord PTB", "discordptb"),
                    ("DiscordCanary", "Discord Canary", "discordcanary"),
                    ("DiscordDevelopment", "Discord Development", "discorddevelopment")
                };

                bool anyFound = false;

                foreach (var (procName, displayName, folderName) in discordVariations)
                {
                    string dirPath = Path.Combine(appData, folderName);
                    if (!Directory.Exists(dirPath)) continue;

                    anyFound = true;
                    // Kill process
                    var procs = Process.GetProcessesByName(procName);
                    if (procs.Length > 0)
                    {
                        sb.AppendLine($"Закрытие процесса {displayName}...");
                        foreach (var p in procs)
                        {
                            try { p.Kill(); p.WaitForExit(2000); } catch { }
                        }
                    }

                    // Delete caches
                    string[] cacheFolders = { "Cache", "Code Cache", "GPUCache" };
                    foreach (var cf in cacheFolders)
                    {
                        string cachePath = Path.Combine(dirPath, cf);
                        if (Directory.Exists(cachePath))
                        {
                            try
                            {
                                Directory.Delete(cachePath, true);
                                sb.AppendLine($"[Успешно] Очищен кэш: {displayName}\\{cf}");
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"[Ошибка] Не удалось удалить {displayName}\\{cf}: {ex.Message}");
                            }
                        }
                    }
                }

                if (!anyFound)
                {
                    sb.AppendLine("Установки Discord не обнаружены в папке %APPDATA%.");
                }
                else
                {
                    sb.AppendLine("Очистка кэша Discord завершена.");
                }

                return sb.ToString();
            });
        }

        public async Task FixConflictsAsync()
        {
            await Task.Run(() =>
            {
                string[] conflicting = { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2", "WinDivert", "WinDivert14" };
                foreach (var srv in conflicting)
                {
                    ZapretCore.RunCommandSync("net.exe", $"stop {srv}");
                    ZapretCore.RunCommandSync("sc.exe", $"delete {srv}");
                }

                ZapretCore.EnableTcpTimestamps();
            });
        }
    }
}
