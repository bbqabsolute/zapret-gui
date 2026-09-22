using System;
using System.Collections.Generic;

namespace ZapretGUI
{
    public class StrategyInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string RawArguments { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsRecommended { get; set; } = true;

        public override string ToString() => Name;
    }

    public enum GameFilterMode
    {
        Disabled,
        All,
        TcpOnly,
        UdpOnly
    }

    public enum IPSetMode
    {
        Loaded,
        None,
        Any
    }

    public enum ZapretStatus
    {
        Stopped,
        RunningStandalone,
        RunningService,
        Starting,
        Stopping,
        Error
    }

    public enum DiagnosticSeverity
    {
        Pass,
        Warning,
        Error,
        Info
    }

    public class DiagnosticItem
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DiagnosticSeverity Severity { get; set; } = DiagnosticSeverity.Info;
        public string Details { get; set; } = string.Empty;
        public string FixActionName { get; set; } = string.Empty;

        public string SeverityIcon => Severity switch
        {
            DiagnosticSeverity.Pass => "✅",
            DiagnosticSeverity.Warning => "⚠️",
            DiagnosticSeverity.Error => "❌",
            _ => "ℹ️"
        };
    }

    public class FakeBinFile
    {
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string Sha256Hash { get; set; } = string.Empty;

        public override string ToString() => FileName;
    }
}
