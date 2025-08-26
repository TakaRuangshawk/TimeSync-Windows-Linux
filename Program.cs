using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TimeSync_FromHttpDateHeader
{
    internal class Program
    {
        private static Mutex _mutex;

        static int Main(string[] args)
        {
            bool createdNew;
            _mutex = new Mutex(true, "Global\\TimeSync_FromHttpDateHeader", out createdNew);

            if (!createdNew)
            {
                // มี instance อื่นรันอยู่แล้ว
                Console.WriteLine("❌ TimeSync is already running.");
                return 1;
            }

            try
            {
                return RunMain(args);
            }
            finally
            {
                // ปล่อย Mutex ตอนโปรแกรมจบ
                _mutex.ReleaseMutex();
            }
        }

        static int RunMain(string[] args)
        {
            // บังคับ TLS1.2 สำหรับ .NET Framework 4.7
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var ignoreCert = GetBool("IgnoreSslErrors", true);
            var timeoutSec = GetInt("HttpTimeoutSec", 12);
            var useHeadThenGet = GetBool("UseHeadThenGet", true);   // ถ้าไม่ใส่ใน config ค่านี้จะเป็น false
            var runOnce = GetBool("RunOnce", true);          // ถ้าไม่ใส่ใน config จะ RunOnce เป็นค่าเริ่มต้น
            var loopIntervalMin = GetInt("LoopIntervalMinutes", 1);  // ใช้เฉพาะโหมด loop

            var targets = BuildCandidateUrls();
            if (targets.Count == 0)
            {
                LogError("No target URLs configured. Please set TimeUrls/TimeHosts/TimePorts.");
                return 2;
            }

            if (runOnce)
            {
                try
                {
                    var dt = FetchFirstServerTime(targets, ignoreCert, TimeSpan.FromSeconds(timeoutSec), useHeadThenGet)
                             .GetAwaiter().GetResult();
                    SetWindowsTimeUtc(dt);
                    Console.WriteLine($"✅ Windows time set to (local): {dt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    return 0;
                }
                catch (Exception ex)
                {
                    LogError("RunOnce failed", ex);
                    Console.Error.WriteLine("❌ " + ex.Message);
                    if (ex.InnerException != null) Console.Error.WriteLine("Inner: " + ex.InnerException.Message);
                    return 1;
                }
            }
            else
            {
                Console.WriteLine($"=== TimeSync started (loop every {loopIntervalMin} minute(s)) ===");
                while (true)
                {
                    try
                    {
                        var dt = FetchFirstServerTime(targets, ignoreCert, TimeSpan.FromSeconds(timeoutSec), useHeadThenGet)
                                 .GetAwaiter().GetResult();
                        SetWindowsTimeUtc(dt);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ Synced Windows time to: {dt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    }
                    catch (Exception ex)
                    {
                        LogError("Loop iteration failed", ex);
                        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ Error: {ex.Message}");
                        if (ex.InnerException != null)
                            Console.Error.WriteLine("   Inner: " + ex.InnerException.Message);
                    }

                    System.Threading.Thread.Sleep(TimeSpan.FromMinutes(loopIntervalMin));
                }
            }
        }

        static List<string> BuildCandidateUrls()
        {
            // TimeUrls (เจาะจง) -> Hosts×Ports×Paths -> FallbackTimeUrls
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

            // 4) URL เฉพาะเจาะจงก่อน
            AddMany(ConfigurationManager.AppSettings["TimeUrls"]);

            // 1–3) สร้างคอมบิเนชัน host×port×path (https เท่านั้น)
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

            // 5) Fallback สุดท้าย
            AddMany(ConfigurationManager.AppSettings["FallbackTimeUrls"]);

            // ตัดซ้ำ รักษาลำดับ
            return urls.Distinct().ToList();
        }

        static async Task<DateTime> FetchFirstServerTime(List<string> urls, bool ignoreCert, TimeSpan timeout, bool headThenGet)
        {
            var handler = new HttpClientHandler();
            if (ignoreCert)
            {
                handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errs) => true;
            }

            using (var client = new HttpClient(handler) { Timeout = timeout })
            {
                foreach (var url in urls)
                {
                    try
                    {
                        Console.WriteLine($"Try: {url}");

                        // HEAD ก่อน
                        if (headThenGet)
                        {
                            using (var req = new HttpRequestMessage(HttpMethod.Head, url))
                            using (var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                            {
                                var dt = TryReadTimeFromHeaders(resp);
                                if (dt.HasValue) return dt.Value;
                            }
                        }

                        // Fallback GET
                        using (var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                        {
                            var dt = TryReadTimeFromHeaders(resp);
                            if (dt.HasValue) return dt.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        // ✅ เขียน log เมื่อ "ติดต่อ URL นี้ไม่ได้" หรือ error ใด ๆ ระหว่างดึงเวลา
                        LogError($"Target failed: {url}", ex);
                        Console.WriteLine($"  -> {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException != null)
                            Console.WriteLine($"     Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                }
            }

            throw new InvalidOperationException("ไม่สามารถดึงเวลาได้จากทุกเป้าหมายที่กำหนด");
        }

        static DateTime? TryReadTimeFromHeaders(HttpResponseMessage resp)
        {
            // ใช้ Date header ก่อน (RFC1123), ถ้าไม่มีลอง Last-Modified
            if (resp.Headers.Date.HasValue) return resp.Headers.Date.Value.UtcDateTime;
            if (resp.Content?.Headers?.LastModified != null) return resp.Content.Headers.LastModified.Value.UtcDateTime;
            return null;
        }

        static bool GetBool(string key, bool def)
            => bool.TryParse(ConfigurationManager.AppSettings[key], out var b) ? b : def;

        static int GetInt(string key, int def)
            => int.TryParse(ConfigurationManager.AppSettings[key], out var n) ? n : def;

        // ---- Logging: เฉพาะ error/ต่อไม่ได้, แยกไฟล์รายวันในโฟลเดอร์ ./log ข้าง .exe ----
        static void LogError(string message, Exception ex = null)
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logDir = Path.Combine(baseDir, "log");
                Directory.CreateDirectory(logDir);

                var logFile = Path.Combine(logDir, $"timesync_{DateTime.Now:yyyyMMdd}.log");

                using (var sw = new StreamWriter(logFile, true))
                {
                    sw.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ❌ {message}");
                    if (ex != null)
                    {
                        sw.WriteLine($"   Error: {ex.Message}");
                        if (ex.InnerException != null)
                            sw.WriteLine($"   Inner: {ex.InnerException.Message}");
                    }
                }
            }
            catch
            {
                // เงียบไว้ ถ้าเขียน log ไม่ได้
            }
        }

        // ---- ตั้งเวลา Windows (UTC) ----
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetSystemTime(ref SYSTEMTIME st);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        static void SetWindowsTimeUtc(DateTime dt)
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
                // ✅ ล็อกเมื่อ SetSystemTime ล้มเหลว
                LogError($"SetSystemTime failed. Win32Error={err}");
                throw new InvalidOperationException($"SetSystemTime failed. Win32Error={err}");
            }
        }
    }
}
