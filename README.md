# ZapretVPN (Flowseal Edition)

<p align="center">
  <img src="https://img.shields.io/github/v/release/bbqabsolute/zapret-gui?style=for-the-badge&color=6366f1" alt="Release">
  <img src="https://img.shields.io/badge/.NET-8.0_WPF-512bd4?style=for-the-badge&logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/Platform-Windows_10%20%2F%2011%20(x64)-0078d6?style=for-the-badge&logo=windows" alt="Platform">
  <img src="https://img.shields.io/badge/Status-Stable-10b981?style=for-the-badge" alt="Status">
</p>

Современный, удобный и производительный графический клиент **ZapretVPN** для обхода замедлений и блокировок YouTube, Discord и других сервисов через **Zapret**, со встроенным клиентом **VPN & Прокси** и автоматическим тестированием доступности.

> [!IMPORTANT]
> **Благодарности и основа проекта:**  
> Все встроенные стратегии обхода, фейковые пакеты (`.bin`), списки и пресеты взяты из популярной и надежной сборки **[Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)**.  
> Оригинальная утилита `winws` и проект Zapret созданы **[bol-van/zapret](https://github.com/bol-van/zapret)**.

---

## 📥 Скачать

Готовые сборки доступны на вкладке **[Releases](https://github.com/bbqabsolute/zapret-gui/releases/latest)**:

| Файл | Описание | Ссылка |
|---|---|---|
| 📦 **`ZapretVPN-v1.10.2.zip`** *(1.8 МБ)* | **Полный комплект (рекомендуется)**. Включает всё необходимое: `ZapretVPN.exe`, все 22 BAT-стратегии Flowseal, `winws.exe`, драйвер `WinDivert`, списки и фейки. Распакуйте и запускайте. | [Скачать ZIP](https://github.com/bbqabsolute/zapret-gui/releases/download/v1.10.2/ZapretVPN-v1.10.2.zip) |
| 🚀 **`ZapretVPN.exe`** *(390 КБ)* | Отдельный исполняемый файл приложения (для обновления существующей папки Zapret). | [Скачать EXE](https://github.com/bbqabsolute/zapret-gui/releases/download/v1.10.2/ZapretVPN.exe) |

---

## ✨ Основные возможности

### 🚀 1. Управление Zapret DPI (22 стратегии Flowseal)
- **Полная интеграция со сборкой Flowseal**:
  - `general` (базовый универсальный пресет)
  - `general (ALT)` ... `general (ALT13)` (альтернативные пресеты под разных провайдеров: МТС, Ростелеком, Билайн, Мегафон, Дом.ру и др.)
  - `general (FAKE TLS AUTO)`, `general (FAKE TLS AUTO ALT)` ... `ALT3` (автоматический подбор фейкового TLS ClientHello)
  - `general (SIMPLE FAKE)`, `general (SIMPLE FAKE ALT)` ... `ALT2`
  - `general (EXP)` (экспериментальные методы расщепления)
- **Два режима работы**:
  - **Автономный запуск (Standalone)** — запуск процесса в фоне для быстрого переключения и тестов.
  - **Системная служба Windows (Service)** — установка zapret в автозагрузку с автоматическим стартом при включении ПК без всплывающих окон.
- **Game Filter (Игровой фильтр)**:
  - Тонкая настройка фильтрации портов (1024-65535) для стабильной работы голосовых чатов Discord и онлайн-игр.
- **Режимы фильтрации IPSet**:
  - `ipset-all.txt`, выборочные списки или работа без ограничений по IP.
- **Подмена фейковых пакетов (`.bin`)**:
  - Мгновенный выбор активного TLS ClientHello и QUIC fake прямо из GUI.

---

### 🛡️ 2. Встроенный клиент VPN & Прокси (BETA)
Полноценный клиент для работы с защищенными прокси и VPN-туннелями:
- **Поддерживаемые протоколы**:
  - `VLESS` (с поддержкой Reality, XTLS-Vision, WebSocket, gRPC)
  - `VMess`
  - `Shadowsocks` (SIP002 и Legacy)
  - `Trojan`
  - `WireGuard`
  - `Hysteria2`
  - `SOCKS5` и `HTTP(S)` прокси
- **Гибкий импорт**:
  - Вставка ссылки из буфера обмена (любая ссылка вида `vless://...`, `vmess://...` и др.).
  - Пакетная вставка нескольких узлов (многострочный текст).
  - Автоматическое распознавание Base64-подписок и загрузка по URL (`https://...`).
  - Поддержка конфигураций Clash YAML.
- **Ядро sing-box**:
  - При первом подключении к протоколам туннелирования приложение само загружает официальное ядро `sing-box` из GitHub Releases.
- **Управление системным прокси Windows (WinINet)**:
  - Автоматическая маршрутизация трафика браузеров и системных программ с гарантированным безопасным сбросом при отключении или закрытии приложения.

---

### 🌐 3. Проверка прокси через HTTP GET
В отличие от обычного TCP-пинга, который показывает только доступность порта сервера:
- **Честный HTTP GET тест**: выполняет реальный сетевой запрос через туннель прокси к доверенным сервисам (`ipwho.is`, `cloudflare.com/cdn-cgi/trace`, `ipapi.co`, `api.ipify.org`).
- **Живые метрики**:
  - Задержка отклика (RTT пинг в миллисекундах).
  - Реальный внешний IP-адрес выхода.
  - Страна и город с флагом-эмодзи (например, 🇩🇪 Germany, Frankfurt).
  - Провайдер / организация (ASN / ISP).
- **Цветовая дифференциация**:
  - 🟢 **Зеленый (< 100 ms)** — отличное качество.
  - 🟡 **Желтый (100–300 ms)** — стандартное качество.
  - 🔴 **Красный (> 300 ms / Ошибка)** — высокий пинг или отсутствие доступа.

---

### 🎨 4. Дизайн и эргономика
- **Windows 11 Fluent Dark**: темная цветовая гамма, скругленные карточки, акцентные градиенты и четкая визуальная иерархия.
- **PerMonitorV2 DPI Aware**: корректное четкое масштабирование на любых мониторах (100%, 125%, 150%, 200%).
- **Системный трей**: сворачивание в трей при закрытии, быстрый статус и уведомления.
- **Встроенная диагностика (9 проверок)**:
  - Права администратора (UAC) с кнопкой быстрого перезапуска.
  - Наличие и целостность системного драйвера WinDivert.
  - Проверка службы базовой фильтрации (BFE).
  - Поиск конфликтующих VPN-сервисов и антивирусов.
- **Редактор списков**:
  - Встроенное редактирование `list-general-user.txt`, `list-exclude-user.txt`, `ipset-exclude-user.txt` прямо из интерфейса.

---

## 🛠️ Сборка из исходного кода

Приложение разработано на **C# / WPF (.NET 8.0)** под Windows 10/11 x64.

```powershell
# 1. Клонирование репозитория
git clone https://github.com/bbqabsolute/zapret-gui.git
cd zapret-gui

# 2. Запуск автоматических тестов (130 тестов)
dotnet run --project gui_tests/ZapretTests.csproj

# 3. Сборка единого EXE-файла без внешних зависимостей
dotnet publish gui_src/ZapretGUI.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false -o .
```

---

## 📜 Благодарности и лицензии
- **[Flowseal](https://github.com/Flowseal/zapret-discord-youtube)** — за подборку лучших стратегий, скриптов, аргументов и активную поддержку сообщества.
- **[bol-van](https://github.com/bol-van/zapret)** — автор оригинального комплекса Zapret и утилиты winws.
- **[SagerNet/sing-box](https://github.com/SagerNet/sing-box)** — универсальное сетевое ядро для VPN-туннелей.
- **[basil00/WinDivert](https://github.com/basil00/WinDivert)** — драйвер перехвата и модификации сетевых пакетов в Windows.

---

## ⚠️ Отказ от ответственности
Данное программное обеспечение создано исключительно в ознакомительных и исследовательских целях. Разработчики не несут ответственности за использование программы пользователями.
