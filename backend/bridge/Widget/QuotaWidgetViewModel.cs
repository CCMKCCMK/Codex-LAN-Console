using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace CodexLanBridge;

internal sealed class QuotaWidgetViewModel : INotifyPropertyChanged
{
    private static readonly Brush HealthyAccent = Freeze(new SolidColorBrush(Color.FromRgb(112, 239, 188)));
    private static readonly Brush WarningAccent = Freeze(new SolidColorBrush(Color.FromRgb(255, 196, 92)));
    private static readonly Brush CriticalAccent = Freeze(new SolidColorBrush(Color.FromRgb(255, 112, 120)));
    private static readonly Brush OfflineAccent = Freeze(new SolidColorBrush(Color.FromRgb(150, 158, 156)));

    private QuotaWidgetSnapshot _snapshot = QuotaWidgetSnapshot.Empty();
    private string _windowText = "CODEX";
    private string _remainingText = "--";
    private string _rateText = "正在连接";
    private string _etaText = "等待额度数据";
    private string _estimatorsText = "近 --  ·  稳 --  ·  均 --";
    private string _toolTipText = "正在读取 Codex 额度";
    private int _remainingPercent;
    private Brush _accentBrush = OfflineAccent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WindowText { get => _windowText; private set => Set(ref _windowText, value); }
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    public string EstimatorsText { get => _estimatorsText; private set => Set(ref _estimatorsText, value); }
    public string ToolTipText { get => _toolTipText; private set => Set(ref _toolTipText, value); }
    public int RemainingPercent { get => _remainingPercent; private set => Set(ref _remainingPercent, value); }
    public Brush AccentBrush { get => _accentBrush; private set => Set(ref _accentBrush, value); }

    public void Apply(QuotaWidgetSnapshot snapshot)
    {
        _snapshot = snapshot;
        RefreshClock();
    }

    public void RefreshClock()
    {
        var snapshot = _snapshot;
        var now = DateTimeOffset.UtcNow;
        if (!snapshot.Available || snapshot.Window is null)
        {
            WindowText = snapshot.Stale ? "CODEX · 离线" : "CODEX";
            RemainingText = "--";
            RateText = "正在连接";
            EtaText = "等待额度数据";
            EstimatorsText = "近 --  ·  稳 --  ·  均 --";
            ToolTipText = string.IsNullOrWhiteSpace(snapshot.Error)
                ? "Codex 尚未返回额度数据"
                : $"Codex 额度暂不可用\n{snapshot.Error}";
            RemainingPercent = 0;
            AccentBrush = OfflineAccent;
            return;
        }

        var window = snapshot.Window;
        var primary = snapshot.PrimaryEstimate;
        var elapsedSeconds = snapshot.UpdatedAt is { } updated
            ? Math.Max(0L, (long)(now - updated).TotalSeconds)
            : 0L;

        WindowText = $"CODEX · {window.Label}{(snapshot.Stale ? " · 旧" : "")}";
        RemainingPercent = Math.Clamp(window.RemainingPercent, 0, 100);
        RemainingText = $"{RemainingPercent}%";
        RateText = primary?.RatePercentPerHour is { } rate
            ? $"-{rate.ToString("0.#", CultureInfo.InvariantCulture)}% / h"
            : "速度采集中";
        EtaText = FormatPrimaryEstimate(primary, window, now, elapsedSeconds);
        EstimatorsText = snapshot.Estimators is { } estimates
            ? $"近 {FormatCompactEstimate(estimates.Recent, window, now, elapsedSeconds)}  ·  " +
              $"稳 {FormatCompactEstimate(estimates.Trend, window, now, elapsedSeconds)}  ·  " +
              $"均 {FormatCompactEstimate(estimates.WindowAverage, window, now, elapsedSeconds)}"
            : "近 --  ·  稳 --  ·  均 --";
        AccentBrush = RemainingPercent switch
        {
            <= 15 => CriticalAccent,
            <= 35 => WarningAccent,
            _ => HealthyAccent
        };

        var resetText = window.ResetsAt is { } reset
            ? DateTimeOffset.FromUnixTimeSeconds(reset).ToLocalTime().ToString("M-d HH:mm", CultureInfo.CurrentCulture)
            : "未知";
        ToolTipText = $"{window.Label} 额度剩余 {RemainingPercent}%\n" +
                      $"消耗速度 {RateText}\n{EtaText}\n" +
                      $"重置时间 {resetText}\n{EstimatorsText}";
    }

    private static string FormatPrimaryEstimate(
        QuotaEstimate? estimate,
        QuotaWindowView window,
        DateTimeOffset now,
        long elapsedSeconds)
    {
        if (estimate?.RatePercentPerHour is null) return "速度采集中";
        if (estimate.ReachesReset && SecondsToReset(window, now) is { } resetSeconds)
            return $"> {FormatDuration(resetSeconds)} 至重置";
        if (estimate.EtaSeconds is { } eta)
            return $"约 {FormatDuration(Math.Max(0, eta - elapsedSeconds))}";
        return "暂无耗尽风险";
    }

    private static string FormatCompactEstimate(
        QuotaEstimate estimate,
        QuotaWindowView window,
        DateTimeOffset now,
        long elapsedSeconds)
    {
        if (estimate.RatePercentPerHour is null) return "--";
        if (estimate.ReachesReset && SecondsToReset(window, now) is not null) return "到重置";
        return estimate.EtaSeconds is { } eta
            ? FormatDuration(Math.Max(0, eta - elapsedSeconds))
            : "--";
    }

    private static long? SecondsToReset(QuotaWindowView window, DateTimeOffset now) =>
        window.ResetsAt is { } reset ? Math.Max(0, reset - now.ToUnixTimeSeconds()) : null;

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return "<1m";
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)}m";
        if (span.TotalDays < 2) return $"{(int)span.TotalHours}h{span.Minutes:00}m";
        return $"{(int)span.TotalDays}d{span.Hours:00}h";
    }

    private static Brush Freeze(Freezable brush)
    {
        brush.Freeze();
        return (Brush)brush;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
