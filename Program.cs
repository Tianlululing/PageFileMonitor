using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PageFileMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly Label _lblPageFile = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _lblCommit = new();
    private readonly Label _lblIoHeader = new();
    private readonly List<Label> _volRows = new();
    private readonly List<Label> _volSubRows = new();
    private readonly List<VolStat> _orderedVols = new();
    private readonly GroupBox _grpPressure = new();
    private readonly Label _lblSize = new();
    private readonly NumericUpDown _numMB = new();
    private readonly Button _btnPressure = new();
    private readonly Label _lblPressureStatus = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private IntPtr _pdhQuery;
    private IntPtr _counterRead;
    private IntPtr _counterWrite;
    private IntPtr _counterPageUsage;
    private byte[] _pdhBuffer = Array.Empty<byte>();
    private DateTime _lastSample = DateTime.UtcNow;
    private bool _hasLastSample;

    private IntPtr _pressurePtr;
    private ulong _pressureBytes;
    private DateTime _pressureStart;

    private sealed class PfInfo
    {
        public string Drive = "";    // 显示用盘符,如 "F:"
        public string Path = "";     // 规范化路径,如 "F:\pagefile.sys"
        public bool Active;          // 是否已生效(枚举自 EnumPageFiles)
        public long TotalPages;      // 生效:文件容量(页)
        public long UsedMB = -1;     // 生效:按使用率折算的已用 MB;-1 = 未知
        public long InitMB, MaxMB;   // 未生效:注册表配置的大小(MB)
    }

    private sealed class VolStat
    {
        public string Drive = "?";       // 卷盘符,如 "F:";"?" = 路径无法解析
        public int DiskNumber = -1;      // 所在物理磁盘号(仅展示用);-1 = 未知
        public List<PfInfo> Files = new();
        public double ReadRate, WriteRate, TotalRead, TotalWrite;
    }

    // key = 卷盘符(大写,"?" 表示无法解析的路径);仅当行集合变化时重建 UI
    private readonly Dictionary<string, VolStat> _vols = new();
    private readonly Dictionary<string, int> _driveToDiskCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string path, long pages)>> _tickVolFiles = new();
    private readonly HashSet<string> _tickActivePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PfInfo>> _tickPendingFiles = new();
    private long _pageSize = 4096;

    private readonly EnumPageFilesProc _enumCb;
    private long _tmpInUsePages;
    private long _tmpTotalPages;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = "页面文件 / 磁盘监控";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(552), S(322));

        _lblPageFile.SetBounds(S(16), S(14), S(520), S(20));
        _lblPageFile.Text = "页面文件:加载中…";

        _progress.SetBounds(S(16), S(38), S(520), S(24));

        _lblCommit.SetBounds(S(16), S(70), S(520), S(20));
        _lblCommit.Text = "提交内存:加载中…";

        _lblIoHeader.SetBounds(S(16), S(96), S(520), S(20));
        _lblIoHeader.Text = "页面文件所在卷 I/O(速率 / 累计):";

        _grpPressure.SetBounds(S(10), S(156), S(532), S(132));
        _grpPressure.Text = "提交内存压力";

        _lblSize.SetBounds(S(16), S(34), S(76), S(20));
        _lblSize.Text = "大小 (MB):";

        _numMB.SetBounds(S(96), S(31), S(90), S(24));
        _numMB.Minimum = 1;
        _numMB.Maximum = 65536;
        _numMB.Value = 1024;

        _btnPressure.SetBounds(S(210), S(30), S(160), S(26));
        _btnPressure.Text = "申请提交内存";
        _btnPressure.Click += BtnPressure_Click;

        _lblPressureStatus.SetBounds(S(16), S(74), S(500), S(44));
        _lblPressureStatus.Text = "";

        _grpPressure.Controls.AddRange(new Control[]
        {
            _lblSize, _numMB, _btnPressure, _lblPressureStatus
        });
        Controls.AddRange(new Control[]
        {
            _lblPageFile, _progress, _lblCommit, _lblIoHeader, _grpPressure
        });

        _enumCb = OnEnumPageFile;
        InitPdh();
        _timer.Tick += (s, e) => UpdateAll();
        _timer.Start();
        UpdateAll();
    }

    // 96 DPI 设计坐标 → 当前 DPI 像素
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private static string FormatBytes(double b, bool rate)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return b.ToString("F2") + " " + units[i] + (rate ? "/s" : "");
    }

    private static string NormalizePageFilePath(string? path)
    {
        string name = path ?? "";
        if (name.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)) name = name.Substring(4);
        return name;
    }

    private void InitPdh()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0)
        {
            _pdhQuery = IntPtr.Zero;
            return;
        }
        // 分区(逻辑卷)级读写计数器,实例名为盘符如 "F:"
        if (PdhAddEnglishCounterW(_pdhQuery, @"\LogicalDisk(*)\Disk Read Bytes/sec",
                IntPtr.Zero, out _counterRead) != 0 ||
            PdhAddEnglishCounterW(_pdhQuery, @"\LogicalDisk(*)\Disk Write Bytes/sec",
                IntPtr.Zero, out _counterWrite) != 0)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
            return;
        }
        // 通配实例:每个页面文件一个使用率,用于折算已用量
        PdhAddEnglishCounterW(_pdhQuery, @"\Paging File(*)\% Usage",
            IntPtr.Zero, out _counterPageUsage);
        PdhCollectQueryData(_pdhQuery);
    }

    private bool OnEnumPageFile(IntPtr context, ref ENUM_PAGE_FILE_INFORMATION info, IntPtr filename)
    {
        // 原生回调原型为 (pContext, pPageFileInfo, lpFilename),顺序不可调换;
        // 尺寸字段在 Win10/11 上以内存页(通常 4KB)为单位
        _tmpTotalPages += info.TotalSize;
        _tmpInUsePages += info.TotalInUse;

        string name = NormalizePageFilePath(Marshal.PtrToStringUni(filename));
        _tickActivePaths.Add(name);
        string key = name.Length >= 2 && name[1] == ':' ? name.Substring(0, 2).ToUpperInvariant() : "?";
        if (!_tickVolFiles.TryGetValue(key, out var files))
        {
            files = new List<(string, long)>();
            _tickVolFiles[key] = files;
        }
        files.Add((name, info.TotalSize));
        return true;
    }

    // 读取注册表里配置的页面文件,过滤掉已生效的,剩余即"未生效(需重启)"的
    private void CollectPendingPageFiles()
    {
        _tickPendingFiles.Clear();
        var cfg = ReadConfiguredPageFiles();
        foreach (var (path, initMB, maxMB) in cfg)
        {
            string name = NormalizePageFilePath(path);
            if (_tickActivePaths.Contains(name)) continue;   // 已生效,不重复计
            var pf = new PfInfo { Path = name, Active = false, InitMB = initMB, MaxMB = maxMB, UsedMB = -1 };
            string key = name.Length >= 2 && name[1] == ':' ? name.Substring(0, 2).ToUpperInvariant() : "?";
            pf.Drive = key;
            if (!_tickPendingFiles.TryGetValue(key, out var list))
            {
                list = new List<PfInfo>();
                _tickPendingFiles[key] = list;
            }
            list.Add(pf);
        }
    }

    // HKLM\...\Memory Management\PagingFiles:REG_MULTI_SZ,每项 "<路径> <初始MB> <最大MB>"
    private static List<(string path, long initMB, long maxMB)> ReadConfiguredPageFiles()
    {
        var list = new List<(string, long, long)>();
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
                0, KEY_READ, out IntPtr hKey) != ERROR_SUCCESS)
        {
            return list;
        }
        try
        {
            int size = 0;
            if (RegQueryValueExW(hKey, "PagingFiles", IntPtr.Zero, out uint type,
                    null, ref size) != ERROR_SUCCESS || type != REG_MULTI_SZ || size < 4)
            {
                return list;
            }
            var buf = new byte[size];
            if (RegQueryValueExW(hKey, "PagingFiles", IntPtr.Zero, out type,
                    buf, ref size) != ERROR_SUCCESS || type != REG_MULTI_SZ)
            {
                return list;
            }
            string raw = Encoding.Unicode.GetString(buf, 0, Math.Max(0, size - 2));
            foreach (var entry in raw.Split('\0'))
            {
                var parts = entry.Split(' ');
                if (parts.Length < 3) continue;
                long.TryParse(parts[^2], out long initMB);
                long.TryParse(parts[^1], out long maxMB);
                string path = string.Join(" ", parts[..^2]).Trim();
                if (path.Length >= 2 && path[1] == ':')
                {
                    list.Add((path, initMB, maxMB));
                }
            }
        }
        finally
        {
            RegCloseKey(hKey);
        }
        return list;
    }

    // 盘符("C:")→ 物理磁盘号;失败返回 -1 且不缓存,允许下次重试
    private int GetDiskNumber(string drive)
    {
        if (drive.Length != 2 || drive[1] != ':') return -1;
        if (_driveToDiskCache.TryGetValue(drive, out int cached)) return cached;
        int num = -1;
        IntPtr h = CreateFileW(@"\\.\" + drive, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h != INVALID_HANDLE_VALUE)
        {
            if (DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0,
                out STORAGE_DEVICE_NUMBER sdn, (uint)Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(),
                out _, IntPtr.Zero))
            {
                num = (int)sdn.DeviceNumber;
            }
            CloseHandle(h);
        }
        if (num >= 0) _driveToDiskCache[drive] = num;
        return num;
    }

    private void RebuildVolRows()
    {
        foreach (var l in _volRows) { Controls.Remove(l); l.Dispose(); }
        foreach (var l in _volSubRows) { Controls.Remove(l); l.Dispose(); }
        _volRows.Clear();
        _volSubRows.Clear();
        _orderedVols.Clear();

        // 子行显示条件:存在多个卷,或存在未生效(需重启)的页面文件
        bool showSub = _vols.Count > 1 ||
                       _vols.Values.Any(d => d.Files.Any(f => !f.Active));
        int y = 122;
        foreach (var stat in _vols.Values
                     .OrderBy(d => d.Drive == "?" ? "~" : d.Drive, StringComparer.Ordinal))
        {
            var lbl = new Label { Text = RowText(stat) };
            lbl.SetBounds(S(16), S(y), S(520), S(20));
            Controls.Add(lbl);
            _volRows.Add(lbl);
            _orderedVols.Add(stat);
            y += 26;
            if (showSub)
            {
                var sub = new Label
                {
                    Text = SubText(stat),
                    ForeColor = System.Drawing.Color.FromArgb(96, 96, 96)
                };
                sub.SetBounds(S(36), S(y), S(500), S(18));
                Controls.Add(sub);
                _volSubRows.Add(sub);
                y += 20;
            }
        }

        // 压力功能区与窗口高度随行数自适应
        int grpTop = y + 8;
        _grpPressure.SetBounds(S(10), S(grpTop), S(532), S(132));
        ClientSize = new Size(ClientSize.Width, S(grpTop + 132 + 14));
    }

    private static string RowText(VolStat st)
    {
        string dn = st.DiskNumber >= 0 ? st.DiskNumber.ToString() : "?";
        return $"卷 {st.Drive}(磁盘 {dn})   " +
               $"读取:{FormatBytes(st.ReadRate, rate: true)}(累计 {FormatBytes(st.TotalRead, rate: false)})   " +
               $"写入:{FormatBytes(st.WriteRate, rate: true)}(累计 {FormatBytes(st.TotalWrite, rate: false)})";
    }

    private string SubText(VolStat st)
    {
        var parts = new List<string>();
        foreach (var f in st.Files)
        {
            if (f.Active)
            {
                long totalMB = (long)(f.TotalPages * _pageSize / (1024 * 1024));
                string used = f.UsedMB < 0 ? "N/A" : $"{f.UsedMB} MB";
                parts.Add(totalMB > 0
                    ? $"页面文件:已用 {used} / 总计 {totalMB} MB"
                    : $"页面文件:已用 {used}");
            }
            else
            {
                parts.Add($"页面文件:未生效(需重启,配置 {f.InitMB}~{f.MaxMB} MB)");
            }
        }
        return string.Join("; ", parts);
    }

    // 通配计数器 → [(实例名, 值)];LogicalDisk 实例名为盘符,Paging File 实例名为路径
    private List<(string name, double value)>? GetCounterArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        int size = 0;
        uint err = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out _, null);
        if (err != PDH_MORE_DATA) return null;
        var buf = new byte[size];
        err = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out int count, buf);
        if (err != 0 || count <= 0) return null;
        var list = new List<(string, double)>(count);
        var gh = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = gh.AddrOfPinnedObject();
            for (int i = 0; i < count; i++)
            {
                int off = i * 24;   // PDH_FMT_COUNTERVALUE_ITEM_W 在 x64 上的大小
                string? nm = Marshal.PtrToStringUni(Marshal.ReadIntPtr(basePtr, off));
                uint cs = (uint)Marshal.ReadInt32(basePtr, off + 8);
                double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(basePtr, off + 16));
                if (nm != null && cs is 0u or 1u) list.Add((nm, val));
            }
        }
        finally
        {
            gh.Free();
        }
        return list;
    }

    // LogicalDisk 实例名 "F:" → "F:";其余(如 _Total)返回 null
    private static string? ParseDriveLetter(string? instance)
    {
        if (string.IsNullOrEmpty(instance)) return null;
        string s = instance.Trim();
        return s.Length == 2 && s[1] == ':' ? s.ToUpperInvariant() : null;
    }

    private void UpdateAll()
    {
        try
        {
            UpdateAllCore();
        }
        catch
        {
            // 单次采样失败(如计数器暂时不可用)只跳过本周期,下个周期重试
        }
    }

    private void UpdateAllCore()
    {
        // 压力块到时自动释放
        if (_pressurePtr != IntPtr.Zero &&
            (DateTime.UtcNow - _pressureStart).TotalMilliseconds >= 60_000)
        {
            ReleasePressure(automatic: true);
        }

        // 一次性采集所有计数器
        var pageFracs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var volRates = new Dictionary<string, (double r, double w)>();
        if (_pdhQuery != IntPtr.Zero && PdhCollectQueryData(_pdhQuery) == 0)
        {
            var pf = GetCounterArray(_counterPageUsage);
            if (pf != null)
            {
                foreach (var (name, v) in pf)
                {
                    // PDH 的 "% Usage" 以百分数返回(0..100):0.99 表示 0.99%,必须固定除以 100
                    if (v >= 0 && v <= 100.5)
                    {
                        pageFracs[NormalizePageFilePath(name)] = v / 100.0;
                    }
                }
            }
            var reads = GetCounterArray(_counterRead);
            var writes = GetCounterArray(_counterWrite);
            if (reads != null)
            {
                foreach (var (name, v) in reads)
                {
                    string? dl = ParseDriveLetter(name);
                    if (dl != null) volRates[dl] = (v, 0);
                }
            }
            if (writes != null)
            {
                foreach (var (name, v) in writes)
                {
                    string? dl = ParseDriveLetter(name);
                    if (dl != null)
                    {
                        var cur = volRates.TryGetValue(dl, out var c) ? c : (0.0, 0.0);
                        volRates[dl] = (cur.Item1, v);
                    }
                }
            }
        }

        // ---- 生效中的页面文件 + 注册表中配置的(未生效)页面文件 ----
        _tickVolFiles.Clear();
        _tickActivePaths.Clear();
        _tmpInUsePages = 0;
        _tmpTotalPages = 0;
        EnumPageFiles(_enumCb, IntPtr.Zero);
        CollectPendingPageFiles();

        // 行集合 = 生效卷 ∪ 未生效卷
        var rowKeys = new HashSet<string>(_tickVolFiles.Keys, StringComparer.Ordinal);
        rowKeys.UnionWith(_tickPendingFiles.Keys);

        bool changed = rowKeys.Count != _vols.Count;
        if (!changed)
        {
            foreach (var k in rowKeys)
            {
                if (!_vols.ContainsKey(k)) { changed = true; break; }
            }
        }
        if (changed)
        {
            _vols.Clear();
            foreach (var k in rowKeys.OrderBy(d => d == "?" ? "~" : d, StringComparer.Ordinal))
            {
                var stat = new VolStat { Drive = k, DiskNumber = GetDiskNumber(k) };
                if (_tickVolFiles.TryGetValue(k, out var act))
                {
                    foreach (var (path, pages) in act)
                    {
                        stat.Files.Add(new PfInfo
                        {
                            Drive = k,
                            Path = path,
                            Active = true,
                            TotalPages = pages,
                            UsedMB = -1
                        });
                    }
                }
                if (_tickPendingFiles.TryGetValue(k, out var pend))
                {
                    stat.Files.AddRange(pend);
                }
                _vols[k] = stat;
            }
            RebuildVolRows();
        }

        var pi = new PERFORMANCE_INFORMATION();
        pi.cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>();
        if (GetPerformanceInfo(ref pi, pi.cb))
        {
            _pageSize = (long)pi.PageSize;
        }

        // ---- 页面文件总量/已用(仅统计生效中的) ----
        if (_tmpTotalPages > 0)
        {
            long totalMB = (long)(_tmpTotalPages * _pageSize / (1024 * 1024));
            long usedMB = (long)(_tmpInUsePages * _pageSize / (1024 * 1024));   // 后备:多为 0
            if (pageFracs.Count > 0)
            {
                double usedBytes = 0;
                bool allKnown = true;
                foreach (var kv in _tickVolFiles)
                {
                    foreach (var (path, pages) in kv.Value)
                    {
                        if (pageFracs.TryGetValue(path, out double frac))
                        {
                            usedBytes += frac * pages * _pageSize;
                        }
                        else
                        {
                            allKnown = false;
                        }
                    }
                }
                if (allKnown) usedMB = (long)Math.Round(usedBytes / (1024 * 1024));
            }
            if (usedMB > totalMB) usedMB = totalMB;
            _lblPageFile.Text = $"页面文件:已用 {usedMB} MB / 总计 {totalMB} MB(占 {usedMB * 100L / totalMB}%)";
            int max = (int)Math.Min(totalMB, int.MaxValue);
            if (_progress.Maximum != max) _progress.Maximum = max;
            _progress.Value = (int)Math.Min(usedMB, max);

            // 分卷、分文件的已用更新
            foreach (var kv in _tickVolFiles)
            {
                if (!_vols.TryGetValue(kv.Key, out var st)) continue;
                foreach (var (path, pages) in kv.Value)
                {
                    var f = st.Files.FirstOrDefault(x =>
                        x.Active && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (f == null) continue;
                    f.TotalPages = pages;
                    if (pageFracs.TryGetValue(path, out double frac))
                    {
                        f.UsedMB = (long)Math.Round(frac * pages * _pageSize / (1024 * 1024));
                    }
                    else
                    {
                        f.UsedMB = -1;
                    }
                }
            }
        }
        else
        {
            _lblPageFile.Text = "页面文件:无生效中的页面文件";
            _progress.Value = 0;
        }

        // ---- 提交内存(便于观察压力功能效果) ----
        if (GetPerformanceInfo(ref pi, pi.cb))
        {
            double commit = (double)(ulong)pi.CommitTotal * (double)(ulong)pi.PageSize;
            double limit = (double)(ulong)pi.CommitLimit * (double)(ulong)pi.PageSize;
            _lblCommit.Text = $"提交内存:{FormatBytes(commit, rate: false)} / {FormatBytes(limit, rate: false)}";
        }
        else
        {
            _lblCommit.Text = "提交内存:N/A";
        }

        // ---- 各卷速率与累计 ----
        var now = DateTime.UtcNow;
        double dt = _hasLastSample ? (now - _lastSample).TotalSeconds : 0;
        _lastSample = now;
        _hasLastSample = true;
        for (int i = 0; i < _orderedVols.Count && i < _volRows.Count; i++)
        {
            var st = _orderedVols[i];
            if (volRates.TryGetValue(st.Drive, out var rw))
            {
                st.ReadRate = rw.r;
                st.WriteRate = rw.w;
            }
            else
            {
                st.ReadRate = 0;
                st.WriteRate = 0;
            }
            if (dt > 0)
            {
                st.TotalRead += st.ReadRate * dt;
                st.TotalWrite += st.WriteRate * dt;
            }
            _volRows[i].Text = RowText(st);
            if (i < _volSubRows.Count)
            {
                _volSubRows[i].Text = SubText(st);
            }
        }

        // ---- 压力块倒计时 ----
        if (_pressurePtr != IntPtr.Zero)
        {
            double remain = 60 - (DateTime.UtcNow - _pressureStart).TotalSeconds;
            if (remain < 0) remain = 0;
            _lblPressureStatus.Text =
                $"已申请 {_pressureBytes / (1024 * 1024)} MB 提交内存,{Math.Ceiling(remain)} 秒后自动释放,或点击立即释放";
        }
    }

    private void BtnPressure_Click(object? sender, EventArgs e)
    {
        if (_pressurePtr != IntPtr.Zero)
        {
            ReleasePressure(automatic: false);
            return;
        }
        long mb = (long)_numMB.Value;
        ulong bytes = (ulong)mb * 1024UL * 1024UL;
        IntPtr p = VirtualAlloc(IntPtr.Zero, (UIntPtr)bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (p == IntPtr.Zero)
        {
            _lblPressureStatus.Text = $"申请失败(错误码 {Marshal.GetLastWin32Error()}):提交上限可能不足";
            return;
        }
        _pressurePtr = p;
        _pressureBytes = bytes;
        _pressureStart = DateTime.UtcNow;
        _btnPressure.Text = "立即释放";
    }

    private void ReleasePressure(bool automatic)
    {
        FreePressure();
        _btnPressure.Text = "申请提交内存";
        _lblPressureStatus.Text = automatic
            ? "已达 60 秒时限,压力内存已自动释放"
            : "压力内存已释放";
    }

    private void FreePressure()
    {
        if (_pressurePtr == IntPtr.Zero) return;
        VirtualFree(_pressurePtr, UIntPtr.Zero, MEM_RELEASE);
        _pressurePtr = IntPtr.Zero;
        _pressureBytes = 0;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _timer.Stop();
        FreePressure();
        if (_pdhQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
        }
    }

    // ---------- Win32 互操作 ----------

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    private const uint KEY_READ = 0x20019;
    private const uint REG_MULTI_SZ = 7;
    private const int ERROR_SUCCESS = 0;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_PAGE_FILE_INFORMATION
    {
        public uint cb;
        public uint Reserved;
        public uint TotalSize;
        public uint TotalInUse;
        public uint PeakUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    private delegate bool EnumPageFilesProc(
        IntPtr context,
        ref ENUM_PAGE_FILE_INFORMATION info,
        IntPtr filename);

    [DllImport("psapi.dll", EntryPoint = "EnumPageFilesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPageFiles(EnumPageFilesProc callback, IntPtr context);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr counterType, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, byte[]? itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr handle, uint ioControlCode, IntPtr inBuffer, uint inSize,
        out STORAGE_DEVICE_NUMBER outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, uint access, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string valueName, IntPtr reserved, out uint type,
        byte[]? data, ref int dataSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);
}
