using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ZapretGUI
{
    public class ZapretCore
    {
        public static readonly string LocalVersion = "1.10.2";

        public string ZapretRoot { get; private set; } = string.Empty;
        public string BinPath => Path.Combine(ZapretRoot, "bin");
        public string ListsPath => Path.Combine(ZapretRoot, "lists");
        public string UtilsPath => Path.Combine(ZapretRoot, "utils");
        public string WinwsExePath => Path.Combine(BinPath, "winws.exe");

        private Process? _standaloneProcess;
        public int? StandalonePid => _standaloneProcess is { HasExited: false } ? _standaloneProcess.Id : null;
        public DateTime? StandaloneStartTime { get; private set; }

        public event Action<string>? LogReceived;
        public event Action? StatusChanged;

        public ZapretCore()
        {
            ZapretRoot = FindZapretRoot();
            EnsureUserLists();
        }

        public void SetZapretRoot(string path)
        {
            if (Directory.Exists(path))
            {
                ZapretRoot = path;
                EnsureUserLists();
                StatusChanged?.Invoke();
            }
        }

        public static string FindZapretRoot()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            
            // Check current directory
            if (IsValidZapretRoot(baseDir)) return baseDir;

            // Check parent directories up to 3 levels
            var parent = Directory.GetParent(baseDir);
            for (int i = 0; i < 3 && parent != null; i++)
            {
                if (IsValidZapretRoot(parent.FullName)) return parent.FullName;
                parent = parent.Parent;
            }

            // Check current working directory
            string cwd = Directory.GetCurrentDirectory();
            if (IsValidZapretRoot(cwd)) return cwd;

            // Check Documents\zapret
            string myDocs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "zapret");
            if (IsValidZapretRoot(myDocs)) return myDocs;

            // Check UserProfile\zapret
            string userProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "zapret");
            if (IsValidZapretRoot(userProfile)) return userProfile;

            // Check standard C:\zapret
            string rootZapret = @"C:\zapret";
            if (IsValidZapretRoot(rootZapret)) return rootZapret;

            return baseDir;
        }

        public static bool IsValidZapretRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            return File.Exists(Path.Combine(path, "bin", "winws.exe")) ||
                   Directory.Exists(Path.Combine(path, "lists"));
        }

        public void EnsureUserLists()
        {
            try
            {
                if (!Directory.Exists(ListsPath)) Directory.CreateDirectory(ListsPath);

                string ipsetExclude = Path.Combine(ListsPath, "ipset-exclude-user.txt");
                if (!File.Exists(ipsetExclude))
                {
                    File.WriteAllText(ipsetExclude, "203.0.113.113/32" + Environment.NewLine, Encoding.UTF8);
                }

                string listGeneral = Path.Combine(ListsPath, "list-general-user.txt");
                if (!File.Exists(listGeneral))
                {
                    File.WriteAllText(listGeneral, "# Never leave this file empty" + Environment.NewLine + "domain.example.abc" + Environment.NewLine, Encoding.UTF8);
                }

                string listExclude = Path.Combine(ListsPath, "list-exclude-user.txt");
                if (!File.Exists(listExclude))
                {
                    File.WriteAllText(listExclude, "domain.example.abc" + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Ошибка инициализации списков пользователя: {ex.Message}");
            }
        }

        public void Log(string message)
        {
            string formatted = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogReceived?.Invoke(formatted);
        }

        #region Strategies

        public List<StrategyInfo> GetStrategies()
        {
            var strategies = new List<StrategyInfo>();
            if (!Directory.Exists(ZapretRoot)) return strategies;

            var batFiles = Directory.GetFiles(ZapretRoot, "*.bat", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Natural numeric sort (general, general (ALT), general (ALT2), ... general (ALT13))
            batFiles = batFiles.OrderBy(f => Regex.Replace(Path.GetFileNameWithoutExtension(f), @"(\d+)", m => m.Value.PadLeft(6, '0'))).ToList();

            foreach (var file in batFiles)
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    string name = Path.GetFileNameWithoutExtension(file);
                    string content = File.ReadAllText(file, Encoding.UTF8);
                    bool notRecommended = content.Contains("NOT RECOMMENDED", StringComparison.OrdinalIgnoreCase);

                    string rawArgs = ParseBatArguments(content);

                    strategies.Add(new StrategyInfo
                    {
                        Name = name,
                        FileName = fileName,
                        FullPath = file,
                        RawArguments = rawArgs,
                        Description = notRecommended ? "⚠️ Не рекомендуется" : "Стандартная стратегия",
                        IsRecommended = !notRecommended
                    });
                }
                catch { }
            }

            return strategies;
        }

        public string ParseBatArguments(string batContent)
        {
            var lines = batContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            bool capturing = false;

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("::") || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (line.Contains("winws.exe", StringComparison.OrdinalIgnoreCase))
                {
                    capturing = true;
                    int idx = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
                    string afterExe = line.Substring(idx + "winws.exe".Length).Trim();
                    if (afterExe.StartsWith("\"")) afterExe = afterExe.Substring(1).Trim();
                    if (afterExe.EndsWith("^")) afterExe = afterExe.Substring(0, afterExe.Length - 1).Trim();
                    sb.Append(afterExe).Append(' ');
                }
                else if (capturing)
                {
                    bool continues = line.EndsWith("^");
                    string clean = continues ? line.Substring(0, line.Length - 1).Trim() : line;
                    sb.Append(clean).Append(' ');
                    if (!continues) break;
                }
            }

            return sb.ToString().Trim();
        }

        public string ResolveArguments(string rawArgs)
        {
            var (tcpPort, udpPort) = GetGameFilterPorts();

            string args = rawArgs
                .Replace("%BIN%", BinPath.TrimEnd('\\') + "\\")
                .Replace("%~dp0bin\\", BinPath.TrimEnd('\\') + "\\")
                .Replace("%LISTS%", ListsPath.TrimEnd('\\') + "\\")
                .Replace("%~dp0lists\\", ListsPath.TrimEnd('\\') + "\\")
                .Replace("%GameFilterTCP%", tcpPort)
                .Replace("%GameFilterUDP%", udpPort)
                .Replace("^", "");

            // Collapse multiple spaces
            args = Regex.Replace(args, @"\s+", " ").Trim();
            return args;
        }

        #endregion

        #region Game Filter

        public (string tcp, string udp) GetGameFilterPorts()
        {
            var mode = GetGameFilterMode();
            return mode switch
            {
                GameFilterMode.All => ("1024-65535", "1024-65535"),
                GameFilterMode.TcpOnly => ("1024-65535", "12"),
                GameFilterMode.UdpOnly => ("12", "1024-65535"),
                _ => ("12", "12")
            };
        }

        public GameFilterMode GetGameFilterMode()
        {
            string flagFile = Path.Combine(UtilsPath, "game_filter.enabled");
            if (!File.Exists(flagFile)) return GameFilterMode.Disabled;

            try
            {
                string text = File.ReadAllText(flagFile).Trim().ToLowerInvariant();
                return text switch
                {
                    "all" => GameFilterMode.All,
                    "tcp" => GameFilterMode.TcpOnly,
                    "udp" => GameFilterMode.UdpOnly,
                    _ => GameFilterMode.UdpOnly
                };
            }
            catch
            {
                return GameFilterMode.Disabled;
            }
        }

        public void SetGameFilterMode(GameFilterMode mode)
        {
            try
            {
                if (!Directory.Exists(UtilsPath)) Directory.CreateDirectory(UtilsPath);
                string flagFile = Path.Combine(UtilsPath, "game_filter.enabled");

                switch (mode)
                {
                    case GameFilterMode.Disabled:
                        if (File.Exists(flagFile)) File.Delete(flagFile);
                        Log("[GameFilter] Игровой фильтр отключен (порты 12).");
                        break;
                    case GameFilterMode.All:
                        File.WriteAllText(flagFile, "all" + Environment.NewLine, Encoding.UTF8);
                        Log("[GameFilter] Включен игровой фильтр TCP и UDP (1024-65535).");
                        break;
                    case GameFilterMode.TcpOnly:
                        File.WriteAllText(flagFile, "tcp" + Environment.NewLine, Encoding.UTF8);
                        Log("[GameFilter] Включен игровой фильтр только TCP (1024-65535).");
                        break;
                    case GameFilterMode.UdpOnly:
                        File.WriteAllText(flagFile, "udp" + Environment.NewLine, Encoding.UTF8);
                        Log("[GameFilter] Включен игровой фильтр только UDP (1024-65535).");
                        break;
                }
                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Ошибка изменения игрового фильтра: {ex.Message}");
            }
        }

        #endregion

        #region IPSet Filter

        public IPSetMode GetIPSetMode(out int lineCount)
        {
            string listFile = Path.Combine(ListsPath, "ipset-all.txt");
            lineCount = 0;

            if (!File.Exists(listFile)) return IPSetMode.Any;

            try
            {
                var lines = File.ReadAllLines(listFile);
                var validLines = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")).ToList();
                lineCount = validLines.Count;

                if (lineCount == 0) return IPSetMode.Any;
                if (validLines.Any(l => l.Contains("203.0.113.113/32"))) return IPSetMode.None;

                return IPSetMode.Loaded;
            }
            catch
            {
                return IPSetMode.Loaded;
            }
        }

        public void SetIPSetMode(IPSetMode targetMode)
        {
            try
            {
                if (!Directory.Exists(ListsPath)) Directory.CreateDirectory(ListsPath);
                string listFile = Path.Combine(ListsPath, "ipset-all.txt");
                string backupFile = Path.Combine(ListsPath, "ipset-all.txt.backup");

                var currentMode = GetIPSetMode(out _);
                if (currentMode == targetMode) return;

                if (targetMode == IPSetMode.None)
                {
                    if (File.Exists(listFile) && currentMode == IPSetMode.Loaded)
                    {
                        File.Copy(listFile, backupFile, true);
                    }
                    File.WriteAllText(listFile, "203.0.113.113/32" + Environment.NewLine, Encoding.UTF8);
                    Log("[IPSet] Переключено в режим 'None' (фильтрация только тестового IP).");
                }
                else if (targetMode == IPSetMode.Any)
                {
                    if (File.Exists(listFile) && currentMode == IPSetMode.Loaded)
                    {
                        File.Copy(listFile, backupFile, true);
                    }
                    File.WriteAllText(listFile, "", Encoding.UTF8);
                    Log("[IPSet] Переключено в режим 'Any' (фильтрация всего трафика / пустой список).");
                }
                else if (targetMode == IPSetMode.Loaded)
                {
                    if (File.Exists(backupFile))
                    {
                        File.Copy(backupFile, listFile, true);
                        Log("[IPSet] Переключено в режим 'Loaded' (восстановлен список из резервной копии).");
                    }
                    else
                    {
                        Log("[IPSet] Резервная копия не найдена! Требуется загрузить ipset через вкладку Обновления.");
                    }
                }

                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Ошибка смены режима IPSet: {ex.Message}");
            }
        }

        #endregion

        #region Auto-Update Toggle

        public bool IsAutoUpdateEnabled()
        {
            string flagFile = Path.Combine(UtilsPath, "check_updates.enabled");
            return File.Exists(flagFile);
        }

        public void SetAutoUpdateEnabled(bool enabled)
        {
            try
            {
                if (!Directory.Exists(UtilsPath)) Directory.CreateDirectory(UtilsPath);
                string flagFile = Path.Combine(UtilsPath, "check_updates.enabled");
                if (enabled)
                {
                    File.WriteAllText(flagFile, "ENABLED" + Environment.NewLine, Encoding.UTF8);
                    Log("[Auto-Update] Автоматическая проверка обновлений включена.");
                }
                else
                {
                    if (File.Exists(flagFile)) File.Delete(flagFile);
                    Log("[Auto-Update] Автоматическая проверка обновлений отключена.");
                }
                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Ошибка изменения автообновления: {ex.Message}");
            }
        }

        #endregion

        #region Active Fakes

        public List<FakeBinFile> GetAvailableFakes()
        {
            var result = new List<FakeBinFile>();
            if (!Directory.Exists(BinPath)) return result;

            var binFiles = Directory.GetFiles(BinPath, "*.bin", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            using var sha256 = SHA256.Create();
            foreach (var file in binFiles)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    byte[] hash = sha256.ComputeHash(bytes);
                    string hashStr = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();

                    result.Add(new FakeBinFile
                    {
                        FileName = Path.GetFileName(file),
                        FullPath = file,
                        Sha256Hash = hashStr
                    });
                }
                catch { }
            }

            return result;
        }

        public (string discordFake, string gameFake) GetCurrentActiveFakes(List<FakeBinFile>? availableFakes = null)
        {
            availableFakes ??= GetAvailableFakes();

            string discordFake = "(не найдено)";
            string gameFake = "(не найдено)";

            string discordFile = Path.Combine(BinPath, "ACTIVE_DISCORD_UDP.bin");
            string gameFile = Path.Combine(BinPath, "ACTIVE_GAME_UDP.bin");

            using var sha256 = SHA256.Create();

            if (File.Exists(discordFile))
            {
                try
                {
                    string hash = BitConverter.ToString(sha256.ComputeHash(File.ReadAllBytes(discordFile))).Replace("-", "").ToUpperInvariant();
                    var match = availableFakes.FirstOrDefault(f => f.Sha256Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    discordFake = match != null ? match.FileName : $"Кастомный ({hash.Substring(0, 8)}...)";
                }
                catch { }
            }

            if (File.Exists(gameFile))
            {
                try
                {
                    string hash = BitConverter.ToString(sha256.ComputeHash(File.ReadAllBytes(gameFile))).Replace("-", "").ToUpperInvariant();
                    var match = availableFakes.FirstOrDefault(f => f.Sha256Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    gameFake = match != null ? match.FileName : $"Кастомный ({hash.Substring(0, 8)}...)";
                }
                catch { }
            }

            return (discordFake, gameFake);
        }

        public bool ReplaceActiveFake(int fakeType, string sourceFilePath)
        {
            try
            {
                string targetFile = fakeType == 1
                    ? Path.Combine(BinPath, "ACTIVE_DISCORD_UDP.bin")
                    : Path.Combine(BinPath, "ACTIVE_GAME_UDP.bin");

                if (!File.Exists(sourceFilePath))
                {
                    Log($"[ERROR] Исходный файл fake не найден: {sourceFilePath}");
                    return false;
                }

                File.Copy(sourceFilePath, targetFile, true);
                Log($"[Fake] Успешно заменен активный fake {(fakeType == 1 ? "Discord UDP" : "Game UDP")} на: {Path.GetFileName(sourceFilePath)}");
                StatusChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Ошибка замены fake: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Standalone Process Runner

        public bool IsStandaloneRunning()
        {
            if (_standaloneProcess != null && !_standaloneProcess.HasExited)
                return true;

            return Process.GetProcessesByName("winws").Length > 0 && GetZapretServiceStatus() != "RUNNING";
        }

        public async Task<bool> StartStandaloneAsync(StrategyInfo strategy, string? customArgs = null)
        {
            if (IsStandaloneRunning())
            {
                Log("[WARN] Standalone winws уже запущен. Остановите его перед новым запуском.");
                return false;
            }

            string srvStatus = GetZapretServiceStatus();
            if (srvStatus == "RUNNING")
            {
                Log("[WARN] Служба zapret сейчас запущена! Чтобы избежать конфликта WinDivert, остановите службу.");
                return false;
            }

            await Task.Run(() =>
            {
                EnableTcpTimestamps();
                EnsureUserLists();
            });

            string finalArgs = !string.IsNullOrWhiteSpace(customArgs)
                ? customArgs
                : ResolveArguments(strategy.RawArguments);

            Log($"[Standalone] Запуск winws.exe со стратегией '{strategy.Name}'...");
            Log($"[Standalone] Аргументы: {finalArgs}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = WinwsExePath,
                    Arguments = finalArgs,
                    WorkingDirectory = BinPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _standaloneProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

                _standaloneProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Log($"[winws] {e.Data}");
                };

                _standaloneProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data)) Log($"[winws:err] {e.Data}");
                };

                _standaloneProcess.Exited += (s, e) =>
                {
                    Log($"[Standalone] winws.exe завершил работу (код выхода: {_standaloneProcess.ExitCode}).");
                    StandaloneStartTime = null;
                    StatusChanged?.Invoke();
                };

                bool started = _standaloneProcess.Start();
                if (started)
                {
                    StandaloneStartTime = DateTime.Now;
                    _standaloneProcess.BeginOutputReadLine();
                    _standaloneProcess.BeginErrorReadLine();
                    Log($"[Standalone] Процесс успешно запущен (PID: {_standaloneProcess.Id}).");
                    StatusChanged?.Invoke();
                    return true;
                }
                else
                {
                    Log("[ERROR] Не удалось запустить winws.exe.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Ошибка запуска winws.exe: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> StopStandaloneAsync()
        {
            Log("[Standalone] Остановка winws.exe...");
            bool killed = false;

            await Task.Run(() =>
            {
                try
                {
                    if (_standaloneProcess != null && !_standaloneProcess.HasExited)
                    {
                        _standaloneProcess.Kill(true);
                        _standaloneProcess.WaitForExit(3000);
                        _standaloneProcess.Dispose();
                        _standaloneProcess = null;
                        killed = true;
                    }

                    // Also kill any orphaned winws processes
                    var procs = Process.GetProcessesByName("winws");
                    foreach (var p in procs)
                    {
                        try
                        {
                            p.Kill();
                            p.WaitForExit(2000);
                            killed = true;
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] Ошибка остановки winws: {ex.Message}");
                }
            });

            StandaloneStartTime = null;
            Log(killed ? "[Standalone] Процесс winws успешно остановлен." : "[Standalone] Процесс winws не был запущен.");
            StatusChanged?.Invoke();
            return killed;
        }

        #endregion

        #region Windows Service Management

        public string GetZapretServiceStatus()
        {
            try
            {
                var (code, stdout, _) = RunCommandSync("sc.exe", "query zapret");
                if (code != 0 || stdout.Contains("1060") || stdout.Contains("FAILED"))
                    return "NOT_INSTALLED";

                if (stdout.Contains("RUNNING")) return "RUNNING";
                if (stdout.Contains("STOPPED")) return "STOPPED";
                if (stdout.Contains("START_PENDING")) return "START_PENDING";
                if (stdout.Contains("STOP_PENDING")) return "STOP_PENDING";
                if (stdout.Contains("PAUSED")) return "PAUSED";

                return "UNKNOWN";
            }
            catch
            {
                return "NOT_INSTALLED";
            }
        }

        public string GetInstalledServiceStrategy()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\zapret");
                if (key != null)
                {
                    object? val = key.GetValue("zapret-discord-youtube");
                    if (val != null) return val.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        public async Task<bool> InstallServiceAsync(StrategyInfo strategy, string? customArgs = null)
        {
            Log($"[Service] Установка службы zapret со стратегией '{strategy.Name}'...");

            return await Task.Run(() =>
            {
                try
                {
                    EnableTcpTimestamps();
                    EnsureUserLists();

                    string finalArgs = !string.IsNullOrWhiteSpace(customArgs)
                        ? customArgs
                        : ResolveArguments(strategy.RawArguments);

                    // Stop and delete old service
                    RunCommandSync("net.exe", "stop zapret");
                    RunCommandSync("sc.exe", "delete zapret");
                    Thread.Sleep(500);

                    // sc create: escape all inner quotes in finalArgs with \" so sc.exe receives them properly
                    string escapedArgs = finalArgs.Replace("\"", "\\\"");
                    string binPathArg = $"\"\\\"{WinwsExePath}\\\" {escapedArgs}\"";
                    string createArgs = $"create zapret binPath= {binPathArg} DisplayName= \"zapret\" start= auto";
                    var (c1, o1, e1) = RunCommandSync("sc.exe", createArgs);
                    if (c1 != 0)
                    {
                        Log($"[ERROR] Ошибка sc create: {o1} {e1}");
                        return false;
                    }

                    RunCommandSync("sc.exe", "description zapret \"Zapret DPI bypass software\"");
                    
                    // Add registry value for strategy name
                    try
                    {
                        using var key = Registry.LocalMachine.CreateSubKey(@"System\CurrentControlSet\Services\zapret");
                        key.SetValue("zapret-discord-youtube", strategy.Name, RegistryValueKind.String);
                    }
                    catch (Exception ex)
                    {
                        Log($"[WARN] Не удалось записать имя стратегии в реестр: {ex.Message}");
                    }

                    // Start service
                    var (c2, o2, e2) = RunCommandSync("sc.exe", "start zapret");
                    Log($"[Service] Результат запуска службы: {o2.Trim()}");

                    StatusChanged?.Invoke();
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] Ошибка при установке службы: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> StartServiceAsync()
        {
            Log("[Service] Запуск службы zapret...");
            return await Task.Run(() =>
            {
                var (code, stdout, stderr) = RunCommandSync("sc.exe", "start zapret");
                Log($"[Service] {stdout.Trim()} {stderr.Trim()}");
                StatusChanged?.Invoke();
                return code == 0;
            });
        }

        public async Task<bool> StopServiceAsync()
        {
            Log("[Service] Остановка службы zapret...");
            return await Task.Run(() =>
            {
                var (code, stdout, stderr) = RunCommandSync("net.exe", "stop zapret");
                Log($"[Service] {stdout.Trim()} {stderr.Trim()}");
                StatusChanged?.Invoke();
                return code == 0;
            });
        }

        public async Task<bool> RestartServiceAsync()
        {
            Log("[Service] Перезапуск службы zapret...");
            return await Task.Run(async () =>
            {
                await StopServiceAsync();
                await Task.Delay(1000);
                return await StartServiceAsync();
            });
        }

        public async Task RemoveAllServicesAsync()
        {
            Log("[Service] Полное удаление служб zapret и WinDivert...");

            await Task.Run(() =>
            {
                // Stop & delete zapret
                RunCommandSync("net.exe", "stop zapret");
                RunCommandSync("sc.exe", "delete zapret");

                // Kill winws
                RunCommandSync("taskkill.exe", "/IM winws.exe /F");

                // Stop & delete WinDivert
                RunCommandSync("net.exe", "stop WinDivert");
                RunCommandSync("sc.exe", "delete WinDivert");

                // Stop & delete WinDivert14
                RunCommandSync("net.exe", "stop WinDivert14");
                RunCommandSync("sc.exe", "delete WinDivert14");
            });

            Log("[Service] Службы zapret и WinDivert удалены.");
            StatusChanged?.Invoke();
        }

        #endregion

        #region Helpers & Diagnostics

        public static void EnableTcpTimestamps()
        {
            try
            {
                RunCommandSync("netsh.exe", "interface tcp set global timestamps=enabled");
            }
            catch { }
        }

        public static (int exitCode, string stdout, string stderr) RunCommandSync(string fileName, string args)
        {
            try
            {
                using var p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                p.Start();
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, stdout, stderr);
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }

        #endregion
    }
}
