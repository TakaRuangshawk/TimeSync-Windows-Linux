using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TimeSync_FromHttpDateHeader
{
    internal class Program
    {
        private static Mutex _mutex;
        private const string USER_AGENT = "TimeSync-Client";
        private static HttpClient _client;
        private static readonly object _logLock = new object();

        static int Main(string[] args)
        {
            // กันรันซ้อน
            bool createdNew;
            _mutex = new Mutex(true, "Global\\TimeSync_FromHttpDateHeader", out createdNew);
            if (!createdNew)
            {
                Console.WriteLine("❌ TimeSync is already running.");
                return 1;
            }

            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            finally
            {
                try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            // TLS1.2
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            // โหลด config
            bool ignoreCert = GetBool("IgnoreSslErrors", true);
            int timeoutSec = GetInt("HttpTimeoutSec", 12);
            bool useHeadThenGet = GetBool("UseHeadThenGet", true);
            bool runOnce = GetBool("RunOnce", true);
            int loopIntervalMin = GetInt("LoopIntervalMinutes", 1);  // ใช้เป็น "เวลาหน่วงก่อนเริ่ม" ด้วย

            var targets = BuildCandidateUrls();
            if (targets.Count == 0)
            {
                LogError("No target URLs configured. Please set TimeUrls/TimeHosts/TimePorts.");
                return 2;
            }

            // หน่วงก่อนเริ่มทำงานจริง เพื่อเลี่ยงช่วงเครื่องเพิ่งบูต/แย่งทรัพยากร
            if (loopIntervalMin > 0)
            {
                Console.WriteLine($"⏳ Delay {loopIntervalMin} minute(s) before starting...");
                await Task.Delay(TimeSpan.FromMinutes(loopIntervalMin));
            }

            // สร้าง HttpClient reuse
            _client = CreateHttpClient(ignoreCert, timeoutSec);
            _client.DefaultRequestHeaders.UserAgent.Clear();
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(USER_AGENT);

            if (runOnce)
            {
                return await RunOnceWithSingleRetryAsync(targets, useHeadThenGet, loopIntervalMin);
            }
            else
            {
                await RunLoopAsync(targets, useHeadThenGet, loopIntervalMin);
                return 0;
            }
        }

        // ---------- โหมดรอบเดียว: หน่วงก่อนเริ่ม + ถ้าพลาดให้รอ interval แล้วลองซ้ำ 1 ครั้ง ----------
        private static async Task<int> RunOnceWithSingleRetryAsync(List<string> targets, bool useHeadThenGet, int loopIntervalMin)
        {
            try
            {
                var dt = await FetchFirstServerTimeAsync(_client, targets, useHeadThenGet);
                SetWindowsTimeUtc(dt);
                Console.WriteLine($"✅ Windows time set to (local): {dt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                return 0;
            }
            catch (Exception ex)
            {
                LogError("RunOnce first attempt failed", ex);
                Console.Error.WriteLine("❌ First attempt failed. Will retry once...");

                // รอเท่าเดิมแล้วลองใหม่ 1 ครั้ง
                if (loopIntervalMin > 0)
                    await Task.Delay(TimeSpan.FromMinutes(loopIntervalMin));

                try
                {
                    var dt = await FetchFirstServerTimeAsync(_client, targets, useHeadThenGet);
                    SetWindowsTimeUtc(dt);
                    Console.WriteLine($"✅ Windows time set to (local): {dt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    return 0;
                }
                catch (Exception ex2)
                {
                    LogError("RunOnce second attempt failed", ex2);
                    Console.Error.WriteLine("❌ Second attempt failed.");
                    return 1;
                }
            }
        }

        // ---------- โหมดวนลูป: ถ้า error ให้ log แล้ว "รอ interval เดิม" แล้วลองใหม่ ไม่เด้งตาย ----------
        private static async Task RunLoopAsync(List<string> targets, bool useHeadThenGet, int loopIntervalMin)
        {
            Console.WriteLine($"=== TimeSync loop every {loopIntervalMin} minute(s) ===");

            while (true)
            {
                try
                {
                    var dt = await FetchFirstServerTimeAsync(_client, targets, useHeadThenGet);
                    SetWindowsTimeUtc(dt);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Synced: {dt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                }
                catch (Exception ex)
                {
                    LogError("Loop iteration failed", ex);
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ {ex.Message}");
                    if (ex.InnerException != null)
                        Console.Error.WriteLine("   Inner: " + ex.InnerException.Message);
                }

                await Task.Delay(TimeSpan.FromMinutes(loopIntervalMin));
            }
        }

        // ---------- HTTP ----------
        private static HttpClient CreateHttpClient(bool ignoreCert, int timeoutSec)
        {
            var handler = new HttpClientHandler();
            if (ignoreCert)
                handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errs) => true;

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSec)
            };
        }

        private static async Task<DateTime> FetchFirstServerTimeAsync(HttpClient client, List<string> urls, bool headThenGet)
        {
            foreach (var url in urls)
            {
                try
                {
                    // HEAD ก่อน
                    if (headThenGet)
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Head, url))
                        {
                            req.Headers.TryAddWithoutValidation("X-TimeSync", "1"); // ให้ฝั่ง Linux กรองได้
                            using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                            {
                                var dt = TryReadTimeFromHeaders(resp);
                                if (dt.HasValue) return dt.Value;
                            }
                        }
                    }

                    // Fallback GET
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.TryAddWithoutValidation("X-TimeSync", "1");
                        using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                        {
                            var dt = TryReadTimeFromHeaders(resp);
                            if (dt.HasValue) return dt.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Target failed: {url}", ex);
                    Console.WriteLine($"Try: {url}");
                    Console.WriteLine($"  -> {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null)
                        Console.WriteLine($"     Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                }
            }

            throw new InvalidOperationException("ไม่สามารถดึงเวลาได้จากทุกเป้าหมายที่กำหนด");
        }

        private static DateTime? TryReadTimeFromHeaders(HttpResponseMessage resp)
        {
            if (resp.Headers.Date.HasValue) return resp.Headers.Date.Value.UtcDateTime;
            if (resp.Content?.Headers?.LastModified != null) return resp.Content.Headers.LastModified.Value.UtcDateTime;
            return null;
        }

        // ---------- URL builder ----------
        private static List<string> BuildCandidateUrls()
        {
            var urls = new List<string>();

            void AddMany(string csv)
            {
                if (string.IsNullOrWhiteSpace(csv)) return;
                foreach (var s in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = s.Trim();
                    if (!string.IsNullOrEmpty(t)) urls.Add(t);
                }
            }

            // URL ตรง ๆ ก่อน
            AddMany(ConfigurationManager.AppSettings["TimeUrls"]);

            // ผสม Host×Port×Path
            var hosts = (ConfigurationManager.AppSettings["TimeHosts"] ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

            var ports = (ConfigurationManager.AppSettings["TimePorts"] ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

            var paths = (ConfigurationManager.AppSettings["TimePaths"] ?? "/")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();

            foreach (var h in hosts)
                foreach (var p in ports)
                    foreach (var path in paths)
                    {
                        var normalizedPath = path.StartsWith("/") ? path : "/" + path;
                        urls.Add($"https://{h}:{p}{normalizedPath}");
                    }

            // Fallback
            AddMany(ConfigurationManager.AppSettings["FallbackTimeUrls"]);

            return urls.Distinct().ToList();
        }

        // ---------- Logging (เฉพาะตอนผิดพลาด) ----------
        private static void LogError(string message, Exception ex = null)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logDir = Path.Combine(baseDir, "log");
                Directory.CreateDirectory(logDir);

                var logFile = Path.Combine(logDir, $"timesync_{DateTime.Now:yyyyMMdd}.log");
                var lines = new List<string> { $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ❌ {message}" };
                if (ex != null)
                {
                    lines.Add($"   Error: {ex.Message}");
                    if (ex.InnerException != null)
                        lines.Add($"   Inner: {ex.InnerException.Message}");
                }

                lock (_logLock)
                {
                    File.AppendAllLines(logFile, lines);
                }
            }
            catch { /* ignore logging errors */ }
        }

        // ---------- ตั้งเวลา Windows (UTC) ----------
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetSystemTime(ref SYSTEMTIME st);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        private static void SetWindowsTimeUtc(DateTime dt)
        {
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            var st = new SYSTEMTIME
            {
                Year = (ushort)utc.Year,
                Month = (ushort)utc.Month,
                Day = (ushort)utc.Day,
                Hour = (ushort)utc.Hour,
                Minute = (ushort)utc.Minute,
                Second = (ushort)utc.Second,
                Milliseconds = (ushort)utc.Millisecond
            };
            if (!SetSystemTime(ref st))
            {
                int err = Marshal.GetLastWin32Error();
                LogError($"SetSystemTime failed. Win32Error={err}");
                throw new InvalidOperationException($"SetSystemTime failed. Win32Error={err}");
            }
        }

        // ---------- Helpers ----------
        private static bool GetBool(string key, bool def)
            => bool.TryParse(ConfigurationManager.AppSettings[key], out var b) ? b : def;

        private static int GetInt(string key, int def)
            => int.TryParse(ConfigurationManager.AppSettings[key], out var n) ? n : def;
    }
}
