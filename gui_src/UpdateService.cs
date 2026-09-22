using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ZapretGUI
{
    public class UpdateService
    {
        private static readonly HttpClient _httpClient;

        static UpdateService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretGUI/1.10.2 (Windows NT 10.0; Win64; x64)");
        }

        public const string VersionUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt";
        public const string ReleaseUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/latest";
        public const string IpsetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
        public const string HostsUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts";

        private readonly ZapretCore _core;

        public UpdateService(ZapretCore core)
        {
            _core = core;
        }

        public async Task<(bool HasUpdate, string RemoteVersion, string Message)> CheckZapretVersionAsync()
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, VersionUrl);
                req.Headers.Add("Cache-Control", "no-cache");
                var response = await _httpClient.SendAsync(req);
                response.EnsureSuccessStatusCode();

                string remote = (await response.Content.ReadAsStringAsync()).Trim();
                if (string.IsNullOrWhiteSpace(remote))
                {
                    return (false, "", "Не удалось прочитать версию из репозитория.");
                }

                bool hasUpdate = !string.Equals(ZapretCore.LocalVersion, remote, StringComparison.OrdinalIgnoreCase);
                string msg = hasUpdate
                    ? $"Доступна новая версия: v{remote} (текущая: v{ZapretCore.LocalVersion})"
                    : $"У вас установлена актуальная версия: v{ZapretCore.LocalVersion}";

                return (hasUpdate, remote, msg);
            }
            catch (Exception ex)
            {
                return (false, "", $"Ошибка при проверке обновления: {ex.Message}");
            }
        }

        public async Task<(bool Success, int LineCount, string Message)> UpdateIpsetAsync()
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, IpsetUrl);
                req.Headers.Add("Cache-Control", "no-cache");
                var response = await _httpClient.SendAsync(req);
                response.EnsureSuccessStatusCode();

                string content = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content))
                {
                    return (false, 0, "Получен пустой ответ от сервера.");
                }

                if (!Directory.Exists(_core.ListsPath)) Directory.CreateDirectory(_core.ListsPath);
                string ipsetFile = Path.Combine(_core.ListsPath, "ipset-all.txt");
                string backupFile = Path.Combine(_core.ListsPath, "ipset-all.txt.backup");

                // Write to file and backup
                File.WriteAllText(ipsetFile, content, Encoding.UTF8);
                File.WriteAllText(backupFile, content, Encoding.UTF8);

                int count = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => !l.TrimStart().StartsWith("#"));

                return (true, count, $"Список IPSet успешно обновлен ({count} подсетей/IP).");
            }
            catch (Exception ex)
            {
                return (false, 0, $"Ошибка скачивания IPSet: {ex.Message}");
            }
        }

        public async Task<(bool NeedsUpdate, string RemoteContent, string Message)> CheckHostsFileAsync()
        {
            try
            {
                string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                if (!File.Exists(hostsPath))
                {
                    return (false, "", "Системный файл hosts не найден.");
                }

                string url = $"{HostsUrl}?t={Guid.NewGuid():N}";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                var response = await _httpClient.SendAsync(req);
                response.EnsureSuccessStatusCode();

                string remoteContent = await response.Content.ReadAsStringAsync();
                var remoteLines = remoteContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                    .ToList();

                if (remoteLines.Count == 0)
                {
                    return (false, remoteContent, "Удаленный файл hosts пуст.");
                }

                string firstLine = remoteLines.First().Trim();
                string lastLine = remoteLines.Last().Trim();

                string localContent = File.ReadAllText(hostsPath);
                bool hasFirst = localContent.Contains(firstLine);
                bool hasLast = localContent.Contains(lastLine);

                bool needsUpdate = !hasFirst || !hasLast;
                string msg = needsUpdate
                    ? "Файл hosts не содержит актуальных записей из репозитория zapret."
                    : "Файл hosts содержит все актуальные записи.";

                return (needsUpdate, remoteContent, msg);
            }
            catch (Exception ex)
            {
                return (false, "", $"Ошибка проверки hosts: {ex.Message}");
            }
        }

        public async Task<(bool Success, string Message)> ApplyHostsUpdateAsync(string remoteHostsContent)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                    string backupPath = hostsPath + ".bak_zapret";

                    if (!File.Exists(hostsPath))
                    {
                        return (false, "Системный файл hosts не найден.");
                    }

                    // Create backup
                    File.Copy(hostsPath, backupPath, true);

                    string current = File.ReadAllText(hostsPath);
                    const string markerStart = "# === ZAPRET DPI BYPASS START ===";
                    const string markerEnd = "# === ZAPRET DPI BYPASS END ===";

                    string newBlock = $"{Environment.NewLine}{markerStart}{Environment.NewLine}{remoteHostsContent.Trim()}{Environment.NewLine}{markerEnd}{Environment.NewLine}";

                    if (current.Contains(markerStart) && current.Contains(markerEnd))
                    {
                        int startIdx = current.IndexOf(markerStart, StringComparison.Ordinal);
                        int endIdx = current.IndexOf(markerEnd, StringComparison.Ordinal) + markerEnd.Length;
                        current = current.Remove(startIdx, endIdx - startIdx).Insert(startIdx, newBlock);
                    }
                    else
                    {
                        current = current.TrimEnd() + newBlock;
                    }

                    File.WriteAllText(hostsPath, current, new UTF8Encoding(false));
                    return (true, $"Файл hosts успешно обновлен! (Резервная копия: hosts.bak_zapret)");
                }
                catch (Exception ex)
                {
                    return (false, $"Ошибка записи в файл hosts: {ex.Message}. Убедитесь, что приложение запущено с правами Администратора.");
                }
            });
        }
    }
}
