using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace ZapretGUI
{
    public partial class MainWindow : Window
    {
        private readonly ZapretCore _core;
        private readonly DiagnosticsService _diagService;
        private readonly UpdateService _updateService;
        private readonly ListsService _listsService;
        private readonly VpnService _vpnService;
        private TrayManager? _trayManager;

        private readonly DispatcherTimer _uiTimer;
        private bool _isClosing = false;
        private ListFileInfo? _currentListFile;
        private string _unfilteredListContent = "";

        private ICollectionView? _vpnProfilesView;
        private CancellationTokenSource? _pingCts;

        public MainWindow()
        {
            InitializeComponent();

            _core = new ZapretCore();
            _diagService = new DiagnosticsService(_core);
            _updateService = new UpdateService(_core);
            _listsService = new ListsService(_core);
            _vpnService = new VpnService(_core);

            // Wire up logging
            _core.LogReceived += Core_LogReceived;
            _core.StatusChanged += Core_StatusChanged;
            _vpnService.StatusChanged += () => Dispatcher.Invoke(UpdateVpnStatus);
            _vpnService.LogReceived += Core_LogReceived;

            // Timer for periodic status updates (every 2 seconds)
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _uiTimer.Tick += (s, e) => RefreshStatus();

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _trayManager = new TrayManager(this, _core);
                TxtZapretRoot.Text = $"Папка: {_core.ZapretRoot}";

                // Ensure window fits within desktop work area on any DPI scaling
                MaxHeight = SystemParameters.WorkArea.Height;
                MaxWidth = SystemParameters.WorkArea.Width;
                if (Height > MaxHeight) Height = MaxHeight;
                if (Width > MaxWidth) Width = MaxWidth;

                UpdateDpiDisplay();
                DpiChanged += (s, ev) => UpdateDpiDisplay();

                bool isAdmin = App.IsAdministrator();
                AdminBadgeBorder.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
                BtnElevateAdmin.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;

                if (!isAdmin)
                {
                    TxtStatusBar.Text = "[Внимание] Запущено без прав администратора. Некоторые функции (служба, драйвер) требуют прав администратора.";
                    _core.Log("[WARN] Приложение запущено без прав администратора. Службы и WinDivert требуют прав администратора.");
                }

                LoadStrategies();
                LoadGameFilter();
                LoadIPSetStatus();
                LoadActiveFakes();
                LoadListsDropdown();
                LoadTargetsFile();
                LoadSettingsCheckboxes();
                InitVpnTab();

                RefreshStatus();
                _uiTimer.Start();


                // Check --minimized command line argument
                if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
                {
                    WindowState = WindowState.Minimized;
                    Hide();
                    _trayManager?.ShowNotification("ZapretVPN", "Приложение запущено в системном трее.", Forms.ToolTipIcon.Info);
                }

                _core.Log("ZapretVPN инициализирован успешно.");
                await RunDiagnosticsAsync();
            }
            catch (Exception ex)
            {
                _core.Log($"[ERROR] Ошибка инициализации: {ex.Message}");
            }
        }

        private void BtnElevateAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (!App.RestartAsAdmin())
            {
                MessageBox.Show("Не удалось запросить повышение прав администратора.", "UAC", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_isClosing) return;

            if (ChkMinimizeToTray.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
                _trayManager?.ShowNotification("ZapretVPN", "Приложение свернуто в системный трей.", Forms.ToolTipIcon.Info);
            }
            else
            {
                _isClosing = true;
                VpnService.CleanupOnAppExit();
                _ = _vpnService.DisconnectAsync();
                _trayManager?.Dispose();
            }
        }

        #region Logging

        private void Core_LogReceived(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtConsoleLog.AppendText(message + Environment.NewLine);
                if (ChkAutoScroll.IsChecked == true)
                {
                    TxtConsoleLog.ScrollToEnd();
                }
                TxtStatusBar.Text = message.Length > 80 ? message.Substring(0, 80) + "..." : message;
            });
        }

        private void Core_StatusChanged()
        {
            Dispatcher.Invoke(RefreshStatus);
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtConsoleLog.Clear();
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtConsoleLog.Text))
            {
                System.Windows.Clipboard.SetText(TxtConsoleLog.Text);
                MessageBox.Show("Журнал скопирован в буфер обмена.", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region Strategies & Management

        private void LoadStrategies()
        {
            var strategies = _core.GetStrategies();
            CmbStrategies.ItemsSource = strategies;

            if (strategies.Count > 0)
            {
                // Prefer 'general' if present
                var general = strategies.FirstOrDefault(s => s.Name.Equals("general", StringComparison.OrdinalIgnoreCase)) ?? strategies[0];
                CmbStrategies.SelectedItem = general;
            }
        }

        private void CmbStrategies_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbStrategies.SelectedItem is StrategyInfo selected)
            {
                TxtStrategyArgs.Text = _core.ResolveArguments(selected.RawArguments);
            }
        }

        private void BtnResetArgs_Click(object sender, RoutedEventArgs e)
        {
            if (CmbStrategies.SelectedItem is StrategyInfo selected)
            {
                TxtStrategyArgs.Text = _core.ResolveArguments(selected.RawArguments);
                _core.Log($"[Args] Аргументы сброшены к оригинальным для '{selected.Name}'.");
            }
        }

        private void BtnReloadStrategies_Click(object sender, RoutedEventArgs e)
        {
            LoadStrategies();
            _core.Log("[Strategies] Список BAT-файлов обновлен.");
        }

        private void RefreshStatus()
        {
            bool isStandalone = _core.IsStandaloneRunning();
            string srvStatus = _core.GetZapretServiceStatus();
            string srvStrategy = _core.GetInstalledServiceStrategy();

            // Standalone card
            if (isStandalone)
            {
                TxtStandaloneStatus.Text = "РАБОТАЕТ (Active)";
                TxtStandaloneStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                string uptime = _core.StandaloneStartTime.HasValue
                    ? (DateTime.Now - _core.StandaloneStartTime.Value).ToString(@"hh\:mm\:ss")
                    : "—";
                TxtStandaloneDetails.Text = $"PID: {_core.StandalonePid} | Uptime: {uptime}";
            }
            else
            {
                TxtStandaloneStatus.Text = "Остановлен";
                TxtStandaloneStatus.Foreground = (SolidColorBrush)FindResource("AccentRed");
                TxtStandaloneDetails.Text = "PID: — | Время работы: —";
            }

            // Service card
            if (srvStatus == "RUNNING")
            {
                TxtServiceStatus.Text = "РАБОТАЕТ (Running)";
                TxtServiceStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            }
            else if (srvStatus == "STOPPED")
            {
                TxtServiceStatus.Text = "Остановлена (Stopped)";
                TxtServiceStatus.Foreground = (SolidColorBrush)FindResource("AccentYellow");
            }
            else if (srvStatus == "NOT_INSTALLED")
            {
                TxtServiceStatus.Text = "Не установлена";
                TxtServiceStatus.Foreground = (SolidColorBrush)FindResource("TextMuted");
            }
            else
            {
                TxtServiceStatus.Text = srvStatus;
                TxtServiceStatus.Foreground = (SolidColorBrush)FindResource("TextSecondary");
            }
            TxtServiceStrategy.Text = string.IsNullOrEmpty(srvStrategy) ? "Стратегия: нет" : $"Стратегия: {srvStrategy}";

            // Update VPN session duration if connected
            if (_vpnService.Status == VpnConnectionStatus.Connected && _vpnService.ConnectedTime.HasValue)
            {
                TxtVpnSessionDuration.Text = $"Время работы сессии: {(DateTime.Now - _vpnService.ConnectedTime.Value):hh\\:mm\\:ss}";
            }

            // 1. Zapret Live Indicator Pill
            bool isZapretActive = isStandalone || srvStatus == "RUNNING";
            if (isStandalone)
            {
                StatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentGreen");
                TxtGlobalStatus.Text = "Zapret: Активен (процесс)";
                StatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                StatusPillBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 16, 185, 129));
            }
            else if (srvStatus == "RUNNING")
            {
                StatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentGreen");
                TxtGlobalStatus.Text = string.IsNullOrEmpty(srvStrategy) ? "Zapret: Служба" : $"Zapret: Служба ({srvStrategy})";
                StatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
                StatusPillBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 16, 185, 129));
            }
            else
            {
                StatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentRed");
                TxtGlobalStatus.Text = "Zapret: Выключен";
                StatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("BorderCard");
                StatusPillBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 22, 25, 37));
            }

            // Quick action buttons state
            BtnQuickStart.IsEnabled = !isZapretActive;
            BtnQuickStop.IsEnabled = isZapretActive;

            // 2. VPN Live Indicator Pill
            if (_vpnService.Status == VpnConnectionStatus.Connected)
            {
                string vpnNode = _vpnService.ActiveProfile?.Name ?? "Подключен";
                VpnStatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentPurple");
                TxtGlobalVpnStatus.Text = $"VPN: {vpnNode}";
                VpnStatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("AccentPurple");
                VpnStatusPillBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(32, 139, 92, 246));
            }
            else if (_vpnService.Status == VpnConnectionStatus.Connecting)
            {
                VpnStatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentYellow");
                TxtGlobalVpnStatus.Text = "VPN: Подключение...";
                VpnStatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("AccentYellow");
                VpnStatusPillBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(28, 245, 158, 11));
            }
            else
            {
                VpnStatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentRed");
                TxtGlobalVpnStatus.Text = "VPN: Отключен";
                VpnStatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("BorderCard");
                VpnStatusPillBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 22, 25, 37));
            }

            _trayManager?.UpdateStatus();
        }

        private void UpdateDpiDisplay()
        {
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                int percent = (int)Math.Round(dpi.DpiScaleX * 100);
                TxtDpiBadge.Text = $"DPI: {percent}%";
            }
            catch
            {
                TxtDpiBadge.Text = "DPI: 100%";
            }
        }


        private async void BtnStartStandalone_Click(object sender, RoutedEventArgs e)
        {
            if (CmbStrategies.SelectedItem is not StrategyInfo strategy)
            {
                MessageBox.Show("Выберите стратегию из списка.", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_core.IsStandaloneRunning())
            {
                var ask = MessageBox.Show($"Процесс winws.exe уже запущен.\nПерезапустить его со стратегией '{strategy.Name}'?", "Перезапуск Standalone", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask == MessageBoxResult.Yes)
                {
                    await _core.StopStandaloneAsync();
                    await Task.Delay(500);
                }
                else
                {
                    return;
                }
            }

            string customArgs = TxtStrategyArgs.Text.Trim();
            await _core.StartStandaloneAsync(strategy, customArgs);
        }

        private async void BtnStopStandalone_Click(object sender, RoutedEventArgs e)
        {
            await _core.StopStandaloneAsync();
        }

        private async void BtnRestartStandalone_Click(object sender, RoutedEventArgs e)
        {
            await _core.StopStandaloneAsync();
            await Task.Delay(500);
            if (CmbStrategies.SelectedItem is StrategyInfo strategy)
            {
                await _core.StartStandaloneAsync(strategy, TxtStrategyArgs.Text.Trim());
            }
        }

        private async void BtnInstallService_Click(object sender, RoutedEventArgs e)
        {
            if (CmbStrategies.SelectedItem is not StrategyInfo strategy)
            {
                MessageBox.Show("Выберите стратегию для установки службы.", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string customArgs = TxtStrategyArgs.Text.Trim();
            bool ok = await _core.InstallServiceAsync(strategy, customArgs);
            if (ok)
            {
                MessageBox.Show($"Служба 'zapret' успешно создана и запущена со стратегией '{strategy.Name}'!", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Ошибка при создании службы. Подробности в журнале.", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnStartService_Click(object sender, RoutedEventArgs e)
        {
            await _core.StartServiceAsync();
        }

        private async void BtnStopService_Click(object sender, RoutedEventArgs e)
        {
            await _core.StopServiceAsync();
        }

        private async void BtnRestartService_Click(object sender, RoutedEventArgs e)
        {
            await _core.RestartServiceAsync();
        }

        private async void BtnRemoveAllServices_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("Вы действительно хотите полностью удалить службу zapret и драйвер WinDivert?", "Удаление служб", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                await _core.RemoveAllServicesAsync();
                MessageBox.Show("Службы zapret и WinDivert удалены.", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void BtnQuickStart_Click(object sender, RoutedEventArgs e)
        {
            string srvStatus = _core.GetZapretServiceStatus();
            if (srvStatus == "STOPPED")
            {
                await _core.StartServiceAsync();
            }
            else if (!_core.IsStandaloneRunning() && CmbStrategies.SelectedItem is StrategyInfo strategy)
            {
                await _core.StartStandaloneAsync(strategy, TxtStrategyArgs.Text.Trim());
            }
            RefreshStatus();
        }

        private async void BtnQuickStop_Click(object sender, RoutedEventArgs e)
        {
            await _core.StopStandaloneAsync();
            await _core.StopServiceAsync();
            RefreshStatus();
        }

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "Выберите папку с распакованным Zapret",
                SelectedPath = _core.ZapretRoot
            };

            if (dlg.ShowDialog() == Forms.DialogResult.OK)
            {
                if (ZapretCore.IsValidZapretRoot(dlg.SelectedPath))
                {
                    _core.SetZapretRoot(dlg.SelectedPath);
                    TxtZapretRoot.Text = $"Папка: {_core.ZapretRoot}";
                    LoadStrategies();
                    LoadGameFilter();
                    LoadIPSetStatus();
                    LoadActiveFakes();
                    LoadListsDropdown();
                    LoadTargetsFile();
                    _core.Log($"[Folder] Корневая папка изменена на: {_core.ZapretRoot}");
                }
                else
                {
                    MessageBox.Show("Выбранная папка не содержит файлов Zapret (bin\\winws.exe или lists\\).", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        #endregion

        #region Filters & Settings

        private void LoadGameFilter()
        {
            var mode = _core.GetGameFilterMode();
            RbGameFilterDisabled.IsChecked = mode == GameFilterMode.Disabled;
            RbGameFilterAll.IsChecked = mode == GameFilterMode.All;
            RbGameFilterTcp.IsChecked = mode == GameFilterMode.TcpOnly;
            RbGameFilterUdp.IsChecked = mode == GameFilterMode.UdpOnly;
        }

        private void GameFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            GameFilterMode mode = GameFilterMode.Disabled;
            if (RbGameFilterAll.IsChecked == true) mode = GameFilterMode.All;
            else if (RbGameFilterTcp.IsChecked == true) mode = GameFilterMode.TcpOnly;
            else if (RbGameFilterUdp.IsChecked == true) mode = GameFilterMode.UdpOnly;

            _core.SetGameFilterMode(mode);

            // Update arguments preview if strategy selected
            if (CmbStrategies.SelectedItem is StrategyInfo selected)
            {
                TxtStrategyArgs.Text = _core.ResolveArguments(selected.RawArguments);
            }
        }

        private void LoadIPSetStatus()
        {
            var mode = _core.GetIPSetMode(out int lineCount);
            string modeName = mode switch
            {
                IPSetMode.Loaded => "Loaded (Активен полный список)",
                IPSetMode.None => "None (Тестовый IP / обход выключен)",
                IPSetMode.Any => "Any (Весь трафик / пустой список)",
                _ => "Неизвестно"
            };

            TxtIpsetStatus.Text = $"Текущий статус: {modeName} — {lineCount} строк";
            TxtIpsetStatus.Foreground = mode switch
            {
                IPSetMode.Loaded => (SolidColorBrush)FindResource("AccentGreen"),
                IPSetMode.None => (SolidColorBrush)FindResource("AccentRed"),
                _ => (SolidColorBrush)FindResource("AccentYellow")
            };
        }

        private void BtnIpsetLoaded_Click(object sender, RoutedEventArgs e)
        {
            _core.SetIPSetMode(IPSetMode.Loaded);
            LoadIPSetStatus();
        }

        private void BtnIpsetNone_Click(object sender, RoutedEventArgs e)
        {
            _core.SetIPSetMode(IPSetMode.None);
            LoadIPSetStatus();
        }

        private void BtnIpsetAny_Click(object sender, RoutedEventArgs e)
        {
            _core.SetIPSetMode(IPSetMode.Any);
            LoadIPSetStatus();
        }

        private void LoadActiveFakes()
        {
            var fakes = _core.GetAvailableFakes();
            CmbDiscordFake.ItemsSource = fakes;
            CmbGameFake.ItemsSource = fakes;

            var (curDiscord, curGame) = _core.GetCurrentActiveFakes(fakes);
            TxtCurrentDiscordFake.Text = $"активный: {curDiscord}";
            TxtCurrentGameFake.Text = $"активный: {curGame}";

            var discMatch = fakes.FirstOrDefault(f => f.FileName.Equals(curDiscord, StringComparison.OrdinalIgnoreCase));
            if (discMatch != null) CmbDiscordFake.SelectedItem = discMatch;

            var gameMatch = fakes.FirstOrDefault(f => f.FileName.Equals(curGame, StringComparison.OrdinalIgnoreCase));
            if (gameMatch != null) CmbGameFake.SelectedItem = gameMatch;
        }

        private void BtnApplyDiscordFake_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDiscordFake.SelectedItem is FakeBinFile fake)
            {
                bool ok = _core.ReplaceActiveFake(1, fake.FullPath);
                if (ok)
                {
                    LoadActiveFakes();
                    MessageBox.Show($"Фейковый пакет Discord UDP заменен на {fake.FileName}.", "Active Fakes", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void BtnApplyGameFake_Click(object sender, RoutedEventArgs e)
        {
            if (CmbGameFake.SelectedItem is FakeBinFile fake)
            {
                bool ok = _core.ReplaceActiveFake(2, fake.FullPath);
                if (ok)
                {
                    LoadActiveFakes();
                    MessageBox.Show($"Фейковый пакет Game UDP заменен на {fake.FileName}.", "Active Fakes", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void LoadSettingsCheckboxes()
        {
            ChkAutoUpdate.IsChecked = _core.IsAutoUpdateEnabled();
            ChkAutostart.IsChecked = TrayManager.IsAutoStartEnabled();
        }

        private void ChkAutoUpdate_Click(object sender, RoutedEventArgs e)
        {
            _core.SetAutoUpdateEnabled(ChkAutoUpdate.IsChecked == true);
        }

        private void ChkAutostart_Click(object sender, RoutedEventArgs e)
        {
            TrayManager.SetAutoStart(ChkAutostart.IsChecked == true);
            _core.Log(ChkAutostart.IsChecked == true
                ? "[Autostart] Автозапуск с Windows включен."
                : "[Autostart] Автозапуск с Windows отключен.");
        }

        private void BtnEnableTimestamps_Click(object sender, RoutedEventArgs e)
        {
            ZapretCore.EnableTcpTimestamps();
            _core.Log("[TCP] Команда включения TCP timestamps выполнена.");
            MessageBox.Show("TCP Timestamps успешно включены через netsh.", "ZapretVPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Lists Editor

        private void LoadListsDropdown()
        {
            var lists = _listsService.GetKnownLists();
            CmbLists.ItemsSource = lists;
            if (lists.Count > 0)
            {
                CmbLists.SelectedIndex = 0;
            }
        }

        private void CmbLists_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLists.SelectedItem is ListFileInfo info)
            {
                _currentListFile = info;
                _unfilteredListContent = _listsService.ReadContent(info);
                TxtListContent.Text = _unfilteredListContent;
                UpdateListStats();
            }
        }

        private void UpdateListStats()
        {
            if (string.IsNullOrEmpty(TxtListContent.Text))
            {
                TxtListStats.Text = "Всего строк: 0";
                return;
            }

            int count = TxtListContent.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(l => !l.TrimStart().StartsWith("#"));
            TxtListStats.Text = $"Записей (без комментариев): {count}";
        }

        private void TxtListContent_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateListStats();
        }

        private void BtnQuickAdd_Click(object sender, RoutedEventArgs e)
        {
            string entry = TxtQuickAdd.Text.Trim();
            if (string.IsNullOrWhiteSpace(entry)) return;
            if (_currentListFile == null) return;

            bool ok = _listsService.AppendEntry(_currentListFile, entry, out string err);
            if (ok)
            {
                _core.Log($"[List] Добавлена запись '{entry}' в {_currentListFile.FileName}");
                TxtQuickAdd.Clear();
                _unfilteredListContent = _listsService.ReadContent(_currentListFile);
                TxtListContent.Text = _unfilteredListContent;
            }
            else
            {
                MessageBox.Show($"Не удалось добавить запись: {err}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private int _lastSearchIndex = -1;

        private void TxtSearchList_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = TxtSearchList.Text.Trim();
            _lastSearchIndex = -1;

            if (string.IsNullOrEmpty(query))
            {
                TxtListContent.IsReadOnly = false;
                BtnSaveList.IsEnabled = true;
                TxtSearchWarning.Visibility = Visibility.Collapsed;
                TxtListContent.Text = _unfilteredListContent;
                return;
            }

            var matchingLines = _unfilteredListContent
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Where(l => l.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            TxtListContent.IsReadOnly = true;
            BtnSaveList.IsEnabled = false;
            TxtSearchWarning.Text = $"[Поиск] Найдено совпадений: {matchingLines.Count}. Сохранение заблокировано для защиты данных.";
            TxtSearchWarning.Visibility = Visibility.Visible;

            TxtListContent.Text = string.Join(Environment.NewLine, matchingLines);
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchList.Clear();
            TxtListContent.IsReadOnly = false;
            BtnSaveList.IsEnabled = true;
            TxtSearchWarning.Visibility = Visibility.Collapsed;
            TxtListContent.Text = _unfilteredListContent;
            _lastSearchIndex = -1;
        }

        private void BtnFindNext_Click(object sender, RoutedEventArgs e)
        {
            string query = TxtSearchList.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            // Switch to full text view for in-place text navigation
            if (TxtListContent.IsReadOnly)
            {
                TxtListContent.IsReadOnly = false;
                TxtListContent.Text = _unfilteredListContent;
                TxtSearchWarning.Text = "Режим навигации по тексту. Нажмите Найти снова для перехода.";
                BtnSaveList.IsEnabled = true;
            }

            string fullText = TxtListContent.Text;
            int startIndex = _lastSearchIndex + 1;
            if (startIndex >= fullText.Length) startIndex = 0;

            int foundIndex = fullText.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);

            if (foundIndex < 0 && startIndex > 0)
            {
                // Wrap around to start of document
                foundIndex = fullText.IndexOf(query, 0, StringComparison.OrdinalIgnoreCase);
            }

            if (foundIndex >= 0)
            {
                _lastSearchIndex = foundIndex;
                TxtListContent.Focus();
                TxtListContent.Select(foundIndex, query.Length);

                int lineIndex = TxtListContent.GetLineIndexFromCharacterIndex(foundIndex);
                TxtListContent.ScrollToLine(lineIndex);
            }
            else
            {
                MessageBox.Show($"Текст '{query}' не найден.", "Поиск", MessageBoxButton.OK, MessageBoxImage.Information);
                _lastSearchIndex = -1;
            }
        }

        private void BtnSaveList_Click(object sender, RoutedEventArgs e)
        {
            if (_currentListFile == null) return;

            if (!string.IsNullOrWhiteSpace(TxtSearchList.Text) && TxtListContent.IsReadOnly)
            {
                MessageBox.Show("Сохранение заблокировано во время фильтрации поиска, чтобы не стереть остальные записи списка.\nОчистите строку поиска перед сохранением.", "Защита данных", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool ok = _listsService.SaveContent(_currentListFile, TxtListContent.Text, out string err);
            if (ok)
            {
                _unfilteredListContent = TxtListContent.Text;
                _core.Log($"[List] Список {_currentListFile.FileName} успешно сохранен.");
                MessageBox.Show($"Файл {_currentListFile.FileName} сохранен.", "Сохранение", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"Ошибка сохранения: {err}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnReloadList_Click(object sender, RoutedEventArgs e)
        {
            if (_currentListFile == null) return;
            _unfilteredListContent = _listsService.ReadContent(_currentListFile);
            TxtListContent.Text = _unfilteredListContent;
            _core.Log($"[List] Список {_currentListFile.FileName} перезагружен с диска.");
        }

        private void BtnOpenInNotepad_Click(object sender, RoutedEventArgs e)
        {
            if (_currentListFile != null && File.Exists(_currentListFile.FullPath))
            {
                Process.Start("notepad.exe", _currentListFile.FullPath);
            }
        }

        #endregion

        #region Diagnostics

        private async void BtnRunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            await RunDiagnosticsAsync();
        }

        private async Task RunDiagnosticsAsync()
        {
            _core.Log("[Diag] Запуск диагностики системы...");
            var items = await _diagService.RunAllDiagnosticsAsync();
            ItemsDiagnostics.ItemsSource = items;
            _core.Log($"[Diag] Проверка завершена. Проверено пунктов: {items.Count}.");
        }

        private async void BtnFixConflicts_Click(object sender, RoutedEventArgs e)
        {
            _core.Log("[Diag] Запуск автоматического устранения конфликтов...");
            await _diagService.FixConflictsAsync();
            await RunDiagnosticsAsync();
            MessageBox.Show("Конфликтующие службы удалены, TCP Timestamps включены.", "Устранение конфликтов", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnClearDiscordCache_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("Discord будет закрыт для очистки кэша. Продолжить?", "Очистка кэша Discord", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            string report = await _diagService.ClearDiscordCacheAsync();
            _core.Log(report);
            MessageBox.Show(report, "Результат очистки Discord", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region Updates

        private async void BtnCheckVersion_Click(object sender, RoutedEventArgs e)
        {
            TxtZapretVersionStatus.Text = "Проверка обновления на GitHub...";
            var (hasUpdate, remoteVer, msg) = await _updateService.CheckZapretVersionAsync();
            TxtZapretVersionStatus.Text = msg;
            _core.Log($"[Update] {msg}");

            if (hasUpdate)
            {
                var ask = MessageBox.Show($"{msg}\n\nОткрыть страницу релиза на GitHub?", "Обновление Zapret", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ask == MessageBoxResult.Yes)
                {
                    OpenUrl(UpdateService.ReleaseUrl);
                }
            }
        }

        private void BtnOpenReleaseUrl_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(UpdateService.ReleaseUrl);
        }

        private async void BtnUpdateIpset_Click(object sender, RoutedEventArgs e)
        {
            TxtIpsetUpdateStatus.Text = "Скачивание актуального IPSet из репозитория...";
            var (ok, count, msg) = await _updateService.UpdateIpsetAsync();
            TxtIpsetUpdateStatus.Text = msg;
            _core.Log($"[IPSet] {msg}");
            LoadIPSetStatus();

            MessageBox.Show(msg, ok ? "IPSet обновлен" : "Ошибка", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private string _cachedRemoteHosts = "";

        private async void BtnCheckHosts_Click(object sender, RoutedEventArgs e)
        {
            TxtHostsStatus.Text = "Проверка файла hosts...";
            var (needsUpdate, remoteContent, msg) = await _updateService.CheckHostsFileAsync();
            _cachedRemoteHosts = remoteContent;
            TxtHostsStatus.Text = msg;
            _core.Log($"[Hosts] {msg}");

            MessageBox.Show(msg, "Проверка hosts", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnApplyHostsUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_cachedRemoteHosts))
            {
                var (_, remoteContent, _) = await _updateService.CheckHostsFileAsync();
                _cachedRemoteHosts = remoteContent;
            }

            if (string.IsNullOrEmpty(_cachedRemoteHosts))
            {
                MessageBox.Show("Не удалось загрузить данные hosts с сервера.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var (ok, msg) = await _updateService.ApplyHostsUpdateAsync(_cachedRemoteHosts);
            TxtHostsStatus.Text = msg;
            _core.Log($"[Hosts] {msg}");
            MessageBox.Show(msg, ok ? "Hosts обновлен" : "Ошибка", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private void BtnOpenHostsInNotepad_Click(object sender, RoutedEventArgs e)
        {
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            if (File.Exists(hostsPath))
            {
                Process.Start("notepad.exe", hostsPath);
            }
        }

        #endregion

        #region Preset Testing

        private void LoadTargetsFile()
        {
            string path = Path.Combine(_core.UtilsPath, "targets.txt");
            if (File.Exists(path))
            {
                TxtTargetsContent.Text = File.ReadAllText(path, Encoding.UTF8);
            }
        }

        private void BtnSaveTargetsTxt_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(_core.UtilsPath, "targets.txt");
            try
            {
                File.WriteAllText(path, TxtTargetsContent.Text, Encoding.UTF8);
                _core.Log("[Targets] Файл targets.txt успешно сохранен.");
                MessageBox.Show("Файл targets.txt сохранен.", "Тестирование", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения targets.txt: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRunTestsPs1_Click(object sender, RoutedEventArgs e)
        {
            string ps1Path = Path.Combine(_core.UtilsPath, "test zapret.ps1");
            if (!File.Exists(ps1Path))
            {
                MessageBox.Show($"Файл теста не найден: {ps1Path}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _core.Log("[Test] Запуск тестового скрипта PowerShell в отдельном окне...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"",
                    WorkingDirectory = _core.ZapretRoot,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                _core.Log($"[ERROR] Не удалось запустить тест: {ex.Message}");
            }
        }

        private void BtnOpenTargetsTxt_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(_core.UtilsPath, "targets.txt");
            if (File.Exists(path))
            {
                Process.Start("notepad.exe", path);
            }
        }

        #endregion

        #region VPN (BETA) Management

        private void InitVpnTab()
        {
            _vpnProfilesView = CollectionViewSource.GetDefaultView(_vpnService.Profiles);
            _vpnProfilesView.Filter = FilterVpnProfile;
            ListVpnProfiles.ItemsSource = _vpnProfilesView;

            if (_vpnService.Profiles.Count > 0)
            {
                ListVpnProfiles.SelectedIndex = 0;
            }

            UpdateVpnStatus();
            UpdateVpnNodesCount();

            string? coreExe = _vpnService.FindCoreExecutable();
            if (coreExe != null)
            {
                TxtVpnCoreStatus.Text = $"Ядро: {Path.GetFileName(coreExe)} (в папке bin\\)";
                TxtVpnCoreStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            }
            else
            {
                TxtVpnCoreStatus.Text = "Ядро: не найдено (прямой / конфиг)";
                TxtVpnCoreStatus.Foreground = (SolidColorBrush)FindResource("TextMuted");
            }
        }

        private bool FilterVpnProfile(object obj)
        {
            if (obj is not VpnProfile profile) return false;
            string filter = TxtVpnFilter.Text.Trim();
            if (string.IsNullOrEmpty(filter)) return true;

            return profile.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                   profile.Server.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                   profile.ProtocolBadge.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                   profile.DetailsSummary.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private void TxtVpnFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _vpnProfilesView?.Refresh();
            UpdateVpnNodesCount();
        }

        private void UpdateVpnNodesCount()
        {
            int total = _vpnService.Profiles.Count;
            int visible = _vpnProfilesView?.Cast<object>().Count() ?? total;
            if (string.IsNullOrWhiteSpace(TxtVpnFilter.Text))
            {
                TxtVpnNodesCount.Text = $"{total} узлов";
            }
            else
            {
                TxtVpnNodesCount.Text = $"{visible} из {total} узлов";
            }
        }

        private void ListVpnProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListVpnProfiles.SelectedItem is VpnProfile selected)
            {
                TxtVpnActiveNodeName.Text = $"Выбранный узел: {selected.Name}";
                TxtVpnActiveNodeEndpoint.Text = $"Адрес: {selected.Server}:{selected.Port} | Протокол: {selected.ProtocolBadge} ({selected.DetailsSummary})";

                if (selected.HttpPingMs.HasValue && selected.HttpPingMs.Value >= 0)
                {
                    TxtVpnExitLatency.Text = $"{selected.HttpPingMs.Value} ms";
                    TxtVpnExitIp.Text = string.IsNullOrEmpty(selected.ExitIp) ? "—" : selected.ExitIp;
                    TxtVpnExitLocation.Text = selected.ExitLocationSummary;
                    TxtVpnExitOrg.Text = string.IsNullOrEmpty(selected.ExitOrg) ? "Готов к работе" : selected.ExitOrg;
                }
                else
                {
                    TxtVpnExitLatency.Text = selected.PingMs.HasValue && selected.PingMs.Value >= 0 ? $"{selected.PingMs.Value} ms" : "—";
                    TxtVpnExitIp.Text = "—";
                    TxtVpnExitLocation.Text = "—";
                    TxtVpnExitOrg.Text = "—";
                }
            }
            else if (_vpnService.ActiveProfile == null)
            {
                TxtVpnActiveNodeName.Text = "Выбранный узел: Не выбран";
                TxtVpnActiveNodeEndpoint.Text = "Адрес: — | Протокол: —";
                TxtVpnExitLatency.Text = "—";
                TxtVpnExitIp.Text = "—";
                TxtVpnExitLocation.Text = "—";
                TxtVpnExitOrg.Text = "—";
            }
        }

        private void UpdateVpnStatus()
        {
            string? coreExe = _vpnService.FindCoreExecutable();
            if (coreExe != null)
            {
                TxtVpnCoreStatus.Text = "Ядро: sing-box (готово)";
                TxtVpnCoreStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
            }
            else
            {
                TxtVpnCoreStatus.Text = "Ядро: автозагрузка при старте";
                TxtVpnCoreStatus.Foreground = (SolidColorBrush)FindResource("AccentYellow");
            }

            if (_vpnService.Status == VpnConnectionStatus.Connected)
            {
                TxtVpnConnectionStatus.Text = "VPN: ПОДКЛЮЧЕН";
                TxtVpnConnectionStatus.Foreground = (SolidColorBrush)FindResource("AccentGreen");
                BadgeVpnBetaTag.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                BtnVpnConnect.IsEnabled = false;
                BtnVpnDisconnect.IsEnabled = true;

                if (_vpnService.ActiveProfile != null)
                {
                    TxtVpnActiveNodeName.Text = $"Подключено к: {_vpnService.ActiveProfile.Name}";
                    TxtVpnActiveNodeEndpoint.Text = $"Адрес: {_vpnService.ActiveProfile.Server}:{_vpnService.ActiveProfile.Port} | Протокол: {_vpnService.ActiveProfile.ProtocolBadge} ({_vpnService.ActiveProfile.DetailsSummary})";

                    if (_vpnService.ActiveProfile.HttpPingMs.HasValue && _vpnService.ActiveProfile.HttpPingMs.Value >= 0)
                    {
                        TxtVpnExitLatency.Text = $"{_vpnService.ActiveProfile.HttpPingMs.Value} ms";
                        TxtVpnExitIp.Text = string.IsNullOrEmpty(_vpnService.ActiveProfile.ExitIp) ? "—" : _vpnService.ActiveProfile.ExitIp;
                        TxtVpnExitLocation.Text = _vpnService.ActiveProfile.ExitLocationSummary;
                        TxtVpnExitOrg.Text = string.IsNullOrEmpty(_vpnService.ActiveProfile.ExitOrg) ? "Подключено" : _vpnService.ActiveProfile.ExitOrg;
                    }
                }

                TxtGlobalVpnStatus.Text = "VPN: Активен";
                VpnStatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentGreen");
                VpnStatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("AccentGreen");
            }
            else if (_vpnService.Status == VpnConnectionStatus.Connecting)
            {
                TxtVpnConnectionStatus.Text = "VPN: ПОДКЛЮЧЕНИЕ...";
                TxtVpnConnectionStatus.Foreground = (SolidColorBrush)FindResource("AccentYellow");
                BtnVpnConnect.IsEnabled = false;
                BtnVpnDisconnect.IsEnabled = true;

                TxtGlobalVpnStatus.Text = "VPN: Подключение...";
                VpnStatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentYellow");
                VpnStatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("AccentYellow");
            }
            else
            {
                TxtVpnConnectionStatus.Text = "VPN: ОТКЛЮЧЕН";
                TxtVpnConnectionStatus.Foreground = (SolidColorBrush)FindResource("AccentRed");
                BadgeVpnBetaTag.Background = (SolidColorBrush)FindResource("AccentPurple");
                BtnVpnConnect.IsEnabled = true;
                BtnVpnDisconnect.IsEnabled = false;
                TxtVpnSessionDuration.Text = "Время работы сессии: —";

                if (ListVpnProfiles.SelectedItem is VpnProfile selected)
                {
                    TxtVpnActiveNodeName.Text = $"Выбранный узел: {selected.Name}";
                    TxtVpnActiveNodeEndpoint.Text = $"Адрес: {selected.Server}:{selected.Port} | Протокол: {selected.ProtocolBadge} ({selected.DetailsSummary})";

                    if (selected.HttpPingMs.HasValue && selected.HttpPingMs.Value >= 0)
                    {
                        TxtVpnExitLatency.Text = $"{selected.HttpPingMs.Value} ms";
                        TxtVpnExitIp.Text = string.IsNullOrEmpty(selected.ExitIp) ? "—" : selected.ExitIp;
                        TxtVpnExitLocation.Text = selected.ExitLocationSummary;
                        TxtVpnExitOrg.Text = string.IsNullOrEmpty(selected.ExitOrg) ? "Готов к работе" : selected.ExitOrg;
                    }
                }

                TxtGlobalVpnStatus.Text = "VPN: Отключен";
                VpnStatusIndicatorDot.Fill = (SolidColorBrush)FindResource("AccentRed");
                VpnStatusPillBorder.BorderBrush = (SolidColorBrush)FindResource("BorderCard");
            }

            RefreshStatus();
        }

        private async void BtnVpnImportLink_Click(object sender, RoutedEventArgs e)
        {
            string raw = TxtVpnLinkInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                MessageBox.Show("Вставьте ссылку в поле ввода.\n\nПоддерживаются протоколы: vless://, vmess://, ss://, trojan://, wireguard://, hysteria2://, socks5:// или прямая ссылка подписки https://...", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if it is a subscription URL
            if ((raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                !raw.Contains("@") && !raw.Contains(":80") && !raw.Contains(":8080") && !raw.Contains(":3128"))
            {
                await FetchSubscriptionInternal(raw);
                return;
            }

            var parsed = VpnService.ParseInput(raw);
            if (parsed.Count > 0)
            {
                _vpnService.AddProfiles(parsed);
                UpdateVpnNodesCount();
                ListVpnProfiles.SelectedItem = parsed[0];
                TxtVpnLinkInput.Clear();
                MessageBox.Show($"Успешно импортировано узлов: {parsed.Count}!\n\nУзлы добавлены в список. Выберите подходящий узел и нажмите 'Подключиться'.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Не удалось распознать формат ссылки.\nУбедитесь, что ссылка начинается с vless://, vmess://, ss://, trojan://, wireguard://, socks5:// или является валидной подпиской.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnVpnFetchSubscription_Click(object sender, RoutedEventArgs e)
        {
            string raw = TxtVpnLinkInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                MessageBox.Show("Вставьте URL-адрес подписки в поле ввода (например, https://example.com/sub/...).", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await FetchSubscriptionInternal(raw);
        }

        private async Task FetchSubscriptionInternal(string url)
        {
            BtnVpnFetchSubscription.IsEnabled = false;
            BtnVpnImportLink.IsEnabled = false;
            TxtStatusBar.Text = "[VPN-Beta] Загрузка подписки...";

            try
            {
                var (success, count, msg) = await _vpnService.FetchSubscriptionAsync(url);
                UpdateVpnNodesCount();
                if (success)
                {
                    TxtVpnLinkInput.Clear();
                    if (_vpnService.Profiles.Count > 0)
                    {
                        ListVpnProfiles.SelectedIndex = 0;
                    }
                    MessageBox.Show(msg, "VPN Подписка (Beta)", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(msg, "Ошибка загрузки подписки", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                BtnVpnFetchSubscription.IsEnabled = true;
                BtnVpnImportLink.IsEnabled = true;
                TxtStatusBar.Text = "Готово";
            }
        }

        private void BtnVpnPasteClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    TxtVpnLinkInput.Text = System.Windows.Clipboard.GetText().Trim();
                }
            }
            catch { }
        }

        private void BtnVpnClearInput_Click(object sender, RoutedEventArgs e)
        {
            TxtVpnLinkInput.Clear();
        }

        private async void BtnVpnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (ListVpnProfiles.SelectedItem is not VpnProfile selected)
            {
                MessageBox.Show("Выберите узел из списка перед подключением.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool enableSysProxy = ChkVpnSystemProxy.IsChecked == true;
            bool ok = await _vpnService.ConnectAsync(selected, enableSysProxy);
            if (ok)
            {
                _trayManager?.ShowNotification("Zapret VPN [BETA]", $"Подключено к узлу '{selected.Name}' ({selected.ProtocolBadge})", Forms.ToolTipIcon.Info);
            }
        }

        private async void BtnVpnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await _vpnService.DisconnectAsync();
            _trayManager?.ShowNotification("Zapret VPN [BETA]", "VPN отключен.", Forms.ToolTipIcon.Info);
        }

        private void BtnVpnCopyConfig_Click(object sender, RoutedEventArgs e)
        {
            var target = (_vpnService.ActiveProfile ?? ListVpnProfiles.SelectedItem as VpnProfile);
            if (target == null)
            {
                MessageBox.Show("Выберите узел в списке для экспорта конфигурации.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string json = VpnService.ExportProfileConfigJson(target);
            System.Windows.Clipboard.SetText(json);
            MessageBox.Show($"Конфигурация для узла '{target.Name}' в формате JSON скопирована в буфер обмена.\nВы можете использовать её в Xray, Sing-box или других клиентах.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnVpnTestAllPings_Click(object sender, RoutedEventArgs e)
        {
            if (_vpnService.Profiles.Count == 0)
            {
                MessageBox.Show("Список узлов пуст. Сначала добавьте ссылки или подписку.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnVpnTestAllPings.IsEnabled = false;
            BtnVpnTestAllPings.Content = "Проверка...";
            TxtStatusBar.Text = $"[VPN-Beta] Проверка пинга {_vpnService.Profiles.Count} узлов...";

            try
            {
                _pingCts = new CancellationTokenSource();
                await _vpnService.MeasureAllPingsAsync(ct: _pingCts.Token);
                _vpnProfilesView?.Refresh();
                TxtStatusBar.Text = "[VPN-Beta] Проверка пинга завершена.";
            }
            finally
            {
                BtnVpnTestAllPings.IsEnabled = true;
                BtnVpnTestAllPings.Content = "Пинг всех (TCP)";
            }
        }

        private async void BtnVpnPingSingle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is VpnProfile profile)
            {
                btn.IsEnabled = false;
                try
                {
                    await _vpnService.MeasurePingAsync(profile);
                    _vpnProfilesView?.Refresh();
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            }
        }

        private void BtnVpnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (ListVpnProfiles.SelectedItem is VpnProfile selected)
            {
                _vpnService.RemoveProfile(selected);
                UpdateVpnNodesCount();
            }
            else
            {
                MessageBox.Show("Выберите узел для удаления.", "VPN (Beta)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnVpnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_vpnService.Profiles.Count == 0) return;

            var res = MessageBox.Show("Вы уверены, что хотите удалить все сохраненные узлы VPN?", "Очистить список", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _vpnService.ClearProfiles();
                UpdateVpnNodesCount();
            }
        }

        private void TxtVpnLinkInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnVpnImportLink_Click(sender, e);
            }
        }

        private void ListVpnProfiles_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ListVpnProfiles.SelectedItem is VpnProfile)
            {
                BtnVpnConnect_Click(sender, e);
            }
        }

        private async void BtnVpnVerifyHttpGet_Click(object sender, RoutedEventArgs e)
        {
            var target = _vpnService.ActiveProfile ?? ListVpnProfiles.SelectedItem as VpnProfile;
            if (target == null)
            {
                MessageBox.Show("Выберите узел из списка или подключитесь к VPN перед проверкой через HTTP GET.", "Проверка прокси (GET)", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnVpnVerifyHttpGet.IsEnabled = false;
            string prevBtnText = BtnVpnVerifyHttpGet.Content?.ToString() ?? "Проверить (GET)";
            BtnVpnVerifyHttpGet.Content = "Проверка GET...";
            TxtStatusBar.Text = $"[VPN-Verify] Проверка узла '{target.Name}' через HTTP GET запрос...";

            try
            {
                var (success, latency, ip, country, city, org, msg) = await _vpnService.VerifyProxyViaHttpGetAsync(target);
                _vpnProfilesView?.Refresh();

                if (success)
                {
                    TxtVpnExitLatency.Text = $"{latency} ms";
                    TxtVpnExitIp.Text = ip;
                    TxtVpnExitLocation.Text = target.ExitLocationSummary;
                    TxtVpnExitOrg.Text = string.IsNullOrEmpty(org) ? "Подключено" : org;
                    TxtStatusBar.Text = $"[VPN-Verify] Проверка успешна: {ip}, {target.ExitLocationSummary}, {latency} ms";
                }
                else
                {
                    TxtVpnExitLatency.Text = "Ошибка / Таймаут";
                    TxtVpnExitIp.Text = "—";
                    TxtVpnExitLocation.Text = "Недоступен";
                    TxtVpnExitOrg.Text = msg;
                    TxtStatusBar.Text = $"[VPN-Verify] Ошибка проверки: {msg}";
                }
            }
            finally
            {
                BtnVpnVerifyHttpGet.IsEnabled = true;
                BtnVpnVerifyHttpGet.Content = prevBtnText;
            }
        }

        private async void BtnVpnVerifySingleHttpGet_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is VpnProfile profile)
            {
                btn.IsEnabled = false;
                string prevText = btn.Content?.ToString() ?? "GET";
                btn.Content = "...";
                try
                {
                    await _vpnService.VerifyProxyViaHttpGetAsync(profile);
                    _vpnProfilesView?.Refresh();
                }
                finally
                {
                    btn.IsEnabled = true;
                    btn.Content = prevText;
                }
            }
        }

        private async void BtnVpnVerifyAllHttpGet_Click(object sender, RoutedEventArgs e)
        {
            if (_vpnService.Profiles.Count == 0)
            {
                MessageBox.Show("Список узлов пуст. Добавьте узлы или подписку перед проверкой.", "Проверка прокси (GET)", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnVpnVerifyAllHttpGet.IsEnabled = false;
            string prevBtnText = BtnVpnVerifyAllHttpGet.Content?.ToString() ?? "Проверить все";
            BtnVpnVerifyAllHttpGet.Content = "Проверка GET...";
            TxtStatusBar.Text = $"[VPN-Verify] Проверка через HTTP GET для {_vpnService.Profiles.Count} узлов...";

            try
            {
                var progress = new Progress<int>(count =>
                {
                    TxtStatusBar.Text = $"[VPN-Verify] Проверено узлов: {count}/{_vpnService.Profiles.Count}";
                    _vpnProfilesView?.Refresh();
                });
                await _vpnService.VerifyAllProfilesViaHttpGetAsync(progress);
                _vpnProfilesView?.Refresh();
                TxtStatusBar.Text = "[VPN-Verify] Проверка всех узлов через HTTP GET завершена!";
                MessageBox.Show("Проверка всех узлов через HTTP GET успешно завершена!", "Проверка прокси (GET)", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                BtnVpnVerifyAllHttpGet.IsEnabled = true;
                BtnVpnVerifyAllHttpGet.Content = prevBtnText;
            }
        }

        private async void BtnVpnInstallCore_Click(object sender, RoutedEventArgs e)
        {
            BtnVpnInstallCore.IsEnabled = false;
            string prevText = BtnVpnInstallCore.Content?.ToString() ?? "Ядро sing-box";
            BtnVpnInstallCore.Content = "Установка...";
            TxtStatusBar.Text = "[VPN] Установка/обновление ядра sing-box...";

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    TxtStatusBar.Text = $"[VPN] {msg}";
                });
                var (success, msg) = await _vpnService.EnsureCoreInstalledAsync(progress);
                UpdateVpnStatus();
                MessageBox.Show(msg, "Ядро Sing-box", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            finally
            {
                BtnVpnInstallCore.IsEnabled = true;
                BtnVpnInstallCore.Content = prevText;
            }
        }

        private void BtnVpnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            TxtVpnFilter.Clear();
        }

        #endregion

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}

