using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZenProxyUI;

public class SettingsWindow : Window
{
    readonly TextBox _keyBox, _modelBox, _urlBox;
    public string ApiKey = "", Model = "", BaseUrl = "";
    public bool CcSyncApplied;

    public SettingsWindow()
    {
        Title = "代理设置";
        Width = 560;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var d = ProxyCore.LoadEnv();
        string providerName = ProxyCore.GetCcSwitchProviderName();
        string ccKey = ProxyCore.GetCcSwitchKey();
        bool ccOk = ProxyCore.CcSwitchAvailable && !string.IsNullOrEmpty(providerName) && !string.IsNullOrEmpty(ccKey);
        string key = ccOk ? ccKey : (d.TryGetValue("UPSTREAM_API_KEY", out var k) ? k : "");
        string model = d.TryGetValue("UPSTREAM_MODEL", out var mo) ? mo : "deepseek-v4-flash-free";
        string url = d.TryGetValue("UPSTREAM_CHAT_COMPLETIONS_URL", out var u) ? u : "https://opencode.ai/zen/v1/chat/completions";

        _keyBox = MakeTextBox(key);
        _modelBox = MakeTextBox(model);
        _urlBox = MakeTextBox(url);

        var copyBtn = MakeButton("复制", "#5E708D");
        copyBtn.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard != null)
            {
                await top.Clipboard.SetTextAsync(_keyBox.Text);
                copyBtn.Content = "已复制";
                copyBtn.Background = new SolidColorBrush(Color.Parse("#34A853"));
            }
        };

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        keyRow.Children.Add(_keyBox);
        keyRow.Children.Add(copyBtn);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        grid.Children.Add(MakeLabel("API Key (同步 CC Switch)"));
        grid.Children.Add(keyRow);
        Grid.SetRow(keyRow, 0);
        Grid.SetColumn(keyRow, 1);

        grid.Children.Add(MakeLabel("模型 (Model)"));
        grid.Children.Add(_modelBox);
        Grid.SetRow(_modelBox, 1);
        Grid.SetColumn(_modelBox, 1);

        grid.Children.Add(MakeLabel("接口地址 (Base URL)"));
        grid.Children.Add(_urlBox);
        Grid.SetRow(_urlBox, 2);
        Grid.SetColumn(_urlBox, 2);

        var statusText = ccOk
            ? "✓ 已读取 CC Switch 当前 provider: " + providerName + ",保存后三方同步"
            : ProxyCore.IsWindows && !ProxyCore.CcSwitchAvailable
                ? "未找到 sqlite3 或 CC Switch 数据库,Key 仅写入 .env.local"
                : "非 Windows 环境,Key 仅写入 .env.local";
        var status = new TextBlock { Text = statusText, Foreground = new SolidColorBrush(ccOk ? Color.Parse("#349A50") : Color.Parse("#D9534F")), Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(status);
        Grid.SetColumnSpan(status, 2);
        Grid.SetRow(status, 3);

        var hint = new TextBlock { Text = "Key 需与 OpenCode Zen 账号一致;修改后自动重启代理生效", Foreground = new SolidColorBrush(Color.Parse("#787E8B")), Margin = new Thickness(0, 0, 0, 8) };

        var saveBtn = MakeButton("保存", "#34A853");
        saveBtn.Click += (_, _) =>
        {
            ApiKey = _keyBox.Text;
            Model = _modelBox.Text;
            BaseUrl = _urlBox.Text;
            CcSyncApplied = ccOk;
            Close(true);
        };
        var cancelBtn = MakeButton("取消", "#6C757D");
        cancelBtn.Click += (_, _) => Close(false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveBtn);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(grid);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        Content = panel;
    }

    TextBox MakeTextBox(string value) => new() { Text = value, FontFamily = new FontFamily("Consolas, Menlo, monospace"), MinWidth = 320 };

    TextBlock MakeLabel(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#2D343F")) };

    Button MakeButton(string text, string color) => new() { Content = text, Background = new SolidColorBrush(Color.Parse(color)), Foreground = Brushes.White, Padding = new Thickness(18, 6) };
}
