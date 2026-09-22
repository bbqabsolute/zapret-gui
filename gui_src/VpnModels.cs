using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ZapretGUI
{
    public enum VpnProtocol
    {
        Vless,
        Vmess,
        Shadowsocks,
        Trojan,
        WireGuard,
        Hysteria2,
        Socks5,
        Http,
        Unknown
    }

    public enum VpnConnectionStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting,
        Error
    }

    public class VpnProfile : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = "Новый узел";
        private VpnProtocol _protocol = VpnProtocol.Unknown;
        private string _server = string.Empty;
        private int _port = 443;
        private string _rawLink = string.Empty;
        private string _security = "tls";
        private string _sni = string.Empty;
        private string _uuid = string.Empty;
        private string _password = string.Empty;
        private string _method = string.Empty;
        private string _path = string.Empty;
        private string _networkType = "tcp";
        private string _publicKey = string.Empty;
        private string _shortId = string.Empty;
        private string _flow = string.Empty;
        private string _fingerprint = string.Empty;
        private string _alpn = string.Empty;
        private int? _pingMs = null;
        private string _pingStatus = "Не проверено";
        private int? _httpPingMs = null;
        private string _exitIp = string.Empty;
        private string _exitCountry = string.Empty;
        private string _exitCountryCode = string.Empty;
        private string _exitCity = string.Empty;
        private string _exitOrg = string.Empty;
        private string _httpCheckStatus = "Не проверено";
        private bool _isSelected = false;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public VpnProtocol Protocol
        {
            get => _protocol;
            set
            {
                _protocol = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProtocolBadge));
                OnPropertyChanged(nameof(ProtocolBadgeBg));
            }
        }

        public string Server
        {
            get => _server;
            set
            {
                _server = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndpointSummary));
            }
        }

        public int Port
        {
            get => _port;
            set
            {
                _port = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EndpointSummary));
            }
        }

        public string RawLink
        {
            get => _rawLink;
            set { _rawLink = value; OnPropertyChanged(); }
        }

        public string Security
        {
            get => _security;
            set { _security = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailsSummary)); }
        }

        public string Sni
        {
            get => _sni;
            set { _sni = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailsSummary)); }
        }

        public string Uuid
        {
            get => _uuid;
            set { _uuid = value; OnPropertyChanged(); }
        }

        public string Password
        {
            get => _password;
            set { _password = value; OnPropertyChanged(); }
        }

        public string Method
        {
            get => _method;
            set { _method = value; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); }
        }

        public string NetworkType
        {
            get => _networkType;
            set { _networkType = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailsSummary)); }
        }

        public string PublicKey
        {
            get => _publicKey;
            set { _publicKey = value; OnPropertyChanged(); }
        }

        public string ShortId
        {
            get => _shortId;
            set { _shortId = value; OnPropertyChanged(); }
        }

        public string Flow
        {
            get => _flow;
            set { _flow = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailsSummary)); }
        }

        public string Fingerprint
        {
            get => _fingerprint;
            set { _fingerprint = value; OnPropertyChanged(); }
        }

        public string Alpn
        {
            get => _alpn;
            set { _alpn = value; OnPropertyChanged(); }
        }

        public int? PingMs
        {
            get => _pingMs;
            set
            {
                _pingMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PingDisplay));
                OnPropertyChanged(nameof(PingBadgeBg));
                OnPropertyChanged(nameof(PingBadgeFg));
            }
        }

        public string PingStatus
        {
            get => _pingStatus;
            set
            {
                _pingStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PingDisplay));
                OnPropertyChanged(nameof(PingBadgeBg));
                OnPropertyChanged(nameof(PingBadgeFg));
            }
        }

        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string ProtocolBadge => Protocol switch
        {
            VpnProtocol.Vless => "VLESS",
            VpnProtocol.Vmess => "VMESS",
            VpnProtocol.Shadowsocks => "SS",
            VpnProtocol.Trojan => "TROJAN",
            VpnProtocol.WireGuard => "WIREGUARD",
            VpnProtocol.Hysteria2 => "HY2",
            VpnProtocol.Socks5 => "SOCKS5",
            VpnProtocol.Http => "HTTP",
            _ => "VPN"
        };

        [JsonIgnore]
        public string ProtocolBadgeBg => Protocol switch
        {
            VpnProtocol.Vless => "#6366F1",
            VpnProtocol.Vmess => "#3B82F6",
            VpnProtocol.Shadowsocks => "#10B981",
            VpnProtocol.Trojan => "#F59E0B",
            VpnProtocol.WireGuard => "#8B5CF6",
            VpnProtocol.Hysteria2 => "#EC4899",
            VpnProtocol.Socks5 => "#06B6D4",
            VpnProtocol.Http => "#64748B",
            _ => "#4B5563"
        };

        [JsonIgnore]
        public string EndpointSummary => $"{Server}:{Port}";

        [JsonIgnore]
        public string DetailsSummary
        {
            get
            {
                string sec = string.IsNullOrEmpty(Security) ? "none" : Security;
                string sni = string.IsNullOrEmpty(Sni) ? "" : $" • SNI: {Sni}";
                string net = string.IsNullOrEmpty(NetworkType) ? "" : $" • {NetworkType.ToUpperInvariant()}";
                return $"{sec.ToUpperInvariant()}{net}{sni}";
            }
        }

        [JsonIgnore]
        public string PingDisplay
        {
            get
            {
                if (PingMs.HasValue)
                {
                    return PingMs.Value >= 0 ? $"{PingMs.Value} ms" : "Таймаут";
                }
                return PingStatus;
            }
        }

        [JsonIgnore]
        public string PingBadgeBg
        {
            get
            {
                if (!PingMs.HasValue) return "#262930";
                if (PingMs.Value < 0) return "#3D1B1F";
                if (PingMs.Value <= 120) return "#143826";
                if (PingMs.Value <= 250) return "#382D14";
                return "#3D1B1F";
            }
        }

        [JsonIgnore]
        public string PingBadgeFg
        {
            get
            {
                if (!PingMs.HasValue) return "#9CA3AF";
                if (PingMs.Value < 0) return "#F87171";
                if (PingMs.Value <= 120) return "#34D399";
                if (PingMs.Value <= 250) return "#FBBF24";
                return "#F87171";
            }
        }

        public int? HttpPingMs
        {
            get => _httpPingMs;
            set
            {
                _httpPingMs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HttpCheckDisplay));
                OnPropertyChanged(nameof(HttpCheckBadgeBg));
                OnPropertyChanged(nameof(HttpCheckBadgeFg));
            }
        }

        public string ExitIp
        {
            get => _exitIp;
            set
            {
                _exitIp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExitLocationSummary));
                OnPropertyChanged(nameof(HttpCheckDisplay));
            }
        }

        public string ExitCountry
        {
            get => _exitCountry;
            set
            {
                _exitCountry = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExitLocationSummary));
                OnPropertyChanged(nameof(HttpCheckDisplay));
            }
        }

        public string ExitCountryCode
        {
            get => _exitCountryCode;
            set
            {
                _exitCountryCode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExitLocationSummary));
                OnPropertyChanged(nameof(HttpCheckDisplay));
            }
        }

        public string ExitCity
        {
            get => _exitCity;
            set
            {
                _exitCity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ExitLocationSummary));
            }
        }

        public string ExitOrg
        {
            get => _exitOrg;
            set { _exitOrg = value; OnPropertyChanged(); }
        }

        public string HttpCheckStatus
        {
            get => _httpCheckStatus;
            set
            {
                _httpCheckStatus = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HttpCheckDisplay));
                OnPropertyChanged(nameof(HttpCheckBadgeBg));
                OnPropertyChanged(nameof(HttpCheckBadgeFg));
            }
        }

        [JsonIgnore]
        public string ExitLocationSummary
        {
            get
            {
                if (!string.IsNullOrEmpty(ExitCountry))
                {
                    string flag = GetFlagEmoji(ExitCountryCode);
                    string city = string.IsNullOrEmpty(ExitCity) ? "" : $" ({ExitCity})";
                    return $"{flag} {ExitCountry}{city}";
                }
                if (!string.IsNullOrEmpty(ExitIp))
                {
                    return ExitIp;
                }
                return "—";
            }
        }

        [JsonIgnore]
        public string HttpCheckDisplay
        {
            get
            {
                if (HttpPingMs.HasValue)
                {
                    if (HttpPingMs.Value >= 0)
                    {
                        string loc = !string.IsNullOrEmpty(ExitCountry)
                            ? $"{GetFlagEmoji(ExitCountryCode)} {ExitCountry}"
                            : (!string.IsNullOrEmpty(ExitIp) ? ExitIp : "");
                        return string.IsNullOrEmpty(loc)
                            ? $"{HttpPingMs.Value} ms"
                            : $"{HttpPingMs.Value} ms • {loc}";
                    }
                    return "Ошибка GET";
                }
                return HttpCheckStatus;
            }
        }

        [JsonIgnore]
        public string HttpCheckBadgeBg
        {
            get
            {
                if (!HttpPingMs.HasValue) return "#1F232D";
                if (HttpPingMs.Value < 0) return "#3D1B1F";
                if (HttpPingMs.Value <= 350) return "#143826";
                if (HttpPingMs.Value <= 800) return "#382D14";
                return "#3D1B1F";
            }
        }

        [JsonIgnore]
        public string HttpCheckBadgeFg
        {
            get
            {
                if (!HttpPingMs.HasValue) return "#9CA3AF";
                if (HttpPingMs.Value < 0) return "#F87171";
                if (HttpPingMs.Value <= 350) return "#34D399";
                if (HttpPingMs.Value <= 800) return "#FBBF24";
                return "#F87171";
            }
        }

        public static string GetCountryName(string? countryCode)
        {
            if (string.IsNullOrEmpty(countryCode)) return "";
            return countryCode.ToUpperInvariant() switch
            {
                "RU" => "Россия",
                "DE" => "Германия",
                "US" => "США",
                "NL" => "Нидерланды",
                "FI" => "Финляндия",
                "SE" => "Швеция",
                "GB" or "UK" => "Великобритания",
                "FR" => "Франция",
                "PL" => "Польша",
                "KZ" => "Казахстан",
                "TR" => "Турция",
                "UA" => "Украина",
                "BY" => "Беларусь",
                "MD" => "Молдова",
                "GE" => "Грузия",
                "AM" => "Армения",
                "CH" => "Швейцария",
                "AT" => "Австрия",
                "JP" => "Япония",
                "SG" => "Сингапур",
                "HK" => "Гонконг",
                "CA" => "Канада",
                "IT" => "Италия",
                "ES" => "Испания",
                "CZ" => "Чехия",
                "LV" => "Латвия",
                "LT" => "Литва",
                "EE" => "Эстония",
                _ => countryCode.ToUpperInvariant()
            };
        }

        public static string GetFlagEmoji(string? countryCode)
        {
            if (string.IsNullOrEmpty(countryCode) || countryCode.Length != 2) return "🌐";
            countryCode = countryCode.ToUpperInvariant();
            if (countryCode[0] < 'A' || countryCode[0] > 'Z' || countryCode[1] < 'A' || countryCode[1] > 'Z')
                return "🌐";
            int first = 0x1F1E6 + (countryCode[0] - 'A');
            int second = 0x1F1E6 + (countryCode[1] - 'A');
            return char.ConvertFromUtf32(first) + char.ConvertFromUtf32(second);
        }

        public override string ToString() => $"{Name} ({EndpointSummary})";
    }
}
