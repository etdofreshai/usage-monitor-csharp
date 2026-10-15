using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UsageMonitor.Services;

namespace UsageMonitor;

public partial class UsagePopup
{
    public event Action? AccountsChanged;
    private IReadOnlyList<UsageAccount> _additionalAccounts = Array.Empty<UsageAccount>();
    public IReadOnlyList<UsageAccount> AdditionalAccountToggles => _additionalAccounts;
    public bool IsAccountVisible(string id) => _config.AccountVisibility == null || !_config.AccountVisibility.TryGetValue(id, out var show) || show;

    public void SetAccountVisible(string id, bool visible)
    {
        (_config.AccountVisibility ??= new())[id] = visible;
        _config.Save();
        ApplyAdditionalAccounts(_additionalAccounts);
    }

    private void ApplyAdditionalAccounts(IReadOnlyList<UsageAccount>? accounts)
    {
        accounts ??= Array.Empty<UsageAccount>();
        var changed = !_additionalAccounts.Select(a => (a.Id, a.Name)).SequenceEqual(accounts.Select(a => (a.Id, a.Name)));
        _additionalAccounts = accounts;
        AdditionalAccountsFull.Children.Clear();
        AdditionalAccountsCompact.Children.Clear();
        foreach (var account in accounts)
        {
            if (!IsAccountVisible(account.Id)) continue;
            var codex = account.Key.StartsWith("codex", StringComparison.Ordinal);
            var number = account.Key[(codex ? 5 : 6)..];
            var label = (codex ? "Codex" : "Claude") + number;
            var windows = new List<(string Label, UsageWindow Window)>();
            if (account.Codex is { } cx)
            {
                if (cx.Primary != null) windows.Add(("5h", cx.Primary));
                windows.Add(("7d", cx.Secondary));
                if (_config.ShowCodexSpark && cx.SparkPrimary != null) windows.Add(("Sp5h", cx.SparkPrimary));
                if (_config.ShowCodexSpark && cx.SparkSecondary != null) windows.Add(("Sp7d", cx.SparkSecondary));
            }
            if (account.Claude is { } cc)
            {
                windows.Add(("5h", cc.FiveHour));
                windows.Add(("7d", cc.SevenDay));
                if (_config.ShowClaudeFable && cc.SevenDayFable != null) windows.Add(("Fab", cc.SevenDayFable));
                if (_config.ShowClaudeDesign && cc.SevenDayDesign != null) windows.Add(("Des", cc.SevenDayDesign));
            }
            if (windows.Count == 0) continue;
            var exhausted = windows.Any(w => w.Window.UsedPercent >= 100);
            var color = exhausted ? ExhaustedColor : Color.Parse(codex ? "#64B5F6" : "#FFB74D");
            var plan = account.Codex?.PlanType ?? account.Claude?.SubscriptionType;
            var resets = account.Codex?.Resets ?? account.Claude?.Resets;
            var full = new StackPanel { Spacing = 3 };
            var title = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(color) };
            SetTitleWithPlan(title, (codex ? "Codex #" : "Claude #") + number, plan, resets);
            ToolTip.SetTip(title, account.Name + "\n" + ResetTooltip(resets));
            full.Children.Add(title);
            MakeLink(title, codex ? _config.CodexUsageUrl : _config.ClaudeUsageUrl);
            var compact = new DockPanel { LastChildFill = true };
            var divider = new Border { Height = 1, Background = Brush.Parse("#2A2A2A"), Margin = new Thickness(0, 1, 0, 3) };
            DockPanel.SetDock(divider, Dock.Top);
            compact.Children.Add(divider);
            var planText = new TextBlock { Width = 58, FontSize = 8, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0), Foreground = Brush.Parse("#888888") };
            SetCompactPlan(planText, plan, resets);
            DockPanel.SetDock(planText, Dock.Right);
            compact.Children.Add(planText);
            var rows = new StackPanel { Spacing = 1 };
            compact.Children.Add(rows);
            ToolTip.SetTip(compact, account.Name);
            foreach (var (name, window) in windows)
            {
                rows.Children.Add(AccountUsageRow(label + " " + name, window, color));
                full.Children.Add(AccountUsageRow(name, window, color));
            }
            WireCompactRowLinks(rows, codex ? _config.CodexUsageUrl : _config.ClaudeUsageUrl);
            var balance = account.Codex?.CreditsBalance;
            if (decimal.TryParse(balance, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                balance = amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(balance))
            {
                rows.Children.Add(new TextBlock { Text = label + " CR  " + balance, FontSize = 9, Foreground = new SolidColorBrush(color) });
                full.Children.Add(new TextBlock { Text = "Credits  " + balance, FontSize = 9, Foreground = new SolidColorBrush(color) });
            }
            if (exhausted)
                foreach (var text in new[] { title, planText })
                {
                    text.Foreground = new SolidColorBrush(color);
                    if (text.Inlines != null)
                        foreach (var inline in text.Inlines) inline.ClearValue(Avalonia.Controls.Documents.TextElement.ForegroundProperty);
                }
            AdditionalAccountsCompact.Children.Add(compact);
            AdditionalAccountsFull.Children.Add(new Border { Child = full, Background = Brush.Parse("#222222"),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(8) });
        }
        if (AdditionalAccountsFull.Parent is Control fullHost) fullHost.IsVisible = AdditionalAccountsFull.Children.Count > 0;
        if (AdditionalAccountsCompact.Parent is Control compactHost) compactHost.IsVisible = AdditionalAccountsCompact.Children.Count > 0;
        if (changed) AccountsChanged?.Invoke();
    }

    private static Grid AccountUsageRow(string label, UsageWindow window, Color color)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("68,68,42,*"), MinHeight = 13 };
        row.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        var bar = new Grid { Width = 62, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        bar.Children.Add(new Border { Background = Brush.Parse("#333333"), CornerRadius = new CornerRadius(2) });
        bar.Children.Add(new Border { Background = new SolidColorBrush(color), Width = 62 * Math.Clamp(window.UsedPercent, 0, 100) / 100,
            HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) });
        if (window.ExpectedPercent is { } expected)
            bar.Children.Add(new Border { Background = Brushes.White, Width = 2, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(60 * Math.Clamp(expected, 0, 100) / 100, 0, 0, 0) });
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        var percent = new TextBlock { FontSize = 8, Foreground = new SolidColorBrush(color), TextAlignment = TextAlignment.Right };
        SetUsedExpectedInlines(percent, window.UsedPercent, color == ExhaustedColor ? null : window.ExpectedPercent);
        Grid.SetColumn(percent, 2);
        row.Children.Add(percent);
        var reset = new TextBlock { Text = label.EndsWith("5h") ? FormatResetCountdown(window.ResetsAt) : FormatResetDate(window.ResetsAt),
            FontSize = 8, Foreground = color == ExhaustedColor ? new SolidColorBrush(color) : Brush.Parse("#888888"), TextAlignment = TextAlignment.Right };
        SetResetTimesTooltip(reset, ("Resets", window.ResetsAt));
        Grid.SetColumn(reset, 3);
        row.Children.Add(reset);
        return row;
    }
}
