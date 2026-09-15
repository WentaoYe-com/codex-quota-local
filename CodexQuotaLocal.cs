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
[assembly: System.Reflection.AssemblyVersion("0.4.0.0")]

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
    public long ObservedAtUnix;
    public bool CreditBalanceAvailable;
    public decimal CreditBalance;
    public bool CreditsUnlimited;
}

internal static class QuotaFreshness
{
    public const int MaxAgeSeconds = 60;

    public static bool IsCurrent(QuotaSnapshot snapshot, long now)
    {
        if (snapshot == null || snapshot.Windows.Count == 0 || snapshot.ObservedAtUnix <= 0 ||
            snapshot.ObservedAtUnix > now + 5 || now - snapshot.ObservedAtUnix >= MaxAgeSeconds)
            return false;
        foreach (QuotaWindow window in snapshot.Windows)
            if (window.ResetAtUnix > 0 && window.ResetAtUnix <= now) return false;
        return true;
    }
}

internal sealed class RadarSnapshot
{
    public readonly List<RadarReading> Readings = new List<RadarReading>();
    public string Confidence;
    public string UpdatedAt;
    public string LastResetAt;
    public string LatestSummary;
    public string LatestUrl;
}

internal sealed class RadarReading
{
    public string Name;
    public int Probability24h;
    public string Detail;
    public string UpdatedAt;
    public string Url;
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
        return Read(mode, false);
    }

    public static QuotaSnapshot Read(QuotaReadMode mode, bool includeCredits)
    {
        return Read(mode, includeCredits, ReadLogs, ReadRemote);
    }

    internal static QuotaSnapshot Read(QuotaReadMode mode, Func<QuotaSnapshot> readLogs, Func<QuotaSnapshot> readRemote)
    {
        return Read(mode, false, readLogs, readRemote);
    }

    internal static QuotaSnapshot Read(QuotaReadMode mode, bool includeCredits,
        Func<QuotaSnapshot> readLogs, Func<QuotaSnapshot> readRemote)
    {
        if (mode == QuotaReadMode.OfflineOnly)
            return readLogs();

        if (mode == QuotaReadMode.LiveFirst || includeCredits)
        {
            QuotaSnapshot remote = readRemote();
            if (remote != null) return remote;
            string remoteError = LastDiagnostic;
            QuotaSnapshot fallback = readLogs();
            LastDiagnostic = fallback == null
                ? remoteError + "; offline fallback failed"
                : remoteError + (includeCredits ? "; using offline fallback without credits" : "; using offline fallback");
            return fallback;
        }

        QuotaSnapshot local = readLogs();
        if (local != null) return local;

        string offlineError = LastDiagnostic;
        QuotaSnapshot liveFallback = readRemote();
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
            "SELECT feedback_log_body, ts FROM logs " +
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
            long observedAt;
            Int64.TryParse(ReadUtf8Column(statement, 1), out observedAt);
            QuotaSnapshot snapshot = ParseHeaderSnapshot(body, observedAt);
            if (snapshot.Windows.Count == 0)
            {
                LastDiagnostic = "local log entry had no standard quota windows";
                return null;
            }

            if (!QuotaFreshness.IsCurrent(snapshot, UnixTime.Now()))
            {
                LastDiagnostic = "local quota stale: record older than 60s, invalid timestamp, or reset time passed";
                return null;
            }

            snapshot.Source = "offline";
            LastDiagnostic = "offline ok";
            return snapshot;
        }
        catch (Exception exception)
        {
            LastDiagnostic = "local quota read failed: " + exception.GetType().Name;
            return null;
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
            request.UserAgent = "codex-quota-local/0.3";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            request.AllowAutoRedirect = false;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
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

            QuotaSnapshot snapshot = ParseRemoteSnapshot(json, UnixTime.Now());
            if (snapshot.Windows.Count == 0)
            {
                LastDiagnostic = "live response had no standard quota windows";
                return null;
            }

            if (!QuotaFreshness.IsCurrent(snapshot, UnixTime.Now()))
            {
                LastDiagnostic = "live quota unavailable: reset time passed; awaiting updated window";
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

    internal static QuotaSnapshot ParseHeaderSnapshot(string body, long observedAt)
    {
        QuotaSnapshot snapshot = new QuotaSnapshot();
        snapshot.ObservedAtUnix = observedAt;
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
                    ? observedAt + resetAfter
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

    internal static QuotaSnapshot ParseRemoteSnapshot(string json, long observedAt)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
        Dictionary<string, object> rateLimit = Dict(root, "rate_limit");
        QuotaSnapshot snapshot = new QuotaSnapshot();
        snapshot.ObservedAtUnix = observedAt;
        AddRemoteWindow(snapshot, rateLimit, "primary_window", "primary");
        AddRemoteWindow(snapshot, rateLimit, "secondary_window", "secondary");

        Dictionary<string, object> credits = Dict(root, "credits") ?? Dict(rateLimit, "credits");
        if (credits != null)
        {
            bool unlimited;
            if (TryBool(Raw(credits, "unlimited"), out unlimited))
                snapshot.CreditsUnlimited = unlimited;

            decimal balance;
            if (TryDecimal(Raw(credits, "balance"), out balance))
            {
                snapshot.CreditBalance = Math.Max(0, balance);
                snapshot.CreditBalanceAvailable = true;
            }
            else
            {
                bool hasCredits;
                if (TryBool(Raw(credits, "has_credits"), out hasCredits) && !hasCredits)
                    snapshot.CreditBalanceAvailable = true;
            }
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

    private static bool TryDecimal(object value, out decimal result)
    {
        try
        {
            if (value == null) { result = 0; return false; }
            result = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch { result = 0; return false; }
    }

    private static bool TryBool(object value, out bool result)
    {
        try
        {
            if (value == null) { result = false; return false; }
            result = Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch { result = false; return false; }
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
    private static readonly Uri OracleEndpoint = new Uri("https://codex-reset.com/api/forecast");
    private static readonly Uri SignalEndpoint = new Uri("https://codexreset.app/api/signal");
    private static readonly Uri WatchEndpoint = new Uri("https://savemetibo.com/status.json");

    public static RadarSnapshot Read()
    {
        RadarSnapshot snapshot = new RadarSnapshot();
        List<string> diagnostics = new List<string>();

        AddOracleReading(snapshot, diagnostics);
        AddSignalReading(snapshot, diagnostics);
        AddWatchReading(snapshot, diagnostics);

        if (snapshot.Readings.Count == 0)
        {
            LastDiagnostic = "radar unavailable: " + String.Join("; ", diagnostics.ToArray());
            return null;
        }

        LastDiagnostic = "radar ok: " + FormatDiagnostic(snapshot);
        if (diagnostics.Count > 0)
            LastDiagnostic += "; unavailable: " + String.Join("; ", diagnostics.ToArray());
        return snapshot;
    }

    private static void AddOracleReading(RadarSnapshot snapshot, List<string> diagnostics)
    {
        try
        {
            Dictionary<string, object> root = FetchJson(OracleEndpoint);
            Dictionary<string, object> probabilities = Dict(root, "probabilities");
            Dictionary<string, object> latestAlert = Dict(root, "latest_alert");
            int value = IntValue(probabilities, "rounded_24h", -1);
            if (value < 0) { diagnostics.Add("oracle missing 24h"); return; }

            snapshot.Readings.Add(new RadarReading
            {
                Name = "oracle",
                Probability24h = value,
                Detail = Str(root, "confidence_note"),
                UpdatedAt = Str(root, "updated_at"),
                Url = "https://codex-reset.com/api/forecast"
            });
            snapshot.Confidence = Str(root, "confidence");
            snapshot.UpdatedAt = Str(root, "updated_at");
            snapshot.LastResetAt = Str(root, "last_reset_at");
            snapshot.LatestSummary = Str(latestAlert, "summary");
            snapshot.LatestUrl = Str(latestAlert, "url");
        }
        catch (Exception exception)
        {
            diagnostics.Add("oracle " + ErrorText(exception));
        }
    }

    private static void AddSignalReading(RadarSnapshot snapshot, List<string> diagnostics)
    {
        try
        {
            Dictionary<string, object> root = FetchJson(SignalEndpoint);
            Dictionary<string, object> forecast = Dict(root, "forecast");
            int value = IntValue(forecast, "probability24h", -1);
            if (value < 0) { diagnostics.Add("signal missing 24h"); return; }

            snapshot.Readings.Add(new RadarReading
            {
                Name = "signal",
                Probability24h = value,
                Detail = Str(forecast, "narrative"),
                UpdatedAt = Str(root, "dataAsOf"),
                Url = "https://codexreset.app/api/signal"
            });
        }
        catch (Exception exception)
        {
            diagnostics.Add("signal " + ErrorText(exception));
        }
    }

    private static void AddWatchReading(RadarSnapshot snapshot, List<string> diagnostics)
    {
        try
        {
            Dictionary<string, object> root = FetchJson(WatchEndpoint);
            object[] changes = ArrayValue(root, "change_log");
            if (changes == null || changes.Length == 0) { diagnostics.Add("watch missing change_log"); return; }
            Dictionary<string, object> latest = changes[0] as Dictionary<string, object>;
            int value = IntValue(latest, "value", -1);
            if (value < 0) { diagnostics.Add("watch missing value"); return; }

            snapshot.Readings.Add(new RadarReading
            {
                Name = "watch",
                Probability24h = value,
                Detail = Str(latest, "text"),
                UpdatedAt = Str(latest, "at"),
                Url = "https://savemetibo.com/status.json"
            });
        }
        catch (Exception exception)
        {
            diagnostics.Add("watch " + ErrorText(exception));
        }
    }

    private static Dictionary<string, object> FetchJson(Uri endpoint)
    {
        if (!IsAllowedEndpoint(endpoint))
            throw new InvalidOperationException("endpoint allowlist failed");

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
        request.Method = "GET";
        request.Accept = "application/json";
        request.UserAgent = "codex-quota-local/0.3";
        request.Timeout = 8000;
        request.ReadWriteTimeout = 8000;

        string json;
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
        {
            if (response.StatusCode != HttpStatusCode.OK)
                throw new WebException("HTTP " + (int)response.StatusCode);
            json = reader.ReadToEnd();
        }

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> root = serializer.DeserializeObject(json) as Dictionary<string, object>;
        if (root == null) throw new InvalidDataException("JSON root was not an object");
        return root;
    }

    private static bool IsAllowedEndpoint(Uri endpoint)
    {
        if (!endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        string host = endpoint.Host.ToLowerInvariant();
        return host.Equals("codex-reset.com") ||
            host.Equals("codexreset.app") ||
            host.Equals("savemetibo.com");
    }

    private static string ErrorText(Exception exception)
    {
        WebException web = exception as WebException;
        if (web != null)
        {
            HttpWebResponse response = web.Response as HttpWebResponse;
            return response == null ? web.Status.ToString() : "HTTP " + (int)response.StatusCode;
        }
        return exception.GetType().Name;
    }

    private static string FormatDiagnostic(RadarSnapshot snapshot)
    {
        List<string> parts = new List<string>();
        foreach (RadarReading reading in snapshot.Readings)
            parts.Add(reading.Name + "=" + reading.Probability24h.ToString() + "%");
        return String.Join(", ", parts.ToArray());
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

    private static object[] ArrayValue(Dictionary<string, object> source, string key)
    {
        return Raw(source, key) as object[];
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
            return value == null ? fallback : Math.Max(0, Math.Min(100, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch { return fallback; }
    }
}

internal static class CodexHost
{
    public static bool IsRunning()
    {
        return IsProcessRunning("ChatGPT") || IsProcessRunning("Codex");
    }

    public static bool IsHostProcessName(string processName)
    {
        return processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProcessRunning(string processName)
    {
        try { return Process.GetProcessesByName(processName).Length > 0; }
        catch { return false; }
    }
}

internal sealed class CodexFollowerContext : ApplicationContext
{
    private readonly string[] args;
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem statusItem;
    private readonly System.Windows.Forms.Timer timer;
    private Process child;

    public CodexFollowerContext(string[] arguments)
    {
        args = arguments;

        ContextMenuStrip menu = new ContextMenuStrip();
        statusItem = new ToolStripMenuItem("Waiting for Codex");
        statusItem.Enabled = false;
        ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit follower");
        exitItem.Click += delegate { ExitThread(); };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        trayIcon = new NotifyIcon();
        trayIcon.Icon = SystemIcons.Information;
        trayIcon.Text = "Codex Quota Follower";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 2000;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Tick();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        StopChild();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.ExitThreadCore();
    }

    private void Tick()
    {
        bool hostRunning = CodexHost.IsRunning();
        bool childRunning = child != null && !child.HasExited;
        if (hostRunning && !childRunning)
            child = Program.StartOverlayChild(args);
        if (!hostRunning && childRunning)
        {
            StopChild();
            child = null;
        }

        statusItem.Text = hostRunning ? "Codex running" : "Waiting for Codex";
        trayIcon.Text = hostRunning ? "Codex Quota Follower: running" : "Codex Quota Follower: waiting";
    }

    private void StopChild()
    {
        if (child == null || child.HasExited) return;
        try
        {
            if (!child.CloseMainWindow()) child.Kill();
        }
        catch { }
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
    private bool balanceMode;
    private bool exitWithCodex;
    private bool hostSeen;
    private int hostMissingSeconds;
    private int quotaIntervalSeconds;
    private int radarIntervalSeconds;
    private readonly Label label;
    private readonly NotifyIcon trayIcon;
    private readonly System.Windows.Forms.Timer timer;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem liveItem;
    private readonly ToolStripMenuItem radarItem;
    private readonly ToolStripMenuItem balanceItem;
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

    public QuotaOverlayForm(QuotaReadMode mode, bool radar, bool balance, int quotaInterval, int radarInterval, bool exitWithHost)
    {
        quotaMode = mode;
        radarMode = radar;
        balanceMode = balance && mode != QuotaReadMode.OfflineOnly;
        exitWithCodex = exitWithHost;
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
        balanceItem = new ToolStripMenuItem("Credit balance: off");
        balanceItem.Click += delegate { SetBalanceMode(!balanceMode); };
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
        menu.Items.Add(balanceItem);
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
        if (ShouldExitWithCodex()) { Close(); return; }
        if (latest != null && !QuotaFreshness.IsCurrent(latest, UnixTime.Now()))
        {
            latest = null;
            label.Text = FormatSnapshot(null, latestRadar, balanceMode);
            trayIcon.Text = ShortTrayText(label.Text);
            UpdateMenuState(null, latestRadar);
            quotaCountdown = 0;
        }
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
        QuotaReadMode readMode = quotaMode;
        bool readRadarEnabled = radarMode;
        bool readBalanceEnabled = balanceMode;
        ThreadPool.QueueUserWorkItem(delegate
        {
            QuotaSnapshot snapshot = readQuota ? QuotaReader.Read(readMode, readBalanceEnabled) : latest;
            RadarSnapshot radar = readRadar ? RadarReader.Read() : latestRadar;
            try
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) { Interlocked.Exchange(ref refreshInProgress, 0); return; }
                        if (readMode != quotaMode || readBalanceEnabled != balanceMode)
                        {
                            Interlocked.Exchange(ref refreshInProgress, 0);
                            quotaCountdown = 0;
                            RefreshData(false);
                            return;
                        }
                        if (!radarMode) radar = null;
                        latest = snapshot;
                        latestRadar = radar;
                        label.Text = FormatSnapshot(snapshot, radar, balanceMode);
                        trayIcon.Text = ShortTrayText(label.Text);
                        UpdateMenuState(snapshot, radar);
                        if (readQuota) quotaCountdown = quotaIntervalSeconds;
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
        if (quotaMode == QuotaReadMode.OfflineOnly) balanceMode = false;
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
        label.Text = FormatSnapshot(latest, latestRadar, balanceMode);
        trayIcon.Text = ShortTrayText(label.Text);
        UpdateMenuState(latest, latestRadar);
        if (radarMode) RefreshData(true);
    }

    private void SetBalanceMode(bool enabled)
    {
        if (quotaMode == QuotaReadMode.OfflineOnly || balanceMode == enabled) return;
        balanceMode = enabled;
        quotaCountdown = 0;
        latest = null;
        label.Text = InitialQuotaText(quotaMode);
        trayIcon.Text = ShortTrayText(label.Text);
        UpdateMenuState(null, latestRadar);
        RefreshData(false);
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
        balanceItem.Enabled = quotaMode != QuotaReadMode.OfflineOnly;
        balanceItem.Checked = balanceMode;
        balanceItem.Text = balanceMode ? "Credit balance: on" : "Credit balance: off";
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
        if (QuotaReader.LastDiagnostic != "not started")
            return "Quota unavailable: " + QuotaReader.LastDiagnostic;
        if (mode == QuotaReadMode.Auto)
            return "Quota mode: auto (logs, then live fallback)";
        return "Quota mode: " + QuotaModeText(mode);
    }

    private static string FormatRadarMenuText(RadarSnapshot radar)
    {
        if (radar == null) return "Reset radar: waiting";
        return "Reset radar 24h: " + FormatRadarValues(radar) + " (oracle/signal/watch)";
    }

    private static string FormatSnapshot(QuotaSnapshot snapshot, RadarSnapshot radar, bool showBalance)
    {
        List<string> output = new List<string>();
        if (!QuotaFreshness.IsCurrent(snapshot, UnixTime.Now())) output.Add("Quota: -- (stale/unavailable)");
        else output.Add(FormatQuota(snapshot));

        if (showBalance)
            output.Add(FormatCreditBalance(snapshot));

        if (radar != null)
        {
            output.Add("Radar 24h " + FormatRadarValues(radar));
        }

        return String.Join("  |  ", output.ToArray());
    }

    internal static string FormatCreditBalance(QuotaSnapshot snapshot)
    {
        if (snapshot == null) return "Credits --";
        if (snapshot.CreditsUnlimited) return "Credits unlimited";
        if (!snapshot.CreditBalanceAvailable) return "Credits --";
        return "Credits " + snapshot.CreditBalance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatRadarValues(RadarSnapshot radar)
    {
        string[] names = new string[] { "oracle", "signal", "watch" };
        List<string> values = new List<string>();
        foreach (string name in names)
        {
            RadarReading reading = FindRadarReading(radar, name);
            values.Add(reading == null ? "--" : reading.Probability24h.ToString() + "%");
        }
        return String.Join("/", values.ToArray());
    }

    private static RadarReading FindRadarReading(RadarSnapshot radar, string name)
    {
        if (radar == null) return null;
        foreach (RadarReading reading in radar.Readings)
            if (reading.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return reading;
        return null;
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
            return CodexHost.IsHostProcessName(processName);
        }
        catch { return false; }
    }

    private bool ShouldExitWithCodex()
    {
        if (!exitWithCodex) return false;
        if (CodexHost.IsRunning())
        {
            hostSeen = true;
            hostMissingSeconds = 0;
            return false;
        }

        if (!hostSeen) return false;
        hostMissingSeconds++;
        return hostMissingSeconds >= 5;
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
        bool balance = HasArg(args, "--balance") && !offlineOnly;
        bool snapshot = HasArg(args, "--snapshot");
        bool followCodex = HasArg(args, "--follow-codex");
        bool exitWithCodex = HasArg(args, "--exit-with-codex");
        int quotaInterval = IntArg(args, "--quota-interval-seconds", 10);
        int radarInterval = IntArg(args, "--radar-interval-minutes", 10) * 60;
        QuotaReadMode quotaMode = offlineOnly ? QuotaReadMode.OfflineOnly : (live ? QuotaReadMode.LiveFirst : QuotaReadMode.Auto);

        if (snapshot)
        {
            QuotaSnapshot data = QuotaReader.Read(quotaMode, balance);
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
            Console.WriteLine("observed_at: " + UnixTime.ToLocal(data.ObservedAtUnix).ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("diagnostic: " + QuotaReader.LastDiagnostic);
            if (balance)
            {
                Console.WriteLine("credits_balance: " + (data.CreditsUnlimited
                    ? "unlimited"
                    : (data.CreditBalanceAvailable
                        ? data.CreditBalance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        : "unavailable")));
            }
            if (radar)
            {
                RadarSnapshot radarData = RadarReader.Read();
                if (radarData == null)
                {
                    Console.WriteLine("radar: NO_DATA: " + RadarReader.LastDiagnostic);
                }
                else
                {
                    foreach (RadarReading reading in radarData.Readings)
                    {
                        Console.WriteLine("radar_24h_{0}: {1}%", reading.Name, reading.Probability24h);
                        Console.WriteLine("radar_updated_at_{0}: {1}", reading.Name, reading.UpdatedAt);
                        Console.WriteLine("radar_url_{0}: {1}", reading.Name, reading.Url);
                    }
                    Console.WriteLine("radar_confidence: {0}", radarData.Confidence);
                    Console.WriteLine("radar_updated_at: {0}", radarData.UpdatedAt);
                    Console.WriteLine("radar_last_reset_at: {0}", radarData.LastResetAt);
                    Console.WriteLine("radar_latest_url: {0}", radarData.LatestUrl);
                    Console.WriteLine("radar_diagnostic: " + RadarReader.LastDiagnostic);
                }
            }
            return;
        }

        if (followCodex)
        {
            RunCodexFollower(args);
            return;
        }

        bool ownsMutex;
        using (Mutex mutex = new Mutex(true, "Local\\CodexQuotaLocal" + quotaMode.ToString() + (radar ? "Radar" : "") + (balance ? "Balance" : ""), out ownsMutex))
        {
            if (!ownsMutex) return;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new QuotaOverlayForm(quotaMode, radar, balance, quotaInterval, radarInterval, exitWithCodex));
        }
    }

    private static void RunCodexFollower(string[] args)
    {
        bool ownsMutex;
        using (Mutex mutex = new Mutex(true, "Local\\CodexQuotaLocalFollower" + (HasArg(args, "--radar") ? "Radar" : ""), out ownsMutex))
        {
            if (!ownsMutex) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CodexFollowerContext(args));
        }
    }

    public static Process StartOverlayChild(string[] args)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = Application.ExecutablePath;
        startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
        startInfo.Arguments = BuildOverlayArguments(args);
        startInfo.UseShellExecute = false;
        return Process.Start(startInfo);
    }

    private static string BuildOverlayArguments(string[] args)
    {
        List<string> output = new List<string>();
        if (HasArg(args, "--live")) output.Add("--live");
        if (HasArg(args, "--offline-only")) output.Add("--offline-only");
        if (HasArg(args, "--radar")) output.Add("--radar");
        if (HasArg(args, "--balance")) output.Add("--balance");
        output.Add("--exit-with-codex");
        AddIntArgument(args, output, "--quota-interval-seconds");
        AddIntArgument(args, output, "--radar-interval-minutes");
        return String.Join(" ", output.ToArray());
    }

    private static void AddIntArgument(string[] args, List<string> output, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            int value;
            if (Int32.TryParse(args[i + 1], out value) && value > 0)
            {
                output.Add(name);
                output.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return;
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
