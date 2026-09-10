using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class QuotaReaderTests
{
    private static int passed;
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open(byte[] filename, out IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static string Headers(long reset, bool relative)
    {
        return "{\"x-codex-primary-window-minutes\":\"300\",\"x-codex-primary-used-percent\":\"80\",\"x-codex-primary-" +
            (relative ? "reset-after-seconds" : "reset-at") + "\":\"" + reset + "\"}";
    }

    private static void WriteLog(string path, long timestamp, string headers)
    {
        IntPtr database;
        if (sqlite3_open(Encoding.UTF8.GetBytes(path + "\0"), out database) != 0)
            throw new Exception("Fixture database open failed");
        try
        {
            string sql = "CREATE TABLE IF NOT EXISTS logs (id INTEGER PRIMARY KEY, ts INTEGER, ts_nanos INTEGER, feedback_log_body TEXT);" +
                "DELETE FROM logs; INSERT INTO logs VALUES (1," + timestamp + ",0,'" + headers.Replace("'", "''") + "');";
            if (sqlite3_exec(database, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                throw new Exception("Fixture database write failed");
        }
        finally { sqlite3_close(database); }
    }

    private static void Main()
    {
        long now = UnixTime.Now();
        QuotaSnapshot fresh = QuotaReader.ParseHeaderSnapshot(Headers(now + 3600, false), now);
        Check(QuotaFreshness.IsCurrent(fresh, now), "fresh log accepted");
        Check(!QuotaFreshness.IsCurrent(fresh, now + 60), "age threshold expires even before reset");
        Check(!QuotaFreshness.IsCurrent(fresh, now - 60), "future timestamp rejected");
        QuotaSnapshot expired = QuotaReader.ParseHeaderSnapshot(Headers(now - 1, false), now);
        Check(!QuotaFreshness.IsCurrent(expired, now), "passed reset rejected even with new log timestamp");
        Check(!QuotaFreshness.IsCurrent(QuotaReader.ParseHeaderSnapshot(Headers(now, false), now), now), "reset boundary rejected");
        Check(!QuotaFreshness.IsCurrent(QuotaReader.ParseHeaderSnapshot(Headers(now + 3600, false), 0), now), "missing timestamp rejected");
        QuotaSnapshot relative = QuotaReader.ParseHeaderSnapshot(Headers(300, true), now - 120);
        Check(relative.Windows[0].ResetAtUnix == now + 180, "relative reset anchored to log time");
        Check(QuotaReader.ParseHeaderSnapshot(Headers(300, true), now - 120).Windows[0].ResetAtUnix == relative.Windows[0].ResetAtUnix,
            "re-reading does not move reset time");

        string directory = Path.Combine(Path.GetTempPath(), "codex-quota-test-" + Guid.NewGuid().ToString("N"));
        string previous = Environment.GetEnvironmentVariable("CODEX_QUOTA_DATA_DIR");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("CODEX_QUOTA_DATA_DIR", directory);
        string path = Path.Combine(directory, "logs_2.sqlite");
        try
        {
            WriteLog(path, now, Headers(now + 3600, false));
            QuotaSnapshot local = QuotaReader.Read(QuotaReadMode.OfflineOnly);
            Check(local != null && local.ObservedAtUnix == now && local.Windows[0].UsedPercent == 80, "SQLite timestamp and quota read together");
            WriteLog(path, now - 3600, Headers(now + 3600, false));
            Check(QuotaReader.Read(QuotaReadMode.OfflineOnly) == null && QuotaReader.LastDiagnostic.Contains("stale"), "old SQLite record rejected despite future reset");
            WriteLog(path, now, Headers(now - 86400, false));
            Check(QuotaReader.Read(QuotaReadMode.OfflineOnly) == null, "screenshot regression: yesterday reset rejected");

            int remoteCalls = 0;
            Func<QuotaSnapshot> logs = delegate { return QuotaReader.Read(QuotaReadMode.OfflineOnly); };
            Func<QuotaSnapshot> remote = delegate { remoteCalls++; return fresh; };
            Check(QuotaReader.Read(QuotaReadMode.Auto, logs, remote) == fresh && remoteCalls == 1, "auto queries live when logs stale");
            remoteCalls = 0;
            Check(QuotaReader.Read(QuotaReadMode.OfflineOnly, logs, remote) == null && remoteCalls == 0, "offline never calls live");
            Func<QuotaSnapshot> unavailable = delegate { QuotaReader.LastDiagnostic = "live unavailable"; return null; };
            Check(QuotaReader.Read(QuotaReadMode.Auto, logs, unavailable) == null, "live failure does not resurrect stale logs");
            Check(QuotaReader.Read(QuotaReadMode.LiveFirst, logs, unavailable) == null, "live-first fallback rejects stale logs");
            WriteLog(path, now, Headers(now + 3600, false));
            Check(QuotaReader.Read(QuotaReadMode.Auto, logs, remote) != null && remoteCalls == 0, "fresh local data avoids network");
            Check(QuotaReader.Read(QuotaReadMode.LiveFirst, logs, unavailable) != null, "live failure can use fresh local data");
            WriteLog(path, now, "invalid");
            Check(QuotaReader.Read(QuotaReadMode.OfflineOnly) == null, "missing headers reported as unavailable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_QUOTA_DATA_DIR", previous);
            File.Delete(path);
            Directory.Delete(directory);
        }
        Console.WriteLine("Passed " + passed + " tests (synthetic data, no network or credentials).");
    }
}
