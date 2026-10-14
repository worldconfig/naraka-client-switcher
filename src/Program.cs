// ============================================================================
//  永劫无间 · 免下载切客户端 / 客户端重建工具
//  单文件 WinForms 程序，目标框架 .NET Framework 4.8（Win10/11 自带）
//
//  原理：
//    同一款游戏在不同渠道（网易国服 / Steam / Epic）的文件 99.6% 完全相同。
//    本工具读取目标渠道的官方文件清单（网易的 data.xml，含每个文件的 MD5 与大小），
//    把已有客户端里"内容一致"的文件用硬链接（同卷零额外空间）复用过去，
//    只从官方 CDN 下载真正缺失/不同的那几百 MB。
//
//  两种模式：
//    A. 按官方清单重建国服 —— 需要 data.xml，逐文件 MD5 校验，最严格
//    B. 整树播种 —— 不需要清单，把已有客户端整棵硬链接到目标目录，
//       之后交给 Epic / Steam 自带的"验证文件完整性"补齐差异
//
//  编译：
//    csc /target:winexe /codepage:65001 /r:System.Windows.Forms.dll ... Program.cs
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

[assembly: System.Reflection.AssemblyTitle("永劫无间 免下载切客户端")]
[assembly: System.Reflection.AssemblyDescription("用已有客户端重建目标渠道客户端，只下载真正的差异")]
[assembly: System.Reflection.AssemblyProduct("Naraka Client Switcher")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace NarakaSwitch
{
    // ------------------------------------------------------------------ 原生
    internal static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public static void CreateHardLink(string linkPath, string existingPath)
        {
            if (!CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
                throw new IOException("CreateHardLink 失败, Win32 错误 " + Marshal.GetLastWin32Error());
        }
    }

    // ------------------------------------------------------------------ 数据
    internal class ManifestItem
    {
        public string Path;      // 形如 program/xxx.dll 或 LauncherGame.exe
        public string Md5;
        public long SizeKb;      // 官方清单里的大小（KB，向下取整）
        public override string ToString() { return Path; }
    }

    internal class Manifest
    {
        public string Version = "";
        public string Cdn = "";
        public long TotalKb;
        public List<ManifestItem> Items = new List<ManifestItem>();
    }

    internal class PlanItem
    {
        public ManifestItem Item;
        public string Action;    // link | download | copy
        public string Reason;    // md5-match / size-match / no-source / md5-mismatch / size-mismatch
        public long SrcBytes = -1;
        public string Target;
        public string Source;
        public string Md5 { get { return Item != null ? Item.Md5 : ""; } }
        public string Path { get { return Item != null ? Item.Path : ""; } }
    }

    internal enum JobKind { Scan, Execute, Verify, Seed, SeedExecute, DownloadManifest }

    internal class JobProgress
    {
        public JobKind Kind;
        public int Done;
        public int Total;
        public string Phase = "";        // 当前阶段：扫描中 / 建立链接 / 下载差集 / 验收中 / 播种中
        public string Message = "";
        public volatile bool Cancelled;  // 由界面线程写入、工作线程读取
    }

    // ------------------------------------------------------------------ 引擎
    internal static class Engine
    {
        public const string OfficialManifestUrl = "https://update.yjwujian.cn/publish/data.xml";

        public static string GetMd5(string path)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                byte[] buf = new byte[4 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    md5.TransformBlock(buf, 0, n, null, 0);
                md5.TransformFinalBlock(new byte[0], 0, 0);
                var sb = new StringBuilder();
                foreach (byte b in md5.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static Manifest LoadManifest(string path)
        {
            var m = new Manifest();
            var doc = new XmlDocument();
            doc.Load(path);
            var cfg = doc.DocumentElement.SelectSingleNode("config");
            if (cfg != null)
            {
                var v = cfg.SelectSingleNode("TargetVersion");
                var c = cfg.SelectSingleNode("CDNDomain");
                var t = cfg.SelectSingleNode("DownloadSizeKB");
                if (v != null) m.Version = v.Attributes["value"].Value.Trim();
                if (c != null) m.Cdn = c.Attributes["value"].Value.Trim().TrimEnd('/');
                if (t != null) long.TryParse(t.Attributes["value"].Value, out m.TotalKb);
            }
            var nodes = doc.DocumentElement.SelectNodes("file/item");
            foreach (XmlNode node in nodes)
            {
                var it = new ManifestItem();
                it.Path = node.Attributes["path"].Value;
                it.Md5 = node.Attributes["md5"] != null ? node.Attributes["md5"].Value : "";
                long kb;
                long.TryParse(node.Attributes["size"].Value, out kb);
                it.SizeKb = kb;
                m.Items.Add(it);
            }
            return m;
        }

        public static void DownloadFile(string url, string dest, string reportDir)
        {
            var dir = System.IO.Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = dest + ".dltmp";
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "Mozilla/5.0");
                wc.DownloadFile(url, tmp);
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);
        }

        /// <summary>在源客户端里找一个与清单条目对应的文件（兼容用户选到根目录或 program 子目录）</summary>
        public static string ResolveSource(string sourceRoot, string manifestPath)
        {
            string[] cands;
            if (manifestPath.StartsWith("program/", StringComparison.OrdinalIgnoreCase))
            {
                string rest = manifestPath.Substring(8);
                cands = new string[] { Path.Combine(sourceRoot, rest.Replace('/', '\\')) };
            }
            else
            {
                cands = new string[] { Path.Combine(sourceRoot, manifestPath.Replace('/', '\\')) };
            }
            foreach (var c in cands) if (File.Exists(c)) return c;
            // 兜底：源目录本身就是 program 的父目录时
            string alt = Path.Combine(sourceRoot, manifestPath.Replace('/', '\\'));
            if (File.Exists(alt)) return alt;
            return cands[0];
        }

        /// <summary>两个路径是否在同一个卷上（硬链接只能同卷）</summary>
        public static bool SameVolume(string a, string b)
        {
            try
            {
                string ra = Path.GetPathRoot(Path.GetFullPath(a));
                string rb = Path.GetPathRoot(Path.GetFullPath(b));
                return string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------------------------------------------------- 扫描
        public static List<PlanItem> Scan(Manifest m, string sourceRoot, string targetRoot,
                                          bool skipHash, JobProgress jp, Action<string> log)
        {
            var plan = new List<PlanItem>(m.Items.Count);
            var sw = Stopwatch.StartNew();
            long hashed = 0;
            if (jp != null) { jp.Phase = "扫描中"; jp.Total = m.Items.Count; jp.Done = 0; }
            for (int i = 0; i < m.Items.Count; i++)
            {
                if (jp != null && jp.Cancelled) break;
                var it = m.Items[i];
                var pi = new PlanItem();
                pi.Item = it;
                pi.Target = Path.Combine(targetRoot, it.Path.Replace('/', '\\'));
                pi.Source = ResolveSource(sourceRoot, it.Path);
                pi.Action = "download";
                pi.Reason = "no-source";

                if (File.Exists(pi.Source))
                {
                    var fi = new FileInfo(pi.Source);
                    pi.SrcBytes = fi.Length;
                    long kb = fi.Length / 1024;
                    if (kb != it.SizeKb)
                        pi.Reason = string.Format("size-mismatch(local={0} wantKB={1})", fi.Length, it.SizeKb);
                    else if (skipHash)
                    { pi.Action = "link"; pi.Reason = "size-match"; }
                    else
                    {
                        string h = GetMd5(pi.Source);
                        hashed += fi.Length;
                        if (string.Equals(h, it.Md5, StringComparison.OrdinalIgnoreCase))
                        { pi.Action = "link"; pi.Reason = "md5-match"; }
                        else
                            pi.Reason = string.Format("md5-mismatch(local={0} want={1})", h, it.Md5);
                    }
                }
                plan.Add(pi);
                if (jp != null) jp.Done = i + 1;
                if (log != null && (i + 1) % 250 == 0)
                    log(string.Format("  扫描 {0}/{1}   可复用={2}  待下载={3}",
                        i + 1, m.Items.Count, CountAction(plan, "link"), CountAction(plan, "download")));
            }
            if (log != null)
                log(string.Format("  扫描完成，耗时 {0:N1} 分钟，实际读取校验 {1:N2} GB",
                    sw.Elapsed.TotalMinutes, hashed / 1073741824.0));
            return plan;
        }

        public static int CountAction(List<PlanItem> plan, string action)
        {
            int n = 0;
            foreach (var p in plan) if (p.Action == action) n++;
            return n;
        }

        public static long SumBytes(List<PlanItem> plan, string action)
        {
            long n = 0;
            foreach (var p in plan) if (p.Action == action) n += p.Item.SizeKb * 1024;
            return n;
        }

        // ---------------------------------------------------------- 执行
        public class ExecResult
        {
            public int Linked, Copied, Downloaded, Failed;
            public List<string> Failures = new List<string>();
            public List<string> Created = new List<string>();
        }

        public static ExecResult Execute(Manifest m, List<PlanItem> plan, string targetRoot,
                                         bool copyMode, JobProgress jp, Action<string> log)
        {
            if (log == null) log = delegate(string s) { };
            var res = new ExecResult();
            int total = plan.Count;
            if (jp != null) { jp.Phase = "建立链接"; jp.Total = total; jp.Done = 0; }

            for (int i = 0; i < plan.Count; i++)
            {
                if (jp != null && jp.Cancelled) break;
                var p = plan[i];
                if (jp != null) jp.Done = i + 1;
                if (p.Action != "link") continue;
                try
                {
                    if (!File.Exists(p.Source)) { res.Failed++; res.Failures.Add("源文件已消失: " + p.Path); continue; }
                    if (!copyMode)
                    {
                        long kb = new FileInfo(p.Source).Length / 1024;
                        if (kb != p.Item.SizeKb) { res.Failed++; res.Failures.Add("源文件已变化: " + p.Path); continue; }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(p.Target));
                    if (File.Exists(p.Target)) File.Delete(p.Target);
                    if (copyMode) { File.Copy(p.Source, p.Target, true); res.Copied++; }
                    else { Native.CreateHardLink(p.Target, p.Source); res.Linked++; }
                    res.Created.Add(p.Target);
                }
                catch (Exception ex) { res.Failed++; res.Failures.Add("链接 " + p.Path + " :: " + ex.Message); }
                if (log != null && (i + 1) % 2000 == 0)
                    log(string.Format("  {0} 进度 {1}/{2}", copyMode ? "复制" : "链接", i + 1, total));
            }
            log(string.Format("  {0}完成: 新建 {1} 个, 失败 {2} 个",
                copyMode ? "复制" : "硬链接", res.Linked + res.Copied, res.Failed));

            // 下载差集
            var todo = new List<PlanItem>();
            foreach (var p in plan) if (p.Action == "download") todo.Add(p);
            int dn = 0;
            if (jp != null) { jp.Phase = "下载差集"; jp.Total = todo.Count; jp.Done = 0; }
            foreach (var p in todo)
            {
                if (jp != null && jp.Cancelled) break;
                dn++;
                if (jp != null) jp.Done = dn;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p.Target));
                    string url = m.Cdn + "/" + m.Version + "/" + p.Path;
                    string tmp = p.Target + ".dltmp";
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Mozilla/5.0");
                        wc.DownloadFile(url, tmp);
                    }
                    if (p.Item.SizeKb > 0)
                    {
                        string h = GetMd5(tmp);
                        if (!string.Equals(h, p.Item.Md5, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(tmp);
                            res.Failed++;
                            res.Failures.Add("下载校验失败 " + p.Path + " (got " + h + ")");
                            continue;
                        }
                    }
                    if (File.Exists(p.Target)) File.Delete(p.Target);
                    File.Move(tmp, p.Target);
                    res.Downloaded++;
                    res.Created.Add(p.Target);
                    log(string.Format("  下载 {0}/{1}  {2}", dn, todo.Count, p.Path));
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    res.Failures.Add("下载 " + p.Path + " :: " + ex.Message);
                }
            }
            return res;
        }

        // ---------------------------------------------------------- 整树播种
        public static ExecResult Seed(string sourceRoot, string targetRoot, bool copyMode,
                                      JobProgress jp, Action<string> log)
        {
            if (log == null) log = delegate(string s) { };
            var res = new ExecResult();
            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            if (jp != null) { jp.Phase = "播种中"; jp.Total = files.Length; jp.Done = 0; }
            for (int i = 0; i < files.Length; i++)
            {
                if (jp != null && jp.Cancelled) break;
                if (jp != null) jp.Done = i + 1;
                string rel = files[i].Substring(sourceRoot.TrimEnd('\\').Length).TrimStart('\\');
                string dst = Path.Combine(targetRoot, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (File.Exists(dst))
                    {
                        if (new FileInfo(dst).Length == new FileInfo(files[i]).Length) continue;
                        File.Delete(dst);
                    }
                    if (copyMode) { File.Copy(files[i], dst, true); res.Copied++; }
                    else { Native.CreateHardLink(dst, files[i]); res.Linked++; }
                    res.Created.Add(dst);
                }
                catch (Exception ex) { res.Failed++; res.Failures.Add(rel + " :: " + ex.Message); }
                if (log != null && (i + 1) % 2000 == 0) log(string.Format("  播种 {0}/{1}", i + 1, files.Length));
            }
            return res;
        }

        // ---------------------------------------------------------- 验收
        public class VerifyResult
        {
            public int Total, Missing, SizeBad, Linked, Real, BadMd5, Sampled;
            public long LinkedBytes, RealBytes, TotalBytes;
            public List<string> Problems = new List<string>();
        }

        public static VerifyResult Verify(Manifest m, string targetRoot, int sampleLinks,
                                          JobProgress jp, Action<string> log)
        {
            if (log == null) log = delegate(string s) { };
            var r = new VerifyResult();
            r.Total = m.Items.Count;
            var linkedItems = new List<ManifestItem>();
            var realItems = new List<ManifestItem>();
            if (jp != null) { jp.Phase = "验收：清点文件"; jp.Total = m.Items.Count; jp.Done = 0; }
            int vi = 0;
            foreach (var it in m.Items)
            {
                if (jp != null && jp.Cancelled) break;
                if (jp != null) jp.Done = ++vi;
                string t = Path.Combine(targetRoot, it.Path.Replace('/', '\\'));
                if (!File.Exists(t)) { r.Missing++; r.Problems.Add("缺失 " + it.Path); continue; }
                var fi = new FileInfo(t);
                r.TotalBytes += fi.Length;
                if (fi.Length / 1024 != it.SizeKb)
                {
                    r.SizeBad++;
                    r.Problems.Add(string.Format("大小不符 {0} local={1} wantKB={2}", it.Path, fi.Length, it.SizeKb));
                }
                bool isLink = false;
                try
                {
                    var h = new HardLinkInfo(t);
                    isLink = h.LinkCount > 1;
                }
                catch { }
                if (isLink) { r.Linked++; r.LinkedBytes += fi.Length; linkedItems.Add(it); }
                else { r.Real++; r.RealBytes += fi.Length; realItems.Add(it); }
            }
            log(string.Format("  到位 {0}/{1}   硬链接 {2} 个 / {3:N2} GB   真实文件 {4} 个 / {5:N1} MB",
                r.Total - r.Missing, r.Total, r.Linked, r.LinkedBytes / 1073741824.0, r.Real, r.RealBytes / 1048576.0));

            log("  校验全部真实文件 MD5 ...");
            if (jp != null) { jp.Phase = "验收：校验下载文件"; jp.Total = Math.Max(1, realItems.Count); jp.Done = 0; }
            int mi = 0;
            foreach (var it in realItems)
            {
                if (jp != null && jp.Cancelled) break;
                if (jp != null) jp.Done = ++mi;
                string t = Path.Combine(targetRoot, it.Path.Replace('/', '\\'));
                if (!string.Equals(GetMd5(t), it.Md5, StringComparison.OrdinalIgnoreCase))
                { r.BadMd5++; r.Problems.Add("MD5 不符 " + it.Path); }
            }
            if (sampleLinks > 0 && linkedItems.Count > 0)
            {
                int take = Math.Min(sampleLinks, linkedItems.Count);
                var rnd = new Random(12345);
                log(string.Format("  抽样校验硬链接 {0}/{1} ...", take, linkedItems.Count));
                if (jp != null) { jp.Phase = "验收：抽样校验"; jp.Total = take; jp.Done = 0; }
                for (int i = 0; i < take; i++)
                {
                    if (jp != null && jp.Cancelled) break;
                    if (jp != null) jp.Done = i + 1;
                    var it = linkedItems[rnd.Next(linkedItems.Count)];
                    string t = Path.Combine(targetRoot, it.Path.Replace('/', '\\'));
                    if (!string.Equals(GetMd5(t), it.Md5, StringComparison.OrdinalIgnoreCase))
                    { r.BadMd5++; r.Problems.Add("MD5 不符(链接) " + it.Path); }
                    r.Sampled++;
                }
            }
            return r;
        }

        public static Process FindOwnerProcess(string file)
        {
            return null;
        }
    }

    /// <summary>取硬链接数（nNumberOfLinks）</summary>
    internal class HardLinkInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        public uint LinkCount;
        public uint FileIndexHigh, FileIndexLow, VolumeSerial;

        public HardLinkInfo(string path)
        {
            IntPtr h = CreateFileW(path, 0, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) throw new IOException("无法打开文件");
            try
            {
                BY_HANDLE_FILE_INFORMATION info;
                if (!GetFileInformationByHandle(h, out info)) throw new IOException("GetFileInformationByHandle 失败");
                LinkCount = info.NumberOfLinks;
                FileIndexHigh = info.FileIndexHigh;
                FileIndexLow = info.FileIndexLow;
                VolumeSerial = info.VolumeSerialNumber;
            }
            finally { CloseHandle(h); }
        }
    }

    // ------------------------------------------------------------------ CSV
    internal static class Csv
    {
        public static string Esc(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        public static void Write(string path, string header, IEnumerable<string> rows)
        {
            using (var sw = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                sw.WriteLine(header);
                foreach (var r in rows) sw.WriteLine(r);
            }
        }
    }

    // ------------------------------------------------------------------ 免责声明
    internal static class Disclaimer
    {
        /// <summary>声明版本：改动声明内容时一并修改，已同意过的用户会重新被要求阅读</summary>
        public const string Version = "1.1";
        public const string Title = "免责声明与使用须知";

        public const string Body =
@"本工具为第三方个人工具，与网易、24 Entertainment、《永劫无间》官方、Steam、
Epic Games 及其关联方均无任何关系，也未获得其授权、认可或支持。

■ 本工具做什么
  · 读取目标渠道的官方文件清单，把你本机已经下载过的相同文件，用硬链接
    （同一磁盘零额外空间）或复制的方式复用过去；
  · 只从官方 CDN 下载清单中缺失或校验不一致的文件；
  · 全程不修改、不注入、不破解任何游戏内容，也不改动反作弊组件本身。

■ 使用前提
  你应当在拥有该游戏合法授权的前提下使用本工具，并自行确认你的使用方式
  符合游戏与平台的服务条款。

■ 风险与责任免除
  1. 硬链接会让两份客户端共用同一批物理数据。若某平台更新时是「原地改写」
     而不是「删除后重建」，可能同时影响另一份客户端。请在任意一边更新游戏
     之后，重新运行一次「③ 验收」。
  2. 本工具按「现状」提供，不附带任何明示或暗示的担保。
  3. 因使用本工具而造成的游戏文件损坏、更新或启动失败、数据丢失、账号异常、
     封禁，以及其他任何直接或间接损失，均由使用者自行承担，作者不承担
     任何责任。
  4. 建议操作前自行备份重要数据。本工具会在 reports 目录生成回滚脚本，
     供你撤销本次创建的文件。

■ 如果你不同意以上内容
  请立即关闭本程序并删除它，不要使用。
";
    }

    // ------------------------------------------------------------------ UI
    public class MainForm : Form
    {
        private TextBox txtManifest, txtSource, txtTarget;
        private RadioButton rbModeA, rbModeB, rbHard, rbCopy;
        private CheckBox chkSkipHash;
        private Button btnBrowseManifest, btnBrowseSource, btnBrowseTarget, btnFetchManifest;
        private Button btnScan, btnExecute, btnVerify, btnCancel, btnOpenReports, btnDisclaimer;
        private TextBox txtLog;
        private ProgressBar bar;
        private Label lblStatus;

        private Manifest manifest;
        private List<PlanItem> lastPlan;
        private string lastPlanTarget;
        private string lastPlanSource;
        private bool seedReady;
        private volatile bool busy;
        private volatile JobProgress job;
        private int barTotal = -1;       // 当前进度条的最大值，用来避免重复设置 Maximum
        private string acceptedEula = ""; // 已同意的免责声明版本；空=未同意
        private string reportRoot;

        public MainForm()
        {
            Text = "永劫无间 · 免下载切客户端 / 客户端重建工具  v1.1";
            Size = new Size(1010, 750);
            MinimumSize = new Size(980, 670);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = C.Bg;
            ForeColor = C.Ink;
            reportRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "reports");
            Controls.Add(MakeHeader());

            int y = 64 + 18;
            txtManifest = AddPathRow("① 官方文件清单 (data.xml)", ref y, out btnBrowseManifest);

            txtSource = AddPathRow("② 已有客户端目录（另一个渠道装的客户端）", ref y, out btnBrowseSource);
            txtTarget = AddPathRow("③ 目标安装目录（要生成/修复哪个）", ref y, out btnBrowseTarget);

            var gbMode = new GroupBox();
            gbMode.Text = "工作模式";
            gbMode.SetBounds(14, y, 644, 64);
            gbMode.ForeColor = C.Muted;
            gbMode.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            rbModeA = FlatRadio(gbMode, "A 按官方清单重建国服（推荐，逐文件 MD5 校验）", 12, 26, 340, true);
            rbModeB = FlatRadio(gbMode, "B 整树播种（交平台校验补差）", 360, 26, 280, false);
            Controls.Add(gbMode);

            var gbWay = new GroupBox();
            gbWay.Text = "复用方式";
            gbWay.SetBounds(664, y, 332, 64);
            gbWay.ForeColor = C.Muted;
            gbWay.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            rbHard = FlatRadio(gbWay, "硬链接（同一磁盘，零额外空间）", 12, 26, 220, true);
            rbCopy = FlatRadio(gbWay, "复制", 238, 26, 90, false);
            Controls.Add(gbWay);
            y += 68;

            chkSkipHash = new CheckBox();
            chkSkipHash.Text = "跳过 MD5 校验（快，但只按文件大小匹配 —— 不推荐，可能链错内容不同的同名文件）";
            chkSkipHash.SetBounds(18, y + 3, 640, 22);
            chkSkipHash.ForeColor = C.Muted;
            Controls.Add(chkSkipHash);
            y += 32;

            btnScan = new Button(); btnScan.Text = "① 扫描（干跑，不改文件）";
            btnScan.SetBounds(14, y, 180, 34);
            btnScan.Click += (s, e) => StartJob(rbModeB.Checked ? JobKind.Seed : JobKind.Scan);
            btnExecute = new Button(); btnExecute.Text = "② 执行";
            btnExecute.SetBounds(200, y, 100, 34);
            btnExecute.Enabled = false;
            btnExecute.Click += (s, e) => { if (ConfirmExecute()) StartJob(rbModeB.Checked ? JobKind.SeedExecute : JobKind.Execute); };
            btnVerify = new Button(); btnVerify.Text = "③ 验收";
            btnVerify.SetBounds(306, y, 90, 34);
            btnVerify.Click += (s, e) => StartJob(JobKind.Verify);
            btnCancel = new Button(); btnCancel.Text = "中止";
            btnCancel.SetBounds(402, y, 80, 34);
            btnCancel.Enabled = false;
            btnCancel.Click += (s, e) => { if (job != null) job.Cancelled = true; Log("已请求中止…"); };
            btnFetchManifest = new Button(); btnFetchManifest.Text = "下载最新清单";
            btnFetchManifest.SetBounds(488, y, 120, 34);
            btnFetchManifest.Click += (s, e) => FetchManifest();
            btnOpenReports = new Button(); btnOpenReports.Text = "打开报告目录";
            btnOpenReports.SetBounds(614, y, 120, 34);
            btnOpenReports.Click += (s, e) => OpenReports();
            btnDisclaimer = new Button(); btnDisclaimer.Text = "免责声明";
            btnDisclaimer.SetBounds(742, y, 100, 34);
            btnDisclaimer.Click += (s, e) => ShowDisclaimer(false);
            StylePrimary(btnScan, C.Green, C.GreenDark);
            StylePrimary(btnExecute, C.Blue, C.BlueDark);
            StylePrimary(btnVerify, C.Teal, C.TealDark);
            StyleDanger(btnCancel);
            StyleNeutral(btnFetchManifest);
            StyleNeutral(btnOpenReports);
            StyleNeutral(btnDisclaimer);
            Controls.Add(btnScan); Controls.Add(btnExecute); Controls.Add(btnVerify);
            Controls.Add(btnCancel); Controls.Add(btnFetchManifest); Controls.Add(btnOpenReports);
            Controls.Add(btnDisclaimer);
            y += 42;

            bar = new ProgressBar();
            bar.Style = ProgressBarStyle.Continuous;
            bar.SetBounds(16, y + 2, 974, 16);
            Controls.Add(bar);
            y += 26;

            lblStatus = new Label();
            lblStatus.Text = "就绪。建议先用「① 扫描」看清要下载多少，再执行。";
            lblStatus.SetBounds(18, y, 960, 20);
            lblStatus.ForeColor = C.Muted;
            Controls.Add(lblStatus);
            y += 28;

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.SetBounds(16, y, 978, Height - y - 44);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            txtLog.BackColor = Color.White;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.Font = new Font("Consolas", 9.5f);
            Controls.Add(txtLog);

            var tip = new System.Windows.Forms.ToolTip();
            tip.AutoPopDelay = 6000; tip.InitialDelay = 350; tip.ReshowDelay = 300;
            tip.SetToolTip(btnScan, "只读扫描，不修改任何文件。它会拿目标渠道的官方清单，逐文件比对已有客户端，算出能复用多少、还要下载多少。");
            tip.SetToolTip(btnExecute, "执行扫描得出的方案：建立硬链接/复制，并从官方 CDN 下载差异。执行前会再次弹窗确认。");
            tip.SetToolTip(btnVerify, "验收：核对目标目录里每个文件是否存在、大小与 MD5 是否与官方清单一致，并抽查硬链接。");
            tip.SetToolTip(btnCancel, "请求停止当前任务。已经创建的文件不会被自动回滚，需要时可运行报告目录里的 rollback 脚本。");
            tip.SetToolTip(btnFetchManifest, "从网易官方地址(update.yjwujian.cn)下载最新的 data.xml 清单到目标目录。");
            tip.SetToolTip(btnOpenReports, "打开 exe 同目录的 reports 文件夹，里面是每次运行的报告和回滚脚本。");
            tip.SetToolTip(chkSkipHash, "强烈不建议开启：只按文件大小匹配不校验内容，可能把『大小相同但内容不同』的文件链错（网上已有实测案例）。");
            tip.SetToolTip(rbModeA, "读取网易官方清单，逐文件 MD5 校验后复用，最严格。适合把别的渠道的客户端『变』成国服。");
            tip.SetToolTip(rbModeB, "整棵文件树链接/复制过去，然后让 Epic / Steam 自带的『验证游戏文件完整性』补差。");
            tip.SetToolTip(rbHard, "两个客户端共用同一份物理数据，不占额外空间、瞬间完成；前提是两个目录在同一个磁盘分区。");
            tip.SetToolTip(rbCopy, "完整复制一份，多占约 90 GB，但两份数据完全独立，更新互不影响。");
            tip.SetToolTip(btnDisclaimer, "随时查看免责声明与使用须知。");

            LoadSettings();
            AutoDetect(false);
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 400;
            timer.Tick += (s, e) => RefreshProgress();
            timer.Start();
            FormClosing += (s, e) =>
            {
                SaveSettings();
                if (busy)
                {
                    if (job != null) job.Cancelled = true;
                    var r = MessageBox.Show("任务还在运行，现在退出会中断它（已创建的链接/已下载的文件不会回滚）。\n\n确定退出吗？",
                        "任务进行中", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (r != DialogResult.OK) { e.Cancel = true; if (job != null) job.Cancelled = false; return; }
                }
            };
        }

        private TextBox AddPathRow(string caption, ref int y, out Button browse)
        {
            var lbl = new Label();
            lbl.Text = caption;
            lbl.SetBounds(16, y, 620, 20);
            lbl.ForeColor = C.Ink;
            lbl.Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold);
            Controls.Add(lbl);

            var tb = new TextBox();
            tb.SetBounds(16, y + 22, 810, 26);
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.BackColor = Color.White;
            tb.Font = new Font(Font.FontFamily, 9.5f);
            Controls.Add(tb);

            browse = new Button();
            browse.Text = "浏览…";
            browse.SetBounds(832, y + 21, 150, 28);
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = caption;
                    if (Directory.Exists(tb.Text)) d.SelectedPath = tb.Text;
                    if (d.ShowDialog() == DialogResult.OK) tb.Text = d.SelectedPath;
                }
            };
            StyleNeutral(browse);
            Controls.Add(browse);
            y += 56;
            return tb;
        }

        // ------------------------------------------------------------ 界面常量与样式
        private static class C
        {
            public static readonly Color Bg        = Color.FromArgb(244, 246, 250);   // 窗体背景
            public static readonly Color Ink       = Color.FromArgb(47, 62, 80);      // 正文深色
            public static readonly Color Muted     = Color.FromArgb(127, 140, 149);   // 次级文字
            public static readonly Color Green     = Color.FromArgb(39, 174, 96);
            public static readonly Color GreenDark = Color.FromArgb(33, 150, 82);
            public static readonly Color Blue      = Color.FromArgb(31, 111, 235);
            public static readonly Color BlueDark  = Color.FromArgb(24, 89, 190);
            public static readonly Color Teal      = Color.FromArgb(0, 150, 136);
            public static readonly Color TealDark  = Color.FromArgb(0, 121, 107);
            public static readonly Color Red       = Color.FromArgb(231, 76, 60);
            public static readonly Color RedDark   = Color.FromArgb(198, 60, 47);
            public static readonly Color Neutral   = Color.FromArgb(232, 237, 243);
            public static readonly Color NeutralHover = Color.FromArgb(206, 215, 226);
        }

        private static void FlatButton(Button b, Color back, Color hover, Color front, bool bold)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            b.BackColor = back;
            b.ForeColor = front;
            b.Font = new Font("Microsoft YaHei UI", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular);
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
        }

        private static void StylePrimary(Button b, Color c, Color cd) { FlatButton(b, c, cd, Color.White, true); }
        private static void StyleDanger(Button b) { FlatButton(b, C.Red, C.RedDark, Color.White, true); }
        private static void StyleNeutral(Button b) { FlatButton(b, C.Neutral, C.NeutralHover, C.Ink, false); }

        private static RadioButton FlatRadio(Control parent, string text, int x, int y, int w, bool checkedState)
        {
            var r = new RadioButton();
            r.Text = text;
            r.SetBounds(x, y, w, 22);
            r.Checked = checkedState;
            r.ForeColor = C.Ink;
            r.Font = new Font("Microsoft YaHei UI", 9f);
            parent.Controls.Add(r);
            return r;
        }

        /// <summary>顶部渐变横幅：标题 + 副标题 + 图标</summary>
        private Panel MakeHeader()
        {
            var p = new Panel();
            p.Dock = DockStyle.Top;
            p.Height = 64;
            p.Paint += (s, e) =>
            {
                var r = p.ClientRectangle;
                using (var gb = new System.Drawing.Drawing2D.LinearGradientBrush(r,
                    Color.FromArgb(30, 136, 229), Color.FromArgb(21, 101, 192), 90f))
                    e.Graphics.FillRectangle(gb, r);
                Bitmap iconBmp = null;
                try { if (Icon != null) iconBmp = Icon.ToBitmap(); } catch { }
                if (iconBmp != null) { e.Graphics.DrawImage(iconBmp, 18, 14, 36, 36); iconBmp.Dispose(); }
                TextRenderer.DrawText(e.Graphics, "永劫无间 · 免下载切客户端",
                    new Font(Font.FontFamily, 14f, FontStyle.Bold), new Point(66, 10), Color.White);
                TextRenderer.DrawText(e.Graphics, "从已有客户端重建目标渠道客户端，只需下载真正的差异 · v1.1",
                    new Font(Font.FontFamily, 9f), new Point(66, 39), Color.FromArgb(234, 242, 250));
            };
            return p;
        }

        // ------------------------------------------------------------ 自动探测
        private void AutoDetect(bool silent)
        {
            try
            {
                if (txtManifest.Text.Length == 0 || !File.Exists(txtManifest.Text))
                {
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        string p = Path.Combine(d.RootDirectory.FullName, @"Naraka\patch\md5list\data.xml");
                        if (File.Exists(p)) { txtManifest.Text = p; break; }
                    }
                }
                if (txtTarget.Text.Length == 0)
                {
                    string mc = txtManifest.Text;
                    if (mc.Length > 0)
                    {
                        // ...\Naraka\patch\md5list\data.xml  ->  ...\Naraka
                        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(mc)));
                        if (!string.IsNullOrEmpty(root)) txtTarget.Text = root;
                    }
                    else txtTarget.Text = @"D:\Naraka";
                }
                if (txtSource.Text.Length == 0)
                {
                    string found = FindClientRoot(txtTarget.Text);
                    if (found != null) txtSource.Text = found;
                    else if (!silent) Log("提示：没有自动找到「已有客户端目录」，请手动选另一个渠道安装的永劫无间（含 NarakaBladepoint_Data 的目录）。");
                }
                if (!silent) Log("自动探测：清单=" + txtManifest.Text + "  目标=" + txtTarget.Text + "  源=" + txtSource.Text);
            }
            catch (Exception ex) { if (!silent) Log("自动探测失败: " + ex.Message); }
        }

        private string FindClientRoot(string excludeRoot)
        {
            var found = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                string root = d.RootDirectory.FullName;
                string[] guesses = new string[] {
                    @"yongjiewujian\NARAKABLADEPOINT",
                    @"SteamLibrary\steamapps\common\NARAKABLADEPOINT",
                    @"steam\steamapps\common\NARAKABLADEPOINT",
                    @"Steam\steamapps\common\NARAKABLADEPOINT",
                    @"Program Files\Epic Games\NARAKABLADEPOINT",
                    @"epic\Epic Games\NARAKABLADEPOINT",
                    @"Epic Games\NARAKABLADEPOINT",
                    @"Games\NARAKABLADEPOINT",
                    @"Naraka\program",
                    @"Naraka",
                    @"NARAKABLADEPOINT"
                };
                foreach (var g in guesses)
                {
                    string p = Path.Combine(root, g);
                    if (Directory.Exists(Path.Combine(p, "NarakaBladepoint_Data")))
                        if (!found.Contains(p)) found.Add(p);
                }
            }
            // 排除目标自己（或目标下面的目录）—— 那份不算"另一个渠道的客户端"
            foreach (var p in found)
                if (!IsUnder(p, excludeRoot)) return p;
            return null;
        }

        private static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string a = path.TrimEnd('\\'), b = root.TrimEnd('\\');
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ 设置
        private string SettingsPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); } }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "manifest") txtManifest.Text = v;
                    else if (k == "source") txtSource.Text = v;
                    else if (k == "target") txtTarget.Text = v;
                    else if (k == "mode") rbModeB.Checked = (v == "B");
                    else if (k == "way") rbCopy.Checked = (v == "copy");
                    else if (k == "eula") acceptedEula = v;
                }
                Log("已载入上次的设置。");
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new string[] {
                    "manifest=" + txtManifest.Text,
                    "source=" + txtSource.Text,
                    "target=" + txtTarget.Text,
                    "mode=" + (rbModeB.Checked ? "B" : "A"),
                    "way=" + (rbCopy.Checked ? "copy" : "hardlink"),
                    "eula=" + acceptedEula
                }, new UTF8Encoding(true));
            }
            catch { }
        }

        // ------------------------------------------------------------ 任务
        private void StartJob(JobKind kind)
        {
            if (busy) { Log("当前有任务在跑。"); return; }
            bool modeB = rbModeB.Checked;

            if (modeB && kind != JobKind.Verify)
            {
                if (!Directory.Exists(txtSource.Text)) { MessageBox.Show("找不到「已有客户端目录」。", "路径不对"); return; }
                if (kind == JobKind.SeedExecute && !Directory.Exists(txtTarget.Text))
                {
                    if (MessageBox.Show("目标目录不存在，要现在创建吗？\n\n" + txtTarget.Text, "创建目标目录",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    try { Directory.CreateDirectory(txtTarget.Text); Log("已创建目标目录: " + txtTarget.Text); }
                    catch (Exception ex) { MessageBox.Show("创建目标目录失败: " + ex.Message); return; }
                }
                if (!rbCopy.Checked && !Engine.SameVolume(txtSource.Text, txtTarget.Text))
                {
                    MessageBox.Show("硬链接不能跨磁盘分区。\n\n源和目标必须在同一个盘（例如都在 F:）。\n请把「复用方式」改成「复制」，或把目标目录换到源所在的分区。",
                        "跨盘无法硬链接");
                    return;
                }
            }
            if (!modeB && kind != JobKind.DownloadManifest)
            {
                if (!File.Exists(txtManifest.Text)) { MessageBox.Show("找不到清单文件 data.xml，可以点「下载最新清单」。", "缺清单"); return; }
                if (!Directory.Exists(txtSource.Text)) { MessageBox.Show("找不到「已有客户端目录」。", "路径不对"); return; }
                if (!Directory.Exists(txtTarget.Text)) { MessageBox.Show("找不到「目标安装目录」。", "路径不对"); return; }
                if (IsUnder(txtSource.Text, txtTarget.Text))
                {
                    MessageBox.Show("「已有客户端目录」就在「目标安装目录」里面（或与它相同）。\n\n请把源指向另一个渠道安装的客户端，例如 Epic/Steam 版所在的目录。",
                        "源和目标重复");
                    return;
                }
                if (!rbCopy.Checked && !Engine.SameVolume(txtSource.Text, txtTarget.Text))
                {
                    MessageBox.Show("硬链接不能跨磁盘分区。\n\n源和目标必须在同一个盘（例如都在 F:）。\n请把「复用方式」改成「复制」，或把目标目录换到源所在的分区。",
                        "跨盘无法硬链接");
                    return;
                }
            }

            busy = true;
            SetBusy(true);
            ResetProgress();
            var jp = new JobProgress();
            jp.Kind = kind;
            jp.Phase = kind == JobKind.Scan ? "扫描中"
                     : kind == JobKind.Execute ? "建立链接"
                     : kind == JobKind.Verify ? "验收中"
                     : kind == JobKind.Seed ? "预检中" : "播种中";
            job = jp;
            var dir = Path.Combine(reportRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try { Directory.CreateDirectory(dir); }
            catch
            {
                dir = Path.Combine(Path.GetTempPath(), "NarakaClientSwitcher", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                try { Directory.CreateDirectory(dir); }
                catch { dir = Path.GetTempPath(); }
                SafeUi(delegate { Log("提示：程序所在目录不可写，报告改存到 " + dir); });
            }
            string src = txtSource.Text, tgt = txtTarget.Text, man = txtManifest.Text;
            bool skip = chkSkipHash.Checked, copy = rbCopy.Checked;

            var th = new Thread(() =>
            {
                try
                {
                    if (kind == JobKind.Scan) DoScan(man, src, tgt, skip, jp, dir);
                    else if (kind == JobKind.Execute) DoExecute(man, tgt, copy, jp, dir);
                    else if (kind == JobKind.Verify) DoVerify(man, tgt, jp, dir);
                    else if (kind == JobKind.Seed) DoSeedPreview(src, tgt, jp, dir);
                    else if (kind == JobKind.SeedExecute) DoSeed(src, tgt, copy, jp, dir);
                }
                catch (Exception ex) { jp.Message = "出错: " + ex.Message; Log("出错: " + ex.Message); }
                finally
                {
                    busy = false;
                    SafeUi(delegate
                    {
                        SetBusy(false);
                        // 中止或结束时给一个明确的最终状态，绝不再空转刷新进度条
                        if (jp.Cancelled) lblStatus.Text = "已中止 —— 结果不完整，请重新执行";
                        else if (!string.IsNullOrEmpty(jp.Message)) lblStatus.Text = jp.Message;
                    });
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void DoScan(string man, string src, string tgt, bool skip, JobProgress jp, string dir)
        {
            Log("");
            Log("========== 扫描（干跑，不会改动任何文件）==========");
            Log("清单: " + man);
            var m = Engine.LoadManifest(man);
            manifest = m;
            Log(string.Format("清单内容: {0} 个文件, 版本 {1}, 声明总量 {2:N2} GB", m.Items.Count, m.Version, m.TotalKb / 1048576.0));
            Log("CDN: " + m.Cdn + "/" + m.Version + "/");
            Log("源: " + src);
            Log("目标: " + tgt);
            // 路径长度预警（Windows 默认单条路径上限 260 字符）
            int maxRel = 0;
            foreach (var it in m.Items) if (it.Path.Length > maxRel) maxRel = it.Path.Length;
            int projected = tgt.TrimEnd('\\').Length + 1 + maxRel;
            if (projected > 250)
                Log(string.Format("预警：目标目录太深，最长的文件路径约 {0} 字符，可能超过 Windows 的 260 字符上限。建议把目标换到更靠根目录的位置（例如 D:\\Naraka）。", projected));
            var plan = Engine.Scan(m, src, tgt, skip, jp, Log);
            if (jp.Cancelled)
            {
                Log("");
                Log("扫描被中止：结果不完整，不能据此执行。请重新扫描。");
                lastPlan = null;
                return;
            }
            lastPlan = plan;
            lastPlanTarget = tgt;
            lastPlanSource = src;
            long linkB = Engine.SumBytes(plan, "link"), dlB = Engine.SumBytes(plan, "download");
            Log("");
            Log("-------- 扫描结果 --------");
            Log(string.Format("可复用（零下载）: {0,6} 个文件   {1,10:N2} GB", Engine.CountAction(plan, "link"), linkB / 1073741824.0));
            Log(string.Format("需要下载        : {0,6} 个文件   {1,10:N1} MB", Engine.CountAction(plan, "download"), dlB / 1048576.0));
            Log("--------------------------");
            var rows = new List<string>();
            foreach (var p in plan)
                rows.Add(string.Join(",", new string[] {
                    Csv.Esc(p.Path), p.Action, Csv.Esc(p.Reason), p.Item.SizeKb.ToString(),
                    p.SrcBytes.ToString(), Csv.Esc(p.Target), Csv.Esc(p.Source) }));
            Csv.Write(Path.Combine(dir, "plan.csv"), "path,action,reason,wantKB,srcBytes,target,source", rows);
            var drows = new List<string>();
            foreach (var p in plan) if (p.Action == "download")
                drows.Add(string.Join(",", new string[] { Csv.Esc(p.Path), p.Item.SizeKb.ToString(), Csv.Esc(p.Reason), m.Cdn + "/" + m.Version + "/" + p.Path }));
            Csv.Write(Path.Combine(dir, "download.csv"), "path,wantKB,reason,url", drows);
            Log("报告: " + dir);
            SafeUi(delegate { btnExecute.Enabled = true; });
            FinishProgress(string.Format("扫描完成 —— 可复用 {0} 个 / {1:N2} GB，需下载 {2} 个 / {3:N1} MB",
                Engine.CountAction(plan, "link"), linkB / 1073741824.0,
                Engine.CountAction(plan, "download"), dlB / 1048576.0));
        }

        private void DoExecute(string man, string tgt, bool copy, JobProgress jp, string dir)
        {
            var m = manifest != null ? manifest : Engine.LoadManifest(man);
            if (lastPlan == null) { Log("请先扫描。"); return; }
            Log("");
            Log("========== 执行 ==========");
            var sw = Stopwatch.StartNew();
            var res = Engine.Execute(m, lastPlan, tgt, copy, jp, Log);
            sw.Stop();
            Log(string.Format("完成: 新建 {0} / 复制 {1} / 下载 {2} / 失败 {3}，耗时 {4:N1} 分钟",
                res.Linked, res.Copied, res.Downloaded, res.Failed, sw.Elapsed.TotalMinutes));

            // 回滚脚本：同时给两种编码，避免代码页差异导致删不掉中文路径
            WriteRollback(dir, res.Created);
            if (res.Failures.Count > 0)
                File.WriteAllLines(Path.Combine(dir, "failures.txt"), res.Failures, new UTF8Encoding(true));
            Log("报告与回滚脚本: " + dir);
            if (res.Failures.Count > 0)
            {
                Log("失败 " + res.Failures.Count + " 项，前 10 项：");
                for (int i = 0; i < Math.Min(10, res.Failures.Count); i++) Log("  " + res.Failures[i]);
            }
            FinishProgress(res.Failed == 0
                ? string.Format("执行完成 —— 链接 {0} / 复制 {1} / 下载 {2}，全部成功", res.Linked, res.Copied, res.Downloaded)
                : string.Format("执行完成（有失败）—— 链接 {0} / 复制 {1} / 下载 {2} / 失败 {3}，详见报告",
                    res.Linked, res.Copied, res.Downloaded, res.Failed));
        }

        private void DoSeed(string src, string tgt, bool copy, JobProgress jp, string dir)
        {
            Log("");
            Log("========== 整树播种（B 模式）==========");
            Log("把 " + src + " 的整棵文件树" + (copy ? "复制" : "硬链接") + "到 " + tgt);
            Log("完成后请到 Epic / Steam 里执行「验证游戏文件完整性」，平台只会补下真正的差异。");
            var res = Engine.Seed(src, tgt, copy, jp, Log);
            Log(string.Format("播种完成: 新建 {0} / 复制 {1} / 失败 {2}", res.Linked, res.Copied, res.Failed));
            WriteRollback(dir, res.Created);
            Log("报告与回滚脚本: " + dir);
            FinishProgress(res.Failed == 0
                ? string.Format("播种完成 —— 新建 {0} / 复制 {1}；接着去 Epic/Steam 点「验证游戏文件完整性」", res.Linked, res.Copied)
                : string.Format("播种完成（有失败 {0} 项），详见报告", res.Failed));
        }

        private void DoSeedPreview(string src, string tgt, JobProgress jp, string dir)
        {
            Log("");
            Log("========== 整树播种 · 预检（B 模式，不改动文件）==========");
            Log("源: " + src);
            Log("目标: " + tgt);
            var files = Directory.GetFiles(src, "*", SearchOption.AllDirectories);
            long bytes = 0, already = 0, todoBytes = 0;
            if (jp != null) { jp.Total = files.Length; jp.Done = 0; }
            for (int i = 0; i < files.Length; i++)
            {
                var fi = new FileInfo(files[i]);
                bytes += fi.Length;
                string rel = files[i].Substring(src.TrimEnd('\\').Length).TrimStart('\\');
                string dst = Path.Combine(tgt, rel);
                if (File.Exists(dst) && new FileInfo(dst).Length == fi.Length) already++;
                else todoBytes += fi.Length;
                if (jp != null) jp.Done = i + 1;
            }
            seedReady = true;
            Log(string.Format("源共 {0} 个文件 / {1:N2} GB", files.Length, bytes / 1073741824.0));
            Log(string.Format("目标已存在同名同大小: {0} 个；需要新建/覆盖: {1:N2} GB", already, todoBytes / 1073741824.0));
            Log("下一步：点「② 执行」播种，然后到 Epic / Steam 里点「验证游戏文件完整性」，平台只会下载真正的差异。");
            // 路径层级提醒：模式 B 是「原样镜像」。若源是客户端目录(含 NarakaBladepoint_Data)、
            // 目标却填成安装根目录(其下才是 program\NarakaBladepoint_Data)，会镜像错层级
            bool srcHasData = Directory.Exists(Path.Combine(src, "NarakaBladepoint_Data"));
            bool tgtHasData = Directory.Exists(Path.Combine(tgt, "NarakaBladepoint_Data"));
            bool tgtHasProgramData = Directory.Exists(Path.Combine(tgt, @"program\NarakaBladepoint_Data"));
            if (srcHasData && !tgtHasData && tgtHasProgramData)
            {
                Log("");
                Log("路径层级提醒：源目录本身就是客户端目录（含 NarakaBladepoint_Data），而目标目录下没有它、");
                Log("  只有 program\\NarakaBladepoint_Data —— 说明目标填的是「安装根目录」。模式 B 会把这棵树");
                Log("  原样镜像过去，结果是 " + tgt + "\\<文件>，而不是 " + tgt + "\\program\\<文件>。");
                Log("  如果你要的是后者，请把「③ 目标安装目录」改成：" + Path.Combine(tgt, "program"));
            }
            // 预检也落一份报告，避免 reports 下留下空目录
            try
            {
                File.WriteAllLines(Path.Combine(dir, "preview.txt"), new string[] {
                    "整树播种 · 预检报告（未修改任何文件）",
                    "时间     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "源目录   : " + src,
                    "目标目录 : " + tgt,
                    string.Format("源文件   : {0} 个 / {1:N2} GB", files.Length, bytes / 1073741824.0),
                    string.Format("已存在同名同大小: {0} 个", already),
                    string.Format("需要新建或覆盖  : {0:N2} GB", todoBytes / 1073741824.0)
                }, new UTF8Encoding(true));
            }
            catch { }
            SafeUi(delegate { btnExecute.Enabled = true; });
            FinishProgress(string.Format("预检完成 —— 源 {0} 个文件 / {1:N2} GB；需要新建或覆盖 {2:N2} GB",
                files.Length, bytes / 1073741824.0, todoBytes / 1073741824.0));
        }

        private void DoVerify(string man, string tgt, JobProgress jp, string dir)
        {
            if (!File.Exists(man)) { Log("请先指定清单文件。"); return; }
            Log("");
            Log("========== 验收 ==========");
            var m = Engine.LoadManifest(man);
            var r = Engine.Verify(m, tgt, 300, jp, Log);
            Log("");
            Log(string.Format("清单 {0} 项 | 缺失 {1} | 大小不符 {2} | MD5 不符 {3}", r.Total, r.Missing, r.SizeBad, r.BadMd5));
            Log(string.Format("硬链接 {0} 个 / {1:N2} GB | 真实文件 {2} 个 / {3:N1} MB | 总 {4:N2} GB",
                r.Linked, r.LinkedBytes / 1073741824.0, r.Real, r.RealBytes / 1048576.0, r.TotalBytes / 1073741824.0));
            Log(r.Missing == 0 && r.SizeBad == 0 && r.BadMd5 == 0
                ? "验收结果: 通过 —— 与官方清单完全一致。"
                : "验收结果: 未通过，详见报告。");
            if (r.Problems.Count > 0)
                File.WriteAllLines(Path.Combine(dir, "verify-problems.txt"), r.Problems, new UTF8Encoding(true));
            Log("报告: " + dir);
            bool pass = r.Missing == 0 && r.SizeBad == 0 && r.BadMd5 == 0;
            FinishProgress(pass
                ? string.Format("验收通过 —— {0} 个文件与官方清单完全一致", r.Total)
                : string.Format("验收未通过 —— 缺失 {0} / 大小不符 {1} / MD5 不符 {2}，详见报告", r.Missing, r.SizeBad, r.BadMd5));
        }

        private void FetchManifest()
        {
            string tgt = txtTarget.Text;
            if (string.IsNullOrEmpty(tgt)) { MessageBox.Show("请先填目标安装目录。"); return; }
            string dest = Path.Combine(tgt, @"patch\md5list\data.xml");
            try
            {
                Log("正在从官方地址下载最新清单: " + Engine.OfficialManifestUrl);
                Application.DoEvents();
                Engine.DownloadFile(Engine.OfficialManifestUrl, dest, reportRoot);
                txtManifest.Text = dest;
                Log("清单已保存: " + dest);
            }
            catch (Exception ex) { MessageBox.Show("下载清单失败: " + ex.Message); }
        }

        private bool ConfirmExecute()
        {
            if (rbModeB.Checked)
            {
                if (!seedReady) { MessageBox.Show("请先点「① 扫描」预检。"); return false; }
                string m2 = string.Format("即将把：\n{0}\n\n整棵文件树{1}到：\n{2}\n\n完成后请在 Epic / Steam 里执行「验证游戏文件完整性」。\n\n确定继续吗？",
                    txtSource.Text, rbCopy.Checked ? "复制" : "硬链接", txtTarget.Text);
                return MessageBox.Show(m2, "确认执行", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
            }
            if (lastPlan == null) { MessageBox.Show("请先点「① 扫描」看清要做什么。"); return false; }
            if (!string.Equals((lastPlanTarget ?? "").TrimEnd('\\'), txtTarget.Text.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals((lastPlanSource ?? "").TrimEnd('\\'), txtSource.Text.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("扫描之后你改过「已有客户端目录」或「目标安装目录」。\n\n为避免写错位置，请重新点一次「① 扫描」。",
                    "路径已改动");
                lastPlan = null;
                SafeUi(delegate { btnExecute.Enabled = false; });
                return false;
            }
            long dl = Engine.SumBytes(lastPlan, "download");
            int links = Engine.CountAction(lastPlan, "link");
            string msg = string.Format(
                "即将在：\n{0}\n\n建立 {1} 个{2}，并从官方 CDN 下载 {3:N1} MB。\n\n确定继续吗？",
                txtTarget.Text, links, rbCopy.Checked ? "复制文件" : "硬链接",
                dl / 1048576.0);
            return MessageBox.Show(msg, "确认执行", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        private void OpenReports()
        {
            try { Directory.CreateDirectory(reportRoot); Process.Start("explorer.exe", reportRoot); }
            catch { }
        }

        /// <summary>显示免责声明。firstRun=true 时是首次运行的强制确认（不同意即退出）</summary>
        private DialogResult ShowDisclaimer(bool firstRun)
        {
            using (var f = new Form())
            {
                f.Text = Disclaimer.Title + "   v" + Disclaimer.Version;
                f.Size = new Size(760, 620);
                f.MinimumSize = new Size(620, 460);
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.BackColor = C.Bg;
                f.ForeColor = C.Ink;
                f.Font = new Font("Microsoft YaHei UI", 9f);

                var head = new Label();
                head.Text = firstRun ? "首次使用前，请阅读并同意以下声明" : Disclaimer.Title;
                head.SetBounds(20, 14, 700, 26);
                head.Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
                head.ForeColor = C.Blue;
                f.Controls.Add(head);

                var box = new TextBox();
                box.Multiline = true;
                box.ReadOnly = true;
                box.ScrollBars = ScrollBars.Vertical;
                box.BorderStyle = BorderStyle.FixedSingle;
                box.BackColor = Color.White;
                box.Font = new Font("Microsoft YaHei UI", 9.5f);
                box.Text = Disclaimer.Body.Replace("\n", "\r\n");
                box.SetBounds(20, 48, f.ClientSize.Width - 40, f.ClientSize.Height - 48 - 62);
                box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
                box.SelectionStart = 0;
                f.Controls.Add(box);

                var ok = new Button();
                ok.Text = firstRun ? "我已知悉并同意" : "关闭";
                ok.SetBounds(f.ClientSize.Width - 220 - 20, f.ClientSize.Height - 48, 220, 34);
                ok.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                ok.DialogResult = DialogResult.OK;
                StylePrimary(ok, C.Green, C.GreenDark);
                f.Controls.Add(ok);
                f.AcceptButton = ok;

                if (firstRun)
                {
                    var no = new Button();
                    no.Text = "不同意，退出";
                    no.SetBounds(f.ClientSize.Width - 220 - 20 - 130 - 10, f.ClientSize.Height - 48, 130, 34);
                    no.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                    no.DialogResult = DialogResult.Cancel;
                    StyleNeutral(no);
                    f.Controls.Add(no);
                    f.CancelButton = no;
                }
                else
                {
                    f.CancelButton = ok;
                }

                return f.ShowDialog(this);
            }
        }

        /// <summary>
        /// 生成回滚脚本：rollback.cmd（系统 ANSI，双击即用，简中环境=GBK）
        /// + rollback.ps1（UTF-8 BOM，不受控制台代码页影响）+ created.txt 清单
        /// </summary>
        private void WriteRollback(string dir, List<string> created)
        {
            try
            {
                using (var sw = new StreamWriter(Path.Combine(dir, "rollback.cmd"), false, Encoding.Default))
                {
                    sw.WriteLine("@echo off");
                    sw.WriteLine("echo 撤销本次创建的文件（共 " + created.Count + " 个）");
                    foreach (var f in created) sw.WriteLine("del /f /q \"" + f + "\"");
                    sw.WriteLine("echo 完成");
                    sw.WriteLine("pause");
                }
                using (var sw = new StreamWriter(Path.Combine(dir, "rollback.ps1"), false, new UTF8Encoding(true)))
                {
                    sw.WriteLine("# 撤销本次创建的文件（共 " + created.Count + " 个）");
                    sw.WriteLine("$ErrorActionPreference = 'SilentlyContinue'");
                    foreach (var f in created) sw.WriteLine("Remove-Item -LiteralPath '" + f.Replace("'", "''") + "' -Force");
                    sw.WriteLine("Write-Host '完成'");
                }
                File.WriteAllLines(Path.Combine(dir, "created.txt"), created, new UTF8Encoding(true));
            }
            catch (Exception ex) { Log("写入回滚脚本失败: " + ex.Message); }
        }

        private void SetBusy(bool b)
        {
            btnScan.Enabled = !b;
            btnVerify.Enabled = !b;
            btnCancel.Enabled = b;
            if (b) btnExecute.Enabled = false;
            btnFetchManifest.Enabled = !b;
        }

        /// <summary>后台线程更新界面：窗口已关闭/句柄未创建时静默忽略，绝不抛异常</summary>
        private void SafeUi(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(a);
            }
            catch { }
        }

        /// <summary>任务开始时把进度条归零（只在开始这一刻归零，运行期间绝不再清零）</summary>
        private void ResetProgress()
        {
            barTotal = -1;
            bar.Value = 0;
            bar.Maximum = 100;
        }

        /// <summary>定时器驱动：只有任务在跑、且没被中止时才刷新进度</summary>
        private void RefreshProgress()
        {
            if (!busy || job == null || job.Cancelled) return;
            int total = job.Total;
            if (total <= 0) return;
            int done = job.Done;
            if (done < 0) done = 0;
            if (done > total) done = total;
            UpdateProgressBar(done, total,
                string.Format("{0}  {1}/{2}  ({3:P0})", job.Phase, done, total, (double)done / total));
        }

        /// <summary>单调更新进度条：只在数值变化时赋值，避免打断原生绘制（否则永远追不上、跑不到头）</summary>
        private void UpdateProgressBar(int done, int total, string msg)
        {
            if (InvokeRequired) { SafeUi(delegate { UpdateProgressBar(done, total, msg); }); return; }
            if (total <= 0) return;
            if (total != barTotal) { barTotal = total; bar.Maximum = total; }
            int v = Math.Max(0, Math.Min(done, bar.Maximum));
            if (bar.Value != v) bar.Value = v;
            if (!string.IsNullOrEmpty(msg))
            {
                lblStatus.Text = msg;
                if (job != null) job.Message = msg;
            }
        }

        /// <summary>任务正常收尾：把进度条补满并给出最终状态（被中止时不调用）</summary>
        private void FinishProgress(string status)
        {
            if (job == null) { SetStatus(status); return; }
            if (job.Cancelled) return;                 // 中止后不再动进度条
            if (job.Total > 0) UpdateProgressBar(job.Total, job.Total, status);
            else SetStatus(status);
        }

        public void Log(string msg)
        {
            if (InvokeRequired)
            {
                SafeUi(delegate { Log(msg); });
                return;
            }
            txtLog.AppendText(msg + "\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();
        }

        public void SetStatus(string msg)
        {
            if (InvokeRequired) { SafeUi(delegate { SetStatus(msg); }); return; }
            lblStatus.Text = msg;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 首次运行（或声明版本更新后）必须先阅读并同意免责声明
            if (acceptedEula != Disclaimer.Version)
            {
                if (ShowDisclaimer(true) != DialogResult.OK)
                {
                    Log("未同意免责声明，程序退出。");
                    Close();
                    return;
                }
                acceptedEula = Disclaimer.Version;
                SaveSettings();
                Log("已同意免责声明（v" + Disclaimer.Version + "）。");
            }
            if (rbModeA.Checked && !File.Exists(txtManifest.Text))
                Log("提示：没找到 data.xml，点右上角「下载最新清单」可以从官方拉到。");
        }
    }

    // ------------------------------------------------------------------ 入口
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--cli" || args[0] == "/cli"))
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                try
                {
                    var so = new StreamWriter(Console.OpenStandardOutput());
                    so.AutoFlush = true;
                    Console.SetOut(so);
                }
                catch { }
                return Cli(args);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int Cli(string[] args)
        {
            string man = null, src = null, tgt = null, mode = "A", way = "hardlink", planFile = null;
            bool exec = false, verify = false, skip = false;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--manifest": man = args[++i]; break;
                    case "--source": src = args[++i]; break;
                    case "--target": tgt = args[++i]; break;
                    case "--mode": mode = args[++i]; break;
                    case "--way": way = args[++i]; break;
                    case "--plan": planFile = args[++i]; break;
                    case "--execute": exec = true; break;
                    case "--verify": verify = true; break;
                    case "--skip-hash": skip = true; break;
                }
            }
            var log = new Action<string>(Console.WriteLine);
            Console.WriteLine("NarakaClientSwitcher CLI");
            foreach (var a in args)
                if (a == "--help" || a == "-h" || a == "/?") { Usage(); return 0; }
            if (mode != "A" && mode != "B")
            { Console.WriteLine("--mode 只能是 A 或 B"); return 2; }
            if (way != "hardlink" && way != "copy")
            { Console.WriteLine("--way 只能是 hardlink 或 copy"); return 2; }
            if (string.IsNullOrEmpty(tgt))
            { Console.WriteLine("缺少 --target 目录"); Usage(); return 2; }

            if (mode == "B")
            {
                if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
                { Console.WriteLine("缺少或找不到 --source 目录"); Usage(); return 2; }
                if (!Engine.SameVolume(src, tgt) && way == "hardlink")
                { Console.WriteLine("源和目标不在同一个卷，硬链接不可用；请改用 --way copy"); return 2; }
                if (!Directory.Exists(tgt))
                {
                    try { Directory.CreateDirectory(tgt); Console.WriteLine("已创建目标目录: " + tgt); }
                    catch (Exception cex) { Console.WriteLine("无法创建目标目录: " + cex.Message); return 2; }
                }
                var jp = new JobProgress();
                var res = Engine.Seed(src, tgt, way == "copy", jp, log);
                Console.WriteLine("播种: 新建 {0} 复制 {1} 失败 {2}", res.Linked, res.Copied, res.Failed);
                return res.Failed == 0 ? 0 : 1;
            }

            if (string.IsNullOrEmpty(man) || !File.Exists(man))
            { Console.WriteLine("缺少或找不到 --manifest（data.xml）"); Usage(); return 2; }
            if (!Directory.Exists(tgt))
            { Console.WriteLine("找不到 --target 目录（模式 A 需要指向已存在的安装目录）"); return 2; }
            var m = Engine.LoadManifest(man);
            Console.WriteLine("清单 {0} 项, 版本 {1}", m.Items.Count, m.Version);
            if (verify)
            {
                var vr = Engine.Verify(m, tgt, 300, new JobProgress(), log);
                return (vr.Missing == 0 && vr.SizeBad == 0 && vr.BadMd5 == 0) ? 0 : 1;
            }
            if (string.IsNullOrEmpty(src) || !Directory.Exists(src))
            { Console.WriteLine("缺少或找不到 --source 目录"); Usage(); return 2; }
            if (!Engine.SameVolume(src, tgt) && way == "hardlink")
            { Console.WriteLine("源和目标不在同一个卷，硬链接不可用；请改用 --way copy"); return 2; }

            List<PlanItem> planList;
            if (!string.IsNullOrEmpty(planFile))
            {
                if (!File.Exists(planFile)) { Console.WriteLine("找不到 --plan 文件: " + planFile); return 2; }
                planList = LoadPlan(planFile, m);
                Console.WriteLine("从报告装载计划: {0} 条 (链接 {1} / 下载 {2})",
                    planList.Count, Engine.CountAction(planList, "link"), Engine.CountAction(planList, "download"));
            }
            else
            {
                planList = Engine.Scan(m, src, tgt, skip, new JobProgress(), log);
                Console.WriteLine("可复用 {0} 个 / {1:N2} GB, 需下载 {2} 个 / {3:N1} MB",
                    Engine.CountAction(planList, "link"), Engine.SumBytes(planList, "link") / 1073741824.0,
                    Engine.CountAction(planList, "download"), Engine.SumBytes(planList, "download") / 1048576.0);
            }
            if (!exec) return 0;
            var ex = Engine.Execute(m, planList, tgt, way == "copy", new JobProgress(), log);
            Console.WriteLine("执行: 链接 {0} 复制 {1} 下载 {2} 失败 {3}", ex.Linked, ex.Copied, ex.Downloaded, ex.Failed);
            return ex.Failed == 0 ? 0 : 1;
        }

        /// <summary>读取 dry-run 生成的 plan.csv（path,action,reason,wantKB,srcBytes,target,source）</summary>
        private static List<PlanItem> LoadPlan(string csvPath, Manifest m)
        {
            var byPath = new Dictionary<string, ManifestItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in m.Items) byPath[it.Path] = it;
            var list = new List<PlanItem>();
            var lines = File.ReadAllLines(csvPath);
            for (int i = 1; i < lines.Length; i++)   // 跳过表头
            {
                if (lines[i].Length == 0) continue;
                var f = SplitCsvLine(lines[i]);
                if (f.Count < 7) continue;
                ManifestItem mi;
                if (!byPath.TryGetValue(f[0], out mi)) continue;
                var pi = new PlanItem();
                pi.Item = mi;
                pi.Action = f[1];
                long sb;
                long.TryParse(f[4], out sb);
                pi.SrcBytes = sb;
                pi.Target = f[5];
                pi.Source = f[6];
                list.Add(pi);
            }
            return list;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQ)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQ = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQ = true;
                else if (c == ',') { res.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(c);
            }
            res.Add(sb.ToString());
            return res;
        }

        private static void Usage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  NarakaClientSwitcher.exe                                   # 打开图形界面");
            Console.WriteLine("  ... --cli --manifest <data.xml> --source <源目录> --target <目标目录>");
            Console.WriteLine("        [--skip-hash] [--execute] [--verify] [--way hardlink|copy]");
            Console.WriteLine("  ... --cli --manifest <data.xml> --target <目标目录> --plan <plan.csv> --execute");
            Console.WriteLine("        # --plan 复用干跑报告，跳过重新计算 MD5");
            Console.WriteLine("  ... --cli --mode B --source <源目录> --target <目标目录> [--way copy]");
        }
    }
}
