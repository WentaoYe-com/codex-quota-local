using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Codex Quota Local")]
[assembly: System.Reflection.AssemblyDescription("Small local-first Codex quota overlay.")]
[assembly: System.Reflection.AssemblyVersion("0.2.2.0")]

internal enum QuotaReadMode
{
    Auto,
    OfflineOnly,
    LiveFirst
}

internal sealed class QuotaWindow
{
    public string Name;
    public int WindowMinutes;
    public double UsedPercent;
    public long ResetAtUnix;
}

internal sealed class QuotaSnapshot
{
    public readonly List<QuotaWindow> Windows = new List<QuotaWindow>();
    public string Source;
}

internal sealed class RadarSnapshot
{
    public int Probability24h;
    public int Probability48h;
    public string Confidence;
    public string UpdatedAt;
    public string LastResetAt;
    public string LatestSummary;
    public string LatestUrl;
}

internal static class UnixTime
{
    public static long Now()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    public static DateTime ToLocal(long unix)
    {
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime();
    }
}

internal static class QuotaReader
{
    private const int SqliteOpenReadOnly = 1;
    private const int SqliteRow = 100;
    public static string LastDiagnostic = "not started";

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(IntPtr filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, IntPtr sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    public static QuotaSnapshot Read(QuotaReadMode mode)
    {
        if (mode == QuotaReadMode.OfflineOnly)
            return ReadLogs();

        if (mode == QuotaReadMode.LiveFirst)
        {
            QuotaSnapshot remote = ReadRemote();
            if (remote != null) return remote;
            string remoteError = LastDiagnostic;
            QuotaSnapshot fallback = ReadLogs();
            LastDiagnostic = fallback == null ? remoteError + "; offline fallback failed" : remoteError + "; using offline fallback";
            return fallback;
        }

        QuotaSnapshot local = ReadLogs();
        if (local != null) return local;

        string offlineError = LastDiagnostic;
        QuotaSnapshot liveFallback = ReadRemote();
        LastDiagnostic = liveFallback == null
            ? offlineError + "; live fallback failed: " + LastDiagnostic
            : offlineError + "; using live fallback";
        return liveFallback;
    }

    public static string ResolveCodexHome()
    {
        string configured = Environment.GetEnvironmentVariable("CODEX_QUOTA_DATA_DIR");
        if (String.IsNullOrWhiteSpace(configured))
            configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!String.IsNullOrWhiteSpace(configured)) return configured;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static QuotaSnapshot ReadLogs()
    {
        string databasePath = Path.Combine(ResolveCodexHome(), "logs_2.sqlite");
        if (!File.Exists(databasePath))
        {
            LastDiagnostic = "offline data not found: " + databasePath;
            return null;
        }

        const string sql =
            "SELECT feedback_log_body FROM logs " +
            "WHERE feedback_log_body LIKE '%x-codex-%-used-percent%' " +
            "ORDER BY ts DESC, ts_nanos DESC, id DESC LIMIT 1";

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        IntPtr pathUtf8 = AllocUtf8(databasePath);
        IntPtr sqlUtf8 = AllocUtf8(sql);
        try
        {
            int result = sqlite3_open_v2(pathUtf8, out database, SqliteOpenReadOnly, IntPtr.Zero);
            if (result != 0)
            {
                LastDiagnostic = "sqlite open failed (" + result + "): " + SqliteError(database);
                return null;
            }

            result = sqlite3_prepare_v2(database, sqlUtf8, -1, out statement, IntPtr.Zero);
            if (result != 0)
            {
                LastDiagnostic = "sqlite prepare failed (" + result + "): " + SqliteError(database);
                return null;
            }

            result = sqlite3_step(statement);
            if (result != SqliteRow)
            {
                LastDiagnostic = "no quota headers in local Codex logs";
                return null;
            }

            string body = ReadUtf8Column(statement, 0);
            QuotaSnapshot snapshot = ParseHeaderSnapshot(body);
            if (snapshot.Windows.Count == 0)
            {
                LastDiagnostic = "local log entry had no standard quota windows";
                return null;
            }

            snapshot.Source = "offline";
            LastDiagnostic = "offline ok";
            return snapshot;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (database != IntPtr.Zero) sqlite3_close(database);
            Marshal.FreeHGlobal(pathUtf8);
            Marshal.FreeHGlobal(sqlUtf8);
        }
    }

    private static QuotaSnapshot ReadRemote()
    {
        try
        {
            string authPath = Path.Combine(ResolveCodexHome(), "auth.json");
            if (!File.Exists(authPath))
            {
                LastDiagnostic = "live mode unavailable: auth.json not found";
                return null;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> auth = serializer.DeserializeObject(File.ReadAllText(authPath)) as Dictionary<string, object>;
            Dictionary<string, object> tokens = Dict(auth, "tokens");
            string accessToken = Str(tokens, "access_token");
            string accountId = Str(tokens, "account_id");
            if (String.IsNullOrWhiteSpace(accessToken) || String.IsNullOrWhiteSpace(accountId))
            {
                LastDiagnostic = "live mode unavailable: Codex auth token missing";
                return null;
            }

            Uri endpoint = new Uri("https://chatgpt.com/backend-api/wham/usage");
            if (!endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                !endpoint.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase))
            {
                LastDiagnostic = "blocked live request: endpoint allowlist failed";
                return null;
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "codex-quota-local/0.2";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + accessToken;
            request.Headers["ChatGPT-Account-Id"] = accountId;

            string json;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    LastDiagnostic = "live HTTP " + (int)response.StatusCode;
                    return null;
                }
                json = reader.ReadToEnd();
            }

            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            Dictionary<string, object> rateLimit = Dict(root, "rate_limit");
            QuotaSnapshot snapshot = new QuotaSnapshot();
            AddRemoteWindow(snapshot, rateLimit, "primary_window", "primary");
            AddRemoteWindow(snapshot, rateLimit, "secondary_window", "secondary");
            Sort(snapshot);
            if (snapshot.Windows.Count == 0)
            {
                LastDiagnostic = "live response had no standard quota windows";
                return null;
            }

            snapshot.Source = "live";
            LastDiagnostic = "live ok";
            return snapshot;
        }
        catch (WebException exception)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            LastDiagnostic = response == null ? "live request failed: " + exception.Status : "live HTTP " + (int)response.StatusCode;
            return null;
        }
        catch (Exception exception)
        {
            LastDiagnostic = "live failed: " + exception.GetType().Name;
            return null;
        }
    }

    private static QuotaSnapshot ParseHeaderSnapshot(string body)
    {
        QuotaSnapshot snapshot = new QuotaSnapshot();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MatchCollection matches = Regex.Matches(body ?? String.Empty,
            "\\\"x-codex-(?<name>[a-z0-9-]+)-window-minutes\\\"\\s*:\\s*\\\"(?<minutes>[^\\\"]*)\\\"",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            string name = match.Groups["name"].Value;
            if (!name.Equals("primary", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("secondary", StringComparison.OrdinalIgnoreCase))
                continue;

            int minutes;
            if (!seen.Add(name) || !Int32.TryParse(match.Groups["minutes"].Value, out minutes) || minutes <= 0)
                continue;

            double used;
            if (!Double.TryParse(HeaderValue(body, "x-codex-" + name + "-used-percent"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out used))
                continue;

            long resetAt;
            if (!Int64.TryParse(HeaderValue(body, "x-codex-" + name + "-reset-at"), out resetAt) || resetAt <= 0)
            {
                long resetAfter;
                resetAt = Int64.TryParse(HeaderValue(body, "x-codex-" + name + "-reset-after-seconds"), out resetAfter) && resetAfter > 0
                    ? UnixTime.Now() + resetAfter
                    : 0;
            }

            snapshot.Windows.Add(new QuotaWindow
            {
                Name = name,
                WindowMinutes = minutes,
                UsedPercent = Clamp(used),
                ResetAtUnix = resetAt
            });
        }

        Sort(snapshot);
        return snapshot;
    }

    private static void AddRemoteWindow(QuotaSnapshot snapshot, Dictionary<string, object> rateLimit, string key, string name)
    {
        Dictionary<string, object> window = Dict(rateLimit, key);
        if (window == null) return;

        double used;
        long seconds;
        if (!TryDouble(Raw(window, "used_percent"), out used) ||
            !TryLong(Raw(window, "limit_window_seconds"), out seconds) || seconds <= 0)
            return;

        long resetAt;
        if (!TryLong(Raw(window, "reset_at"), out resetAt) || resetAt <= 0)
        {
            long resetAfter;
            resetAt = TryLong(Raw(window, "reset_after_seconds"), out resetAfter) && resetAfter > 0
                ? UnixTime.Now() + resetAfter
                : 0;
        }

        snapshot.Windows.Add(new QuotaWindow
        {
            Name = name,
            WindowMinutes = (int)Math.Min(Int32.MaxValue, seconds / 60),
            UsedPercent = Clamp(used),
            ResetAtUnix = resetAt
        });
    }

    private static object Raw(Dictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value) ? value : null;
    }

    private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
    {
        return Raw(source, key) as Dictionary<string, object>;
    }

    private static string Str(Dictionary<string, object> source, string key)
    {
        object value = Raw(source, key);
        return value == null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryDouble(object value, out double result)
    {
        try
        {
            if (value == null) { result = 0; return false; }
            result = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch { result = 0; return false; }
    }

    private static bool TryLong(object value, out long result)
    {
        try
        {
            if (value == null) { result = 0; return false; }
            result = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch { result = 0; return false; }
    }

    private static string HeaderValue(string body, string name)
    {
        Match match = Regex.Match(body ?? String.Empty,
            "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"(?<value>[^\\\"]*)\\\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static double Clamp(double value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static void Sort(QuotaSnapshot snapshot)
    {
        snapshot.Windows.Sort(delegate(QuotaWindow a, QuotaWindow b)
        {
            int byDuration = a.WindowMinutes.CompareTo(b.WindowMinutes);
            if (byDuration != 0) return byDuration;
            return String.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IntPtr AllocUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return pointer;
    }

    private static string ReadUtf8Column(IntPtr statement, int column)
    {
        IntPtr pointer = sqlite3_column_text(statement, column);
        int length = sqlite3_column_bytes(statement, column);
        if (pointer == IntPtr.Zero || length <= 0) return String.Empty;
        byte[] bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string SqliteError(IntPtr database)
    {
        if (database == IntPtr.Zero) return "unknown";
        IntPtr pointer = sqlite3_errmsg(database);
        return pointer == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(pointer);
    }
}

internal static class RadarReader
{
    public static string LastDiagnostic = "not started";
    private static readonly Uri ForecastEndpoint = new Uri("https://codex-reset.com/api/forecast");

    public static RadarSnapshot Read()
    {
        try
        {
            if (!ForecastEndpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
                !ForecastEndpoint.Host.Equals("codex-reset.com", StringComparison.OrdinalIgnoreCase))
            {
                LastDiagnostic = "blocked radar request: endpoint allowlist failed";
                return null;
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ForecastEndpoint);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = "codex-quota-local/0.2";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;

            string json;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    LastDiagnostic = "radar HTTP " + (int)response.StatusCode;
                    return null;
                }
                json = reader.ReadToEnd();
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            Dictionary<string, object> probabilities = Dict(root, "probabilities");
            Dictionary<string, object> latestAlert = Dict(root, "latest_alert");

            RadarSnapshot snapshot = new RadarSnapshot();
            snapshot.Probability24h = IntValue(probabilities, "rounded_24h", -1);
            snapshot.Probability48h = IntValue(probabilities, "rounded_48h", -1);
            snapshot.Confidence = Str(root, "confidence");
            snapshot.UpdatedAt = Str(root, "updated_at");
            snapshot.LastResetAt = Str(root, "last_reset_at");
            snapshot.LatestSummary = Str(latestAlert, "summary");
            snapshot.LatestUrl = Str(latestAlert, "url");
            if (snapshot.Probability24h < 0 && snapshot.Probability48h < 0)
            {
                LastDiagnostic = "radar response missing probabilities";
                return null;
            }

            LastDiagnostic = "radar ok";
            return snapshot;
        }
        catch (WebException exception)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            LastDiagnostic = response == null ? "radar request failed: " + exception.Status : "radar HTTP " + (int)response.StatusCode;
            return null;
        }
        catch (Exception exception)
        {
            LastDiagnostic = "radar failed: " + exception.GetType().Name;
            return null;
        }
    }

    private static object Raw(Dictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value) ? value : null;
    }

    private static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
    {
        return Raw(source, key) as Dictionary<string, object>;
    }

    private static string Str(Dictionary<string, object> source, string key)
    {
        object value = Raw(source, key);
        return value == null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int IntValue(Dictionary<string, object> source, string key, int fallback)
    {
        try
        {
            object value = Raw(source, key);
            return value == null ? fallback : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return fallback; }
    }
}

internal sealed class QuotaOverlayForm : Form
{
    private const int GwlHwndParent = -8;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const uint GaRoot = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    private QuotaReadMode quotaMode;
    private bool radarMode;
    private int quotaIntervalSeconds;
    private int radarIntervalSeconds;
    private readonly Label label;
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer timer;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem liveItem;
    private readonly ToolStripMenuItem radarItem;
    private readonly ToolStripMenuItem quotaModeMenu;
    private readonly ToolStripMenuItem quotaAutoItem;
    private readonly ToolStripMenuItem quotaOfflineOnlyItem;
    private readonly ToolStripMenuItem quotaLiveFirstItem;
    private readonly ToolStripMenuItem quotaRefreshMenu;
    private readonly ToolStripMenuItem radarRefreshMenu;
    private IntPtr trackedWindow = IntPtr.Zero;
    private IntPtr ownedWindow = IntPtr.Zero;
    private bool allowVisible;
    private int refreshInProgress;
    private int quotaCountdown;
    private int radarCountdown;
    private QuotaSnapshot latest;
    private RadarSnapshot latestRadar;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern IntPtr SetWindowLongPtr32(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);

    public QuotaOverlayForm(QuotaReadMode mode, bool radar, int quotaInterval, int radarInterval)
    {
        quotaMode = mode;
        radarMode = radar;
        quotaIntervalSeconds = Math.Max(2, quotaInterval);
        radarIntervalSeconds = Math.Max(30, radarInterval);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(245, 26);
        BackColor = Color.FromArgb(40, 40, 44);

        label = new Label();
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleCenter;
        label.BackColor = Color.Transparent;
        label.ForeColor = Color.FromArgb(238, 238, 242);
        label.Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);
        label.AutoEllipsis = true;
        label.Text = InitialQuotaText(quotaMode);
        Controls.Add(label);

        ContextMenuStrip menu = new ContextMenuStrip();
        refreshItem = new ToolStripMenuItem("Refresh now");
        refreshItem.Click += delegate { RefreshData(true); };
        liveItem = new ToolStripMenuItem("Quota source: waiting");
        liveItem.Enabled = false;
        quotaModeMenu = new ToolStripMenuItem("Quota mode");
        quotaAutoItem = new ToolStripMenuItem("Auto: logs, then live");
        quotaAutoItem.Click += delegate { SetQuotaMode(QuotaReadMode.Auto); };
        quotaOfflineOnlyItem = new ToolStripMenuItem("Offline only");
        quotaOfflineOnlyItem.Click += delegate { SetQuotaMode(QuotaReadMode.OfflineOnly); };
        quotaLiveFirstItem = new ToolStripMenuItem("Live first");
        quotaLiveFirstItem.Click += delegate { SetQuotaMode(QuotaReadMode.LiveFirst); };
        quotaModeMenu.DropDownItems.Add(quotaAutoItem);
        quotaModeMenu.DropDownItems.Add(quotaOfflineOnlyItem);
        quotaModeMenu.DropDownItems.Add(quotaLiveFirstItem);
        radarItem = new ToolStripMenuItem("Reset radar: off");
        radarItem.Click += delegate { SetRadarMode(!radarMode); };
        quotaRefreshMenu = new ToolStripMenuItem("Quota refresh");
        AddIntervalItem(quotaRefreshMenu, "5 seconds", 5, true);
        AddIntervalItem(quotaRefreshMenu, "10 seconds", 10, true);
        AddIntervalItem(quotaRefreshMenu, "30 seconds", 30, true);
        AddIntervalItem(quotaRefreshMenu, "1 minute", 60, true);
        radarRefreshMenu = new ToolStripMenuItem("Radar refresh");
        AddIntervalItem(radarRefreshMenu, "1 minute", 60, false);
        AddIntervalItem(radarRefreshMenu, "5 minutes", 300, false);
        AddIntervalItem(radarRefreshMenu, "10 minutes", 600, false);
        AddIntervalItem(radarRefreshMenu, "30 minutes", 1800, false);
        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += delegate { Close(); };
        menu.Items.Add(refreshItem);
        menu.Items.Add(liveItem);
        menu.Items.Add(quotaModeMenu);
        menu.Items.Add(radarItem);
        menu.Items.Add(quotaRefreshMenu);
        menu.Items.Add(radarRefreshMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        UpdateMenuState(null, null);

        trayIcon = new NotifyIcon();
        trayIcon.Icon = SystemIcons.Information;
        trayIcon.Text = "Codex Quota Local (" + QuotaModeText(quotaMode) + ")";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1000;
        timer.Tick += OnTick;
        timer.Start();
        quotaCountdown = 0;
        radarCountdown = 0;

        FormClosed += delegate
        {
            timer.Stop();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            menu.Dispose();
        };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
            return parameters;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && !allowVisible) base.SetVisibleCore(false);
        else base.SetVisibleCore(value);
    }

    private void OnTick(object sender, EventArgs e)
    {
        TrackCodexWindow();
        if (quotaCountdown > 0) quotaCountdown--;
        if (radarMode && radarCountdown > 0) radarCountdown--;
        if (quotaCountdown <= 0 || (radarMode && radarCountdown <= 0))
            RefreshData(false);
    }

    private void RefreshData(bool force)
    {
        if (Interlocked.Exchange(ref refreshInProgress, 1) != 0) return;
        bool readQuota = force || quotaCountdown <= 0;
        bool readRadar = radarMode && (force || radarCountdown <= 0);
        ThreadPool.QueueUserWorkItem(delegate
        {
            QuotaReadMode readMode = quotaMode;
            bool readRadarEnabled = radarMode;
            QuotaSnapshot snapshot = readQuota ? QuotaReader.Read(readMode) : latest;
            RadarSnapshot radar = readRadar ? RadarReader.Read() : latestRadar;
            try
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!radarMode) radar = null;
                        latest = snapshot;
                        latestRadar = radar;
                        label.Text = FormatSnapshot(snapshot, radar);
                        trayIcon.Text = ShortTrayText(label.Text);
                        UpdateMenuState(snapshot, radar);
                        if (readQuota) quotaCountdown = snapshot == null ? 3 : quotaIntervalSeconds;
                        if (readRadar && readRadarEnabled) radarCountdown = radar == null ? 60 : radarIntervalSeconds;
                        Interlocked.Exchange(ref refreshInProgress, 0);
                    });
                }
                else Interlocked.Exchange(ref refreshInProgress, 0);
            }
            catch
            {
                Interlocked.Exchange(ref refreshInProgress, 0);
            }
        });
    }

    private void SetQuotaMode(QuotaReadMode mode)
    {
        if (quotaMode == mode) return;
        quotaMode = mode;
        quotaCountdown = 0;
        latest = null;
        label.Text = InitialQuotaText(quotaMode);
        trayIcon.Text = ShortTrayText(label.Text);
        UpdateMenuState(null, latestRadar);
        RefreshData(true);
    }

    private void SetRadarMode(bool enabled)
    {
        if (radarMode == enabled) return;
        radarMode = enabled;
        radarCountdown = 0;
        if (!radarMode) latestRadar = null;
        label.Text = FormatSnapshot(latest, latestRadar);
        trayIcon.Text = ShortTrayText(label.Text);
        UpdateMenuState(latest, latestRadar);
        if (radarMode) RefreshData(true);
    }

    private void SetQuotaInterval(int seconds)
    {
        quotaIntervalSeconds = Math.Max(2, seconds);
        quotaCountdown = 0;
        UpdateMenuState(latest, latestRadar);
        RefreshData(false);
    }

    private void SetRadarInterval(int seconds)
    {
        radarIntervalSeconds = Math.Max(30, seconds);
        radarCountdown = 0;
        UpdateMenuState(latest, latestRadar);
        if (radarMode) RefreshData(false);
    }

    private void AddIntervalItem(ToolStripMenuItem menu, string text, int seconds, bool quota)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Tag = seconds;
        item.Click += delegate
        {
            if (quota) SetQuotaInterval(seconds);
            else SetRadarInterval(seconds);
        };
        menu.DropDownItems.Add(item);
    }

    private void UpdateMenuState(QuotaSnapshot snapshot, RadarSnapshot radar)
    {
        liveItem.Text = FormatQuotaMenuText(snapshot, quotaMode);
        quotaAutoItem.Checked = quotaMode == QuotaReadMode.Auto;
        quotaOfflineOnlyItem.Checked = quotaMode == QuotaReadMode.OfflineOnly;
        quotaLiveFirstItem.Checked = quotaMode == QuotaReadMode.LiveFirst;
        radarItem.Checked = radarMode;
        radarItem.Text = radarMode ? FormatRadarMenuText(radar) : "Reset radar: off";
        SetIntervalChecks(quotaRefreshMenu, quotaIntervalSeconds);
        SetIntervalChecks(radarRefreshMenu, radarIntervalSeconds);
    }

    private static void SetIntervalChecks(ToolStripMenuItem menu, int seconds)
    {
        foreach (ToolStripItem child in menu.DropDownItems)
        {
            ToolStripMenuItem item = child as ToolStripMenuItem;
            if (item == null || item.Tag == null) continue;
            item.Checked = Convert.ToInt32(item.Tag, System.Globalization.CultureInfo.InvariantCulture) == seconds;
        }
    }

    private static string ShortTrayText(string text)
    {
        if (String.IsNullOrEmpty(text)) return "Codex Quota Local";
        return text.Length <= 63 ? text : text.Substring(0, 63);
    }

    private static string InitialQuotaText(QuotaReadMode mode)
    {
        if (mode == QuotaReadMode.OfflineOnly) return "Quota: local logs...";
        if (mode == QuotaReadMode.LiveFirst) return "Quota: live sync...";
        return "Quota: local logs, then live...";
    }

    private static string QuotaModeText(QuotaReadMode mode)
    {
        if (mode == QuotaReadMode.OfflineOnly) return "offline only";
        if (mode == QuotaReadMode.LiveFirst) return "live first";
        return "auto";
    }

    private static string FormatQuotaMenuText(QuotaSnapshot snapshot, QuotaReadMode mode)
    {
        if (snapshot != null && snapshot.Source != null)
            return "Quota source: " + snapshot.Source;
        if (mode == QuotaReadMode.Auto)
            return "Quota mode: auto (logs, then live fallback)";
        return "Quota mode: " + QuotaModeText(mode);
    }

    private static string FormatRadarMenuText(RadarSnapshot radar)
    {
        if (radar == null) return "Reset radar: waiting";
        string confidence = String.IsNullOrWhiteSpace(radar.Confidence) ? "" : ", " + radar.Confidence;
        return "Reset radar: 24h " + radar.Probability24h + "%, 48h " + radar.Probability48h + "%" + confidence;
    }

    private static string FormatSnapshot(QuotaSnapshot snapshot, RadarSnapshot radar)
    {
        List<string> output = new List<string>();
        if (snapshot == null) output.Add("No quota data");
        else output.Add(FormatQuota(snapshot));

        if (radar != null)
        {
            string radarText = "Radar 24h " + radar.Probability24h + "%";
            if (radar.Probability48h >= 0) radarText += " / 48h " + radar.Probability48h + "%";
            output.Add(radarText);
        }

        return String.Join("  |  ", output.ToArray());
    }

    private static string FormatQuota(QuotaSnapshot snapshot)
    {
        List<string> parts = new List<string>();
        foreach (QuotaWindow window in snapshot.Windows)
        {
            double remaining = Math.Max(0, 100 - window.UsedPercent);
            string value = Math.Abs(remaining - Math.Round(remaining)) < 0.05
                ? Math.Round(remaining).ToString("0")
                : remaining.ToString("0.0");
            string part = FormatDuration(window.WindowMinutes) + " " + value + "%";
            if (window.ResetAtUnix > 0)
                part += " -> " + FormatResetTime(UnixTime.ToLocal(window.ResetAtUnix));
            parts.Add(part);
        }
        return String.Join("  |  ", parts.ToArray());
    }

    private static string FormatResetTime(DateTime resetAt)
    {
        return resetAt.Date == DateTime.Now.Date
            ? resetAt.ToString("HH:mm")
            : resetAt.ToString("M/d HH:mm");
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes == 10080) return "W";
        if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440).ToString() + "d";
        if (minutes > 0 && minutes % 60 == 0) return (minutes / 60).ToString() + "h";
        return minutes.ToString() + "m";
    }

    private void TrackCodexWindow()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            IntPtr root = GetAncestor(foreground, GaRoot);
            if (root == IntPtr.Zero) root = foreground;
            if (IsChatGptWindow(root) && !IsIconic(root))
            {
                trackedWindow = root;
                PositionForWindow(root);
                return;
            }
        }

        if (trackedWindow != IntPtr.Zero && IsWindow(trackedWindow) && IsChatGptWindow(trackedWindow) && !IsIconic(trackedWindow))
        {
            PositionForWindow(trackedWindow);
            return;
        }

        trackedWindow = IntPtr.Zero;
        Hide();
    }

    private static bool IsChatGptWindow(IntPtr window)
    {
        uint processId;
        GetWindowThreadProcessId(window, out processId);
        try
        {
            string processName = Process.GetProcessById((int)processId).ProcessName;
            return processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private void PositionForWindow(IntPtr root)
    {
        Rect bounds;
        if (DwmGetWindowAttribute(root, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf(typeof(Rect))) != 0 &&
            !GetWindowRect(root, out bounds))
        {
            Hide();
            return;
        }

        uint dpi;
        try { dpi = GetDpiForWindow(root); } catch { dpi = 96; }
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        int height = (int)Math.Round(26 * scale);
        Size measured = TextRenderer.MeasureText(label.Text ?? String.Empty, label.Font);
        int availableWidth = Math.Max((int)Math.Round(170 * scale), bounds.Right - bounds.Left - (int)Math.Round(190 * scale));
        int preferredWidth = measured.Width + (int)Math.Round(24 * scale);
        int width = Math.Max((int)Math.Round(170 * scale), Math.Min(availableWidth, preferredWidth));
        int buttons = (int)Math.Round(142 * scale);
        int x = bounds.Right - buttons - width - (int)Math.Round(8 * scale);
        int y = bounds.Top + (int)Math.Round(4 * scale);

        if (ownedWindow != root)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, GwlHwndParent, root);
            else SetWindowLongPtr32(Handle, GwlHwndParent, root);
            ownedWindow = root;
        }

        if (!Visible)
        {
            allowVisible = true;
            Show();
            allowVisible = false;
        }
        SetWindowPos(Handle, IntPtr.Zero, x, y, width, height, SwpNoActivate | SwpNoZOrder | SwpShowWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    private static void Main(string[] args)
    {
        bool live = HasArg(args, "--live");
        bool offlineOnly = HasArg(args, "--offline-only");
        bool radar = HasArg(args, "--radar");
        bool snapshot = HasArg(args, "--snapshot");
        int quotaInterval = IntArg(args, "--quota-interval-seconds", 10);
        int radarInterval = IntArg(args, "--radar-interval-minutes", 10) * 60;
        QuotaReadMode quotaMode = offlineOnly ? QuotaReadMode.OfflineOnly : (live ? QuotaReadMode.LiveFirst : QuotaReadMode.Auto);

        if (snapshot)
        {
            QuotaSnapshot data = QuotaReader.Read(quotaMode);
            if (data == null)
            {
                Console.WriteLine("NO_DATA: " + QuotaReader.LastDiagnostic);
                Environment.ExitCode = 2;
                return;
            }
            foreach (QuotaWindow window in data.Windows)
            {
                Console.WriteLine("{0}: {1:0.#}% left, {2}, reset {3}",
                    window.Name,
                    100 - window.UsedPercent,
                    FormatDuration(window.WindowMinutes),
                    window.ResetAtUnix > 0 ? UnixTime.ToLocal(window.ResetAtUnix).ToString("yyyy-MM-dd HH:mm:ss") : "unknown");
            }
            Console.WriteLine("source: " + data.Source);
            Console.WriteLine("diagnostic: " + QuotaReader.LastDiagnostic);
            if (radar)
            {
                RadarSnapshot radarData = RadarReader.Read();
                if (radarData == null)
                {
                    Console.WriteLine("radar: NO_DATA: " + RadarReader.LastDiagnostic);
                }
                else
                {
                    Console.WriteLine("radar_24h: {0}%", radarData.Probability24h);
                    Console.WriteLine("radar_48h: {0}%", radarData.Probability48h);
                    Console.WriteLine("radar_confidence: {0}", radarData.Confidence);
                    Console.WriteLine("radar_updated_at: {0}", radarData.UpdatedAt);
                    Console.WriteLine("radar_last_reset_at: {0}", radarData.LastResetAt);
                    Console.WriteLine("radar_latest_url: {0}", radarData.LatestUrl);
                }
            }
            return;
        }

        bool ownsMutex;
        using (Mutex mutex = new Mutex(true, "Local\\CodexQuotaLocal" + quotaMode.ToString() + (radar ? "Radar" : ""), out ownsMutex))
        {
            if (!ownsMutex) return;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new QuotaOverlayForm(quotaMode, radar, quotaInterval, radarInterval));
        }
    }

    private static bool HasArg(string[] args, string name)
    {
        foreach (string arg in args)
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            int value;
            if (Int32.TryParse(args[i + 1], out value) && value > 0) return value;
        }
        return fallback;
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes == 10080) return "weekly";
        if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440).ToString() + "d";
        if (minutes > 0 && minutes % 60 == 0) return (minutes / 60).ToString() + "h";
        return minutes.ToString() + "m";
    }
}
