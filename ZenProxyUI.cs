using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

class ZenProxyUI : Form
{
    Process proc;
    RichTextBox logBox;
    Button startBtn, stopBtn, clearBtn, pinBtn, settingsBtn;
    Label statusLbl, infoLbl;
    Timer uptimeTimer;
    DateTime startedAt;
    bool closing;

    static readonly Color C_BG = Color.FromArgb(250, 250, 252);
    static readonly Color C_HEADER = Color.FromArgb(255, 255, 255);
    static readonly Color C_GREEN = Color.FromArgb(52, 168, 83);
    static readonly Color C_RED = Color.FromArgb(217, 83, 79);
    static readonly Color C_BLUE = Color.FromArgb(26, 115, 232);
    static readonly Color C_GRAY = Color.FromArgb(120, 126, 139);
    static readonly Color C_DARK = Color.FromArgb(45, 52, 63);

    static string EnvPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env.local"); } }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ZenProxyUI());
    }

    ZenProxyUI()
    {
        Text = "ZenProxy";
        ClientSize = new Size(880, 540);
        MinimumSize = new Size(640, 380);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = C_BG;
        Font = new Font("Microsoft YaHei UI", 9F);

        // ---------- toolbar ----------
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = C_HEADER,
            WrapContents = false,
            AutoScroll = false,
            Margin = Padding.Empty,
        };
        startBtn = MakeButton("启动代理", C_GREEN);
        stopBtn = MakeButton("停止代理", C_RED);
        stopBtn.Enabled = false;
        clearBtn = MakeButton("清空日志", Color.FromArgb(108, 117, 125));
        pinBtn = MakeButton("窗口置顶", Color.FromArgb(108, 117, 125));
        settingsBtn = MakeButton("设置", Color.FromArgb(94, 112, 141));
        startBtn.Click += (s, e) => StartProxy();
        stopBtn.Click += (s, e) => StopProxy();
        clearBtn.Click += (s, e) => { logBox.Clear(); AppendLog("日志已清空", C_GRAY); };
        pinBtn.Click += (s, e) =>
        {
            TopMost = !TopMost;
            pinBtn.BackColor = TopMost ? C_BLUE : Color.FromArgb(108, 117, 125);
        };
        settingsBtn.Click += (s, e) => OpenSettings();
        toolbar.Controls.AddRange(new Control[] { startBtn, stopBtn, clearBtn, pinBtn, settingsBtn });

        // ---------- log box ----------
        logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(52, 58, 70),
            Font = new Font("Consolas", 9.5F),
            WordWrap = false,
            HideSelection = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        logBox.TextChanged += (s, e) =>
        {
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
            if (logBox.Lines.Length > 3000)
            {
                int cut = logBox.GetFirstCharIndexFromLine(logBox.Lines.Length - 2500);
                if (cut > 0) { logBox.Select(0, cut); logBox.SelectedText = ""; }
            }
        };
        AppendLog("[系统] 欢迎使用 ZenProxy", C_BLUE);

        // ---------- status bar ----------
        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            ColumnCount = 2,
            BackColor = C_HEADER,
            Padding = new Padding(12, 0, 12, 0),
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        statusLbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = C_DARK, Text = "状态: 未运行" };
        infoLbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, ForeColor = C_GRAY, Text = "端口 4050" };
        statusBar.Controls.Add(statusLbl, 0, 0);
        statusBar.Controls.Add(infoLbl, 1, 0);

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(228, 231, 238) };

        Controls.Add(logBox);
        Controls.Add(toolbar);
        Controls.Add(line);
        Controls.Add(statusBar);

        uptimeTimer = new Timer { Interval = 1000 };
        uptimeTimer.Tick += (s, e) =>
        {
            if (proc != null && !proc.HasExited)
                infoLbl.Text = "端口 4050 · 模型 " + GetEnv("UPSTREAM_MODEL") + " · 已运行 " + (DateTime.Now - startedAt).ToString(@"hh\:mm\:ss");
        };

        Shown += (s, e) =>
        {
            if (IsProxyRunning())
            {
                SetStatus("代理已在运行", C_GREEN);
                AppendLog("[系统] 检测到代理已在端口 4050 运行", C_GRAY);
            }
            else
            {
                AppendLog("[系统] 代理未运行,自动启动...", C_GRAY);
                StartProxy();
            }
        };
        FormClosing += (s, e) =>
        {
            closing = true;
            if (proc != null && !proc.HasExited)
            {
                AppendLog("[系统] 窗口关闭,停止代理", C_GRAY);
                StopProxy();
            }
        };
    }

    // ---------- env file helpers ----------
    static Dictionary<string, string> LoadEnv()
    {
        var d = new Dictionary<string, string>();
        if (File.Exists(EnvPath))
        {
            foreach (var line in File.ReadAllLines(EnvPath))
            {
                var m = Regex.Match(line, "^([^#=][^=]*)=(.*)$");
                if (m.Success) d[m.Groups[1].Value.Trim()] = m.Groups[2].Value;
            }
        }
        return d;
    }

    static void SaveEnv(Dictionary<string, string> d)
    {
        var sb = new StringBuilder();
        foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).AppendLine();
        File.WriteAllText(EnvPath, sb.ToString(), new UTF8Encoding(false));
    }

    static string GetEnv(string key)
    {
        var d = LoadEnv();
        return d.ContainsKey(key) ? d[key] : "";
    }

    // ---------- CC Switch sync helpers ----------
    static string CcSwitchDbPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-switch", "cc-switch.db"); } }
    static string ClaudeSettingsPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"); } }

    static string RunSqlite(string sql)
    {
        try
        {
            if (!File.Exists(CcSwitchDbPath)) return "";
            var p = new Process();
            p.StartInfo.FileName = "sqlite3";
            p.StartInfo.Arguments = "\"" + CcSwitchDbPath + "\" " + sql;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch { return ""; }
    }

    static string GetCcSwitchProviderName()
    {
        return RunSqlite("SELECT name FROM providers WHERE app_type='claude' AND is_current=1 LIMIT 1;");
    }

    static string GetCcSwitchKey()
    {
        return RunSqlite("SELECT json_extract(settings_config,'$.env.ANTHROPIC_AUTH_TOKEN') FROM providers WHERE app_type='claude' AND is_current=1 LIMIT 1;");
    }

    static void SetCcSwitchKey(string key)
    {
        RunSqlite("UPDATE providers SET settings_config = json_set(settings_config,'$.env.ANTHROPIC_AUTH_TOKEN','" + key.Replace("'", "''") + "') WHERE app_type='claude' AND is_current=1;");
    }

    static void SetClaudeSettingsKey(string key)
    {
        try
        {
            if (!File.Exists(ClaudeSettingsPath)) return;
            string text = File.ReadAllText(ClaudeSettingsPath);
            string safeKey = key.Replace("\"", "\\\"");
            var m = Regex.Match(text, "\"ANTHROPIC_AUTH_TOKEN\"\\s*:\\s*\"[^\"]*\"");
            if (m.Success)
                text = text.Replace(m.Value, "\"ANTHROPIC_AUTH_TOKEN\": \"" + safeKey + "\"");
            else
            {
                var envMatch = Regex.Match(text, "\"env\"\\s*:\\s*\\{");
                if (envMatch.Success)
                    text = text.Insert(envMatch.Index + envMatch.Length, "\n    \"ANTHROPIC_AUTH_TOKEN\": \"" + safeKey + "\",");
            }
            File.WriteAllText(ClaudeSettingsPath, text);
        }
        catch { }
    }

    void OpenSettings()
    {
        var d = LoadEnv();
        string providerName = GetCcSwitchProviderName();
        string ccKey = GetCcSwitchKey();
        bool ccOk = !String.IsNullOrEmpty(providerName) && !String.IsNullOrEmpty(ccKey);
        string key = ccOk ? ccKey : (d.ContainsKey("UPSTREAM_API_KEY") ? d["UPSTREAM_API_KEY"] : "");
        string model = d.ContainsKey("UPSTREAM_MODEL") ? d["UPSTREAM_MODEL"] : "deepseek-v4-flash-free";
        string url = d.ContainsKey("UPSTREAM_CHAT_COMPLETIONS_URL") ? d["UPSTREAM_CHAT_COMPLETIONS_URL"] : "https://opencode.ai/zen/v1/chat/completions";

        var f = new SettingsForm(key, model, url, ccOk, providerName);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            string newKey = f.ApiKey.Trim();
            d["UPSTREAM_API_KEY"] = newKey;
            d["UPSTREAM_MODEL"] = f.Model.Trim();
            d["UPSTREAM_CHAT_COMPLETIONS_URL"] = f.BaseUrl.Trim();
            SaveEnv(d);
            if (ccOk || !String.IsNullOrEmpty(providerName))
            {
                SetCcSwitchKey(newKey);
                SetClaudeSettingsKey(newKey);
                AppendLog("[系统] 配置已保存并同步 CC Switch (模型: " + d["UPSTREAM_MODEL"] + ")", C_GREEN);
            }
            else
            {
                AppendLog("[系统] 配置已保存 (模型: " + d["UPSTREAM_MODEL"] + ")", C_GREEN);
            }
            if (proc != null && !proc.HasExited)
            {
                AppendLog("[系统] 配置已变更,自动重启代理...", C_GRAY);
                StopProxy();
                StartProxy();
            }
            else
            {
                AppendLog("[系统] 下次启动代理时生效", C_GRAY);
            }
        }
    }

    // ---------- UI helpers ----------
    Button MakeButton(string text, Color bg)
    {
        return new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(100, 32),
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
    }

    bool IsProxyRunning()
    {
        try { using (var c = new TcpClient()) { c.Connect("127.0.0.1", 4050); return true; } }
        catch { return false; }
    }

    void SetStatus(string text, Color color)
    {
        if (closing) return;
        if (statusLbl.InvokeRequired) { statusLbl.BeginInvoke((Action)(() => SetStatus(text, color))); return; }
        statusLbl.Text = "状态: " + text;
        statusLbl.ForeColor = color;
    }

    void AppendLog(string line, Color color)
    {
        if (closing) return;
        if (logBox.InvokeRequired) { logBox.BeginInvoke((Action)(() => AppendLog(line, color))); return; }
        logBox.SelectionStart = logBox.TextLength;
        logBox.SelectionColor = color;
        logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + "\n");
        logBox.SelectionColor = logBox.ForeColor;
    }

    void ParseProxyLine(string line)
    {
        if (line.StartsWith("{"))
        {
            try
            {
                var json = NewtonsoftJsonLikeParse(line);
                string ev = json["event"];
                if (ev == "proxy_listening")
                {
                    SetStatus("代理运行中", C_GREEN);
                    AppendLog("✓ 代理已启动 (端口 " + json["port"] + ", 模型 " + json["upstream_model"] + ")", C_GREEN);
                    uptimeTimer.Start();
                    startedAt = DateTime.Now;
                    return;
                }
                if (ev == "request")
                {
                    int status = int.Parse(json["status"]);
                    Color c = status >= 400 ? C_RED : C_GRAY;
                    AppendLog(json["method"] + " " + json["path"] + " → " + status + " (" + json["ms"] + "ms)", c);
                    return;
                }
            }
            catch { }
        }
        if (line.StartsWith("[err]"))
            AppendLog(line, C_RED);
        else if (line.Trim().Length > 0)
            AppendLog(line, C_GRAY);
    }

    Dictionary<string, string> NewtonsoftJsonLikeParse(string line)
    {
        var result = new Dictionary<string, string>();
        var m = Regex.Matches(line, "\"([a-z_]+)\"\\s*:\\s*\"?([^\"}]*)\"?");
        foreach (Match mm in m)
            result[mm.Groups[1].Value] = mm.Groups[2].Value;
        return result;
    }

    void StartProxy()
    {
        if (proc != null && !proc.HasExited) return;
        if (IsProxyRunning()) { SetStatus("代理已在运行", C_GREEN); return; }

        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string exe = Path.Combine(dir, "ZenProxy.exe");

        try
        {
            proc = new Process();
            proc.StartInfo.FileName = exe;
            proc.StartInfo.WorkingDirectory = dir;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += (s, e) => { if (!String.IsNullOrEmpty(e.Data)) ParseProxyLine(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (!String.IsNullOrEmpty(e.Data)) ParseProxyLine("[err] " + e.Data); };
            proc.Exited += (s, e) =>
            {
                uptimeTimer.Stop();
                startBtn.Invoke((Action)(() => startBtn.Enabled = true));
                stopBtn.Invoke((Action)(() => stopBtn.Enabled = false));
                SetStatus("代理已退出", C_RED);
                AppendLog("[系统] 代理进程已退出", C_GRAY);
                proc = null;
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            startBtn.Enabled = false;
            stopBtn.Enabled = true;
            SetStatus("代理启动中...", C_BLUE);
            AppendLog("[系统] 正在启动代理...", C_GRAY);
        }
        catch (Exception ex)
        {
            AppendLog("[系统] 启动失败: " + ex.Message, C_RED);
            SetStatus("启动失败", C_RED);
        }
    }

    void StopProxy()
    {
        try
        {
            if (proc != null && !proc.HasExited) { proc.Kill(); proc.WaitForExit(3000); }
            proc = null;
        }
        catch { }
        uptimeTimer.Stop();
        startBtn.Enabled = true;
        stopBtn.Enabled = false;
        SetStatus("已停止", C_GRAY);
        AppendLog("[系统] 代理已停止", C_GRAY);
    }
}

class SettingsForm : Form
{
    TextBox keyBox, modelBox, urlBox;
    public string ApiKey, Model, BaseUrl;

    public SettingsForm(string apiKey, string model, string baseUrl, bool ccOk, string providerName)
    {
        Text = "代理设置";
        ClientSize = new Size(560, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(250, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9F);

        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 176, Padding = new Padding(16, 16, 16, 0), ColumnCount = 2, RowCount = 4 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        keyBox = MakeTextBox(apiKey);
        modelBox = MakeTextBox(model);
        urlBox = MakeTextBox(baseUrl);

        var copyBtn = MakeButton("复制", Color.FromArgb(94, 112, 141), 64);
        copyBtn.Click += (s, e) => { Clipboard.SetText(keyBox.Text); copyBtn.Text = "已复制"; copyBtn.BackColor = C_COPY_OK; };
        var keyRow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 12, 4), WrapContents = false };
        keyRow.Controls.Add(keyBox);
        keyRow.Controls.Add(copyBtn);

        grid.Controls.Add(MakeLabel("API Key (同步 CC Switch)"), 0, 0);
        grid.Controls.Add(keyRow, 1, 0);
        grid.Controls.Add(MakeLabel("模型 (Model)"), 0, 1);
        grid.Controls.Add(modelBox, 1, 1);
        grid.Controls.Add(MakeLabel("接口地址 (Base URL)"), 0, 2);
        grid.Controls.Add(urlBox, 1, 2);

        var status = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = ccOk ? C_GREEN_TEXT : Color.FromArgb(217, 83, 79),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = ccOk ? "✓ 已读取 CC Switch 当前 provider: " + providerName + ",保存后三方同步" : "未找到 CC Switch 激活的 Claude provider,Key 仅写入 .env.local",
        };
        grid.Controls.Add(status, 0, 3);
        grid.SetColumnSpan(status, 2);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(16, 6, 16, 0),
            Text = "Key 需与 OpenCode Zen 账号一致;修改后自动重启代理生效",
            ForeColor = Color.FromArgb(120, 126, 139),
            AutoEllipsis = true,
        };

        var saveBtn = MakeButton("保存", Color.FromArgb(52, 168, 83), 88);
        var cancelBtn = MakeButton("取消", Color.FromArgb(108, 117, 125), 88);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(16, 8, 16, 12), FlowDirection = FlowDirection.RightToLeft };
        saveBtn.Click += (s, e) => { ApiKey = keyBox.Text; Model = modelBox.Text; BaseUrl = urlBox.Text; DialogResult = DialogResult.OK; Close(); };
        cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        btns.Controls.Add(saveBtn);
        btns.Controls.Add(cancelBtn);

        Controls.Add(grid);
        Controls.Add(hint);
        Controls.Add(btns);
        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }

    static readonly Color C_COPY_OK = Color.FromArgb(52, 168, 83);
    static readonly Color C_GREEN_TEXT = Color.FromArgb(52, 145, 80);

    Label MakeLabel(string text)
    {
        return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(45, 52, 63) };
    }

    TextBox MakeTextBox(string value)
    {
        return new TextBox { Text = value, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 6, 4), Font = new Font("Consolas", 9F) };
    }

    Button MakeButton(string text, Color bg, int width)
    {
        return new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(width, 28),
            Margin = new Padding(4, 4, 0, 0),
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
    }
}
