using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ZapretGUI
{
    public class ListFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsUserList { get; set; }

        public override string ToString() => DisplayName;
    }

    public class ListsService
    {
        private readonly ZapretCore _core;

        public ListsService(ZapretCore core)
        {
            _core = core;
        }

        public List<ListFileInfo> GetKnownLists()
        {
            var result = new List<ListFileInfo>
            {
                new ListFileInfo
                {
                    FileName = "list-general-user.txt",
                    DisplayName = "Пользовательские домены (list-general-user.txt)",
                    Description = "Ваши собственные домены, которые нужно пускать через zapret.",
                    IsUserList = true
                },
                new ListFileInfo
                {
                    FileName = "list-exclude-user.txt",
                    DisplayName = "Пользовательские исключения (list-exclude-user.txt)",
                    Description = "Домены, которые НЕ должны обрабатываться zapret.",
                    IsUserList = true
                },
                new ListFileInfo
                {
                    FileName = "ipset-exclude-user.txt",
                    DisplayName = "Исключения IP пользователя (ipset-exclude-user.txt)",
                    Description = "IP-адреса и подсети (CIDR), исключаемые из обработки.",
                    IsUserList = true
                },
                new ListFileInfo
                {
                    FileName = "list-general.txt",
                    DisplayName = "Общий список доменов (list-general.txt)",
                    Description = "Встроенный список доменов zapret (YouTube, Discord и др.).",
                    IsUserList = false
                },
                new ListFileInfo
                {
                    FileName = "list-google.txt",
                    DisplayName = "Google домены (list-google.txt)",
                    Description = "Специфические домены Google сервисов.",
                    IsUserList = false
                },
                new ListFileInfo
                {
                    FileName = "list-exclude.txt",
                    DisplayName = "Общие исключения (list-exclude.txt)",
                    Description = "Встроенные исключения доменов (банки, госсервисы и др.).",
                    IsUserList = false
                },
                new ListFileInfo
                {
                    FileName = "ipset-exclude.txt",
                    DisplayName = "Общие исключения IP (ipset-exclude.txt)",
                    Description = "Встроенные исключения подсетей IP.",
                    IsUserList = false
                },
                new ListFileInfo
                {
                    FileName = "ipset-all.txt",
                    DisplayName = "Активный IPSet (ipset-all.txt)",
                    Description = "Полный список диапазонов IP-адресов подсетей для фильтрации.",
                    IsUserList = false
                }
            };

            foreach (var item in result)
            {
                item.FullPath = Path.Combine(_core.ListsPath, item.FileName);
            }

            return result;
        }

        public string ReadContent(ListFileInfo fileInfo)
        {
            if (!File.Exists(fileInfo.FullPath))
            {
                return "";
            }
            try
            {
                return File.ReadAllText(fileInfo.FullPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return $"[Ошибка чтения файла: {ex.Message}]";
            }
        }

        public bool SaveContent(ListFileInfo fileInfo, string content, out string errorMessage)
        {
            errorMessage = "";
            try
            {
                if (!Directory.Exists(_core.ListsPath)) Directory.CreateDirectory(_core.ListsPath);

                // Create backup
                if (File.Exists(fileInfo.FullPath))
                {
                    string backup = fileInfo.FullPath + ".bak";
                    File.Copy(fileInfo.FullPath, backup, true);
                }

                File.WriteAllText(fileInfo.FullPath, content, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public bool AppendEntry(ListFileInfo fileInfo, string entry, out string errorMessage)
        {
            errorMessage = "";
            try
            {
                entry = entry.Trim();
                if (string.IsNullOrWhiteSpace(entry)) return false;

                string current = ReadContent(fileInfo);
                var lines = current.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

                if (lines.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    errorMessage = "Запись уже присутствует в списке.";
                    return false;
                }

                string newContent = current.TrimEnd() + Environment.NewLine + entry + Environment.NewLine;
                return SaveContent(fileInfo, newContent, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
