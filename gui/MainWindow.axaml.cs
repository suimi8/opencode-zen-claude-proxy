using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ZenProxyUI;

public record LogEntry(string Display, string Color);

public partial class MainWindow : Window
{
    const string C_SYS = "#1A73E8";
    const string C_OK = "#349A50";
    const string C_ERR = "#D9534F";
    const string C_GRY = "#787E8B";
    const string C_DARK = "#343A46";

    readonly ObservableCollection<LogEntry> _logs = new();
    Process? _proc;
    DateTime _startedAt;
    readonly DispatcherTimer _timer;
    bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logs;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_proc != null && !_proc.HasExited)
                InfoText.Text = "端口 4050 · 模型 " + ProxyCore.GetEnv("UPSTREAM_MODEL") + " · 已运行 " + (DateTime.Now - _startedAt).ToString(@"hh\:mm\:ss");
        };

        Opened += (_, _) =>
        {
            if (PortInUse())
            {
                SetStatus("代理已在运行", C_OK);
                Append("检测到代理已在端口 4050 运行", C_GRY);
            }
            else
            {
                Append("代理未运行,自动启动...", C_GRY);
                StartProxy();
            }
        };

        Closing += (_, _) =>
        {
            _closing = true;
            if (_proc != null && !_proc.HasExited)
            {
                Append("窗口关闭,停止代理", C_GRY);
                StopProxy();
            }
        };
    }

    // ---------- UI helpers ----------
    void SetStatus(string text, string color)
    {
        StatusText.Text = "状态: " + text;
        StatusText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));
    }

    void Append(string line, string color)
    {
        if (_closing) return;
        _logs.Add(new LogEntry("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line, color));
        while (_logs.Count > 3000) _logs.RemoveAt(0);
        LogList.ScrollIntoView(_logs[^1]);
    }

    void ParseProxyLine(string line)
    {
        if (line.StartsWith("{"))
        {
            try
            {
                var json = new Dictionary<string, string>();
                foreach (Match mm in Regex.Matches(line, "\"([a-z_]+)\"\\s*:\\s*\"?([^\"}]*)\"?"))
                    json[mm.Groups[1].Value] = mm.Groups[2].Value;
                string ev = json["event"];
                if (ev == "proxy_listening")
                {
                    SetStatus("代理运行中", C_OK);
                    Append("✓ 代理已启动 (端口 " + json["port"] + ", 模型 " + json["upstream_model"] + ")", C_OK);
                    _startedAt = DateTime.Now;
                    _timer.Start();
                    return;
                }
                if (ev == "request")
                {
                    int status = int.Parse(json["status"]);
                    Append(json["method"] + " " + json["path"] + " → " + status + " (" + json["ms"] + "ms)", status >= 400 ? C_ERR : C_GRY);
                    return;
                }
            }
            catch { }
        }
        if (line.StartsWith("[err]"))
            Append(line, C_ERR);
        else if (!string.IsNullOrWhiteSpace(line))
            Append(line, C_GRY);
    }

    // ---------- proxy lifecycle ----------
    bool PortInUse()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 4050); return true; }
        catch { return false; }
    }

    void OnStartClick(object? sender, RoutedEventArgs e) => StartProxy();

    void OnStopClick(object? sender, RoutedEventArgs e) => StopProxy();

    void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _logs.Clear();
        Append("日志已清空", C_GRY);
    }

    void OnPinClick(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinBtn.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Topmost ? "#1A73E8" : "#6C757D"));
    }

    async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow();
        var ok = await settings.ShowDialog<bool>(this);
        if (ok)
        {
            var d = ProxyCore.LoadEnv();
            d["UPSTREAM_API_KEY"] = settings.ApiKey.Trim();
            d["UPSTREAM_MODEL"] = settings.Model.Trim();
            d["UPSTREAM_CHAT_COMPLETIONS_URL"] = settings.BaseUrl.Trim();
            ProxyCore.SaveEnv(d);
            if (settings.CcSyncApplied)
            {
                ProxyCore.SetCcSwitchKey(settings.ApiKey.Trim());
                ProxyCore.SetClaudeSettingsKey(settings.ApiKey.Trim());
                Append("配置已保存并同步 CC Switch (模型: " + d["UPSTREAM_MODEL"] + ")", C_OK);
            }
            else
            {
                Append("配置已保存 (模型: " + d["UPSTREAM_MODEL"] + ")", C_OK);
            }
            if (_proc != null && !_proc.HasExited)
            {
                Append("配置已变更,自动重启代理...", C_GRY);
                StopProxy();
                StartProxy();
            }
            else
            {
                Append("下次启动代理时生效", C_GRY);
            }
        }
    }

    void StartProxy()
    {
        if (_proc != null && !_proc.HasExited) return;
        if (PortInUse()) { SetStatus("代理已在运行", C_OK); return; }
        if (!File.Exists(ProxyCore.ProxyExePath))
        {
            Append("未找到代理程序: " + ProxyCore.ProxyExePath, C_ERR);
            SetStatus("启动失败", C_ERR);
            return;
        }

        try
        {
            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(ProxyCore.ProxyExePath)
                {
                    WorkingDirectory = ProxyCore.AppDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            _proc.OutputDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Dispatcher.UIThread.Post(() => ParseProxyLine(ev.Data)); };
            _proc.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) Dispatcher.UIThread.Post(() => ParseProxyLine("[err] " + ev.Data)); };
            _proc.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _timer.Stop();
                StartBtn.IsEnabled = true;
                StopBtn.IsEnabled = false;
                SetStatus("代理已退出", C_ERR);
                Append("代理进程已退出", C_GRY);
                _proc = null;
            });
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            StartBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
            SetStatus("代理启动中...", C_SYS);
            Append("正在启动代理...", C_GRY);
        }
        catch (Exception ex)
        {
            Append("启动失败: " + ex.Message, C_ERR);
            SetStatus("启动失败", C_ERR);
        }
    }

    void StopProxy()
    {
        try
        {
            if (_proc != null && !_proc.HasExited) { _proc.Kill(); _proc.WaitForExit(3000); }
            _proc = null;
        }
        catch { }
        _timer.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        SetStatus("已停止", C_GRY);
        Append("代理已停止", C_GRY);
    }
}
