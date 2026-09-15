using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Management;
using Microsoft.Win32;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public class Guard : Window {
 enum PageKind { Overview, Details, Settings, Evidence }
 enum DataViewKind { Raw, Derived, Conclusion }
 enum ThemeMode { System, Light, Dark }
 enum ChartDomain { Time, Frequency }
 Grid root, main;
 StackPanel overviewDisks, detailsDisks, detailsDataStack;
 Canvas detailsChart;
 Border detailsChartCard;
 StackPanel chartOptionsPanel;
 ComboBox detailsLayerSelector, detailsDriveSelector;
 ComboBox chartDomainSelector, themeSelector;
 CheckBox chartReadCheck, chartWriteCheck, chartIopsCheck, chartQueueCheck, chartActiveCheck;
 TextBlock detailsChartTitle, detailsSelectionHint;
 bool updatingDetailsControls;
 DataViewKind selectedDataView = DataViewKind.Raw;
 string selectedDetailsDriveName = "";
 ThemeMode themeMode = ThemeMode.System;
 ChartDomain chartDomain = ChartDomain.Time;
 TextBlock overviewRead, overviewWrite, overviewIops, overviewQueue, overviewActive, overviewHealth, overviewStatus, overviewNode, overviewProbe;
 TextBlock pageSubtitle;
 Border sidebar;
 bool busy;
 DispatcherTimer timer;
 Task<Dictionary<int,Health>> healthTask;
 DateTime lastHealthPollStart = DateTime.MinValue;
 int performanceIntervalSeconds = 1;
 int healthPollMinutes = 5;
 Dictionary<int,Health> health = new Dictionary<int,Health>();
 Dictionary<string,Drive> drives = new Dictionary<string,Drive>();
 DisplayOptions options;
 PageKind currentPage = PageKind.Overview;
 FloatingPanel floating;
 bool ping0Enabled;
 string ping0TargetIp = "";
 string ping0ApiKey = ""; // API Key 只在本次进程内保存在内存，不写入明文配置。
 int ping0IntervalMinutes = 30;
 Ping0Snapshot ping0 = new Ping0Snapshot();
 Task<Ping0Snapshot> ping0Task;
 DateTime lastPing0PollStart = DateTime.MinValue;
 TextBlock ping0StatusText;
 ComboBox ping0IntervalSelector;
 bool probeEnabled;
 string probePath = "";
 int probeSizeMb = 64;
 int probeMaxSeconds = 30;
 int probeIntervalMinutes = 0;
 ProbeResult probe = new ProbeResult();
 Task<ProbeResult> probeTask;
 DateTime lastProbeStart = DateTime.MinValue;
 TextBlock probeStatusText;
 ComboBox probeSizeSelector, probeDurationSelector, probeScheduleSelector;
 System.Windows.Forms.NotifyIcon tray;
 bool exitRequested;
 bool closeToTray;
 class ProbeTarget {
  public string Key, Label, Folder = "";
  public int Index;
  public string[] Roots = new string[0];
  public bool Enabled;
  public ProbeResult Result = new ProbeResult();
 }
 List<ProbeTarget> probeTargets = new List<ProbeTarget>();
 Dictionary<string,string> savedProbeTargets = new Dictionary<string,string>();
 StackPanel probeRows;
 bool MainVisible { get { return IsVisible && WindowState != WindowState.Minimized; } }

 static List<ProbeTarget> DiscoverProbeTargets() {
  var result = new List<ProbeTarget>();
  using(var search = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
  using(var disks = search.Get()) foreach(ManagementObject disk in disks) {
   using(disk) {
    int index = Convert.ToInt32(disk["Index"]);
    var roots = new List<string>();
    using(var partitions = disk.GetRelated("Win32_DiskPartition")) foreach(ManagementObject part in partitions) using(part)
     using(var volumes = part.GetRelated("Win32_LogicalDisk")) foreach(ManagementObject volume in volumes) using(volume) roots.Add(Convert.ToString(volume["DeviceID"]) + "\\");
    string serial = Convert.ToString(disk["SerialNumber"]).Trim();
    string model = Convert.ToString(disk["Model"]);
    string identity = model + "|" + (serial.Length > 0 ? serial : Convert.ToString(disk["PNPDeviceID"]));
    result.Add(new ProbeTarget { Index = index, Key = Convert.ToBase64String(Encoding.UTF8.GetBytes(identity)).TrimEnd('='), Label = "磁盘 " + index + " · " + model, Roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
   }
  }
  return result.OrderBy(x => x.Index).ToList();
 }
 static bool TargetMatches(ProbeTarget target, string folder, List<ProbeTarget> current) {
  try {
   if(!Path.IsPathRooted(folder) || folder.StartsWith(@"\\")) return false;
   string full = Path.GetFullPath(folder);
   var owners = current.Where(x => x.Roots.Any(r => String.Equals(r, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))).ToArray();
   if(owners.Length != 1 || owners[0].Key != target.Key) return false;
   for(var d = new DirectoryInfo(full); d != null; d = d.Parent)
    if(d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return false;
   return true;
  } catch { return false; }
 }
 async void LoadProbeTargets() {
  try {
   var found = await Task.Run(() => DiscoverProbeTargets());
   foreach(var target in found) {
    string value;
    if(savedProbeTargets.TryGetValue(target.Key, out value)) {
     var fields = value.Split(new [] { '|' }, 2);
     if(fields.Length == 2) { target.Enabled = fields[0] == "1"; target.Folder = Encoding.UTF8.GetString(Convert.FromBase64String(fields[1])); }
    } else if(!String.IsNullOrWhiteSpace(probePath) && TargetMatches(target, probePath, found)) { target.Folder = probePath; }
    else if(target.Roots.Length > 0) target.Folder = Path.Combine(target.Roots[0], "DiskGuardProbe");
   }
   probeTargets = found; BuildProbeRows();
  } catch { if(probeRows != null) { probeRows.Children.Clear(); probeRows.Children.Add(Text("磁盘映射未取得，探针不可用", 12, Muted)); } }
 }
 void BuildProbeRows() {
  if(probeRows == null) return;
  probeRows.Children.Clear();
  foreach(var target in probeTargets) {
   var row = new Grid { Margin = new Thickness(0,0,0,10) };
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(158) });
   row.ColumnDefinitions.Add(new ColumnDefinition());
   row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   var label = Text("磁盘 " + target.Index + "  " + String.Join(" / ", target.Roots), 12, Ink); label.ToolTip = target.Label; label.VerticalAlignment = VerticalAlignment.Center;
   row.Children.Add(label);
   var input = StyledTextBox(Double.NaN, target.Folder, new Thickness(0,0,12,0)); input.HorizontalAlignment = HorizontalAlignment.Stretch;
   input.IsEnabled = target.Roots.Length > 0;
   input.ToolTip = "填写这块物理硬盘上的文件夹；不支持网络盘、目录联接或无法唯一映射的卷。";
   input.TextChanged += (s,e) => target.Folder = input.Text.Trim(); input.LostFocus += (s,e) => SaveOptions();
   Grid.SetColumn(input,1); row.Children.Add(input);
   var check = new CheckBox { Content = "参与测试", Foreground = Ink, IsChecked = target.Enabled, IsEnabled = target.Roots.Length > 0, VerticalAlignment = VerticalAlignment.Center };
   check.Checked += (s,e) => { target.Enabled = true; SaveOptions(); };
   check.Unchecked += (s,e) => { target.Enabled = false; SaveOptions(); };
   var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
   var browse = ActionButton("浏览…", () => {
    using(var dialog = new System.Windows.Forms.FolderBrowserDialog()) {
     dialog.Description = "选择 " + target.Label + " 上的目标文件夹";
     if(Directory.Exists(target.Folder)) dialog.SelectedPath = target.Folder;
     else if(target.Roots.Length > 0) dialog.SelectedPath = target.Roots[0];
     if(dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) { input.Text = dialog.SelectedPath; SaveOptions(); }
    }
   }, HorizontalAlignment.Left);
   browse.IsEnabled = target.Roots.Length > 0; browse.Margin = new Thickness(0,0,10,0);
   actions.Children.Add(browse); actions.Children.Add(check);
   Grid.SetColumn(actions,2); row.Children.Add(actions); probeRows.Children.Add(row);
   probeRows.Children.Add(Text(target.Roots.Length == 0 ? "没有可用盘符，暂不支持探针测试" : target.Result.Updated == DateTime.MinValue ? "尚未测试" : target.Result.Summary, 11, Muted));
  }
  if(probeTargets.Count == 0) probeRows.Children.Add(Text("正在识别物理硬盘及盘符…",12,Muted));
 }
 void RestoreMain() { Show(); WindowState = WindowState.Normal; Activate(); RenderCurrent(); }
 void HideToTray() { SaveOptions(); Hide(); if(floating != null) floating.Hide(); }
 void ExitApplication() {
  if(probeTask != null) { MessageBox.Show("探针正在运行，请等待清理完成后退出。"); return; }
  exitRequested = true; Close();
 }
 void CreateTray() {
  tray = new System.Windows.Forms.NotifyIcon { Text = "磁盘观察室", Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location), Visible = true };
  var menu = new System.Windows.Forms.ContextMenuStrip();
  menu.Items.Add("打开主界面", null, (s,e) => Dispatcher.BeginInvoke(new Action(RestoreMain)));
  menu.Items.Add("显示悬浮窗", null, (s,e) => Dispatcher.BeginInvoke(new Action(() => { if(floating == null) floating = new FloatingPanel(this); floating.Show(); floating.Refresh(); })));
  menu.Items.Add("退出程序", null, (s,e) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
  tray.ContextMenuStrip = menu;
  tray.DoubleClick += (s,e) => Dispatcher.BeginInvoke(new Action(RestoreMain));
 }
  ComboBox performanceIntervalSelector, healthIntervalSelector;
  ComboBox fontSelector;
 string fontFamilyName = "Microsoft YaHei UI";
 bool settingsPersistence = true;

 static Brush Ink = new SolidColorBrush(Color.FromRgb(32,39,52));
 static Brush Muted = new SolidColorBrush(Color.FromRgb(121,132,148));
 static Brush Blue = new SolidColorBrush(Color.FromRgb(54,116,216));
 static Brush Teal = new SolidColorBrush(Color.FromRgb(42,147,128));
 static Brush Amber = new SolidColorBrush(Color.FromRgb(183,115,25));
 static Brush Red = new SolidColorBrush(Color.FromRgb(185,72,83));
 static Brush Violet = new SolidColorBrush(Color.FromRgb(112,92,190));
 static Brush CardFill = new SolidColorBrush(Color.FromArgb(226,255,255,255));
 static Brush CardStroke = new SolidColorBrush(Color.FromArgb(210,255,255,255));
 static Brush SurfaceFill = new SolidColorBrush(Color.FromArgb(180,255,255,255));
 static Brush SidebarFill = new SolidColorBrush(Color.FromArgb(195,248,248,251));
 static Brush InputFill = new SolidColorBrush(Color.FromArgb(220,255,255,255));
 static Brush InputBorder = new SolidColorBrush(Color.FromArgb(210,205,215,228));
 static Brush InputHighlight = new SolidColorBrush(Color.FromArgb(220,219,233,251));
 static Brush ChartFill = new SolidColorBrush(Color.FromArgb(90,245,248,252));
 static Brush TrackFill = new SolidColorBrush(Color.FromArgb(80,180,190,205));
 static bool DarkPalette;

 class DisplayOptions {
  public bool Read = true, Write = true, Iops = true, Queue = true, Active = true;
  public bool Temperature = true, Health = true, Wear = true, Errors = true;
  public bool PowerOnHours = true, Endurance = true, Workload = true;
  public bool Any() { return Read || Write || Iops || Queue || Active || Temperature || Health || Wear || Errors || PowerOnHours || Endurance || Workload; }
 }

 class Ping0Snapshot {
  public bool Enabled;
  public string Status = "未启用";
  public string Ip = "";
  public string Location = "";
  public string Asn = "";
  public string AsnName = "";
  public string Org = "";
  public string IpRisk = "";
  public string IsIdc = "";
  public string IsNative = "";
  public string AsnType = "";
  public string OrgType = "";
  public string Error = "";
  public DateTime Updated = DateTime.MinValue;
  public bool HasRisk { get { return !String.IsNullOrWhiteSpace(IpRisk); } }
 }

 class ProbeResult {
  public string Status = "未测试";
  public string Error = "";
  public string Target = "";
  public long Bytes;
  public double WriteMBps, ReadMBps;
  public double WriteMilliseconds, ReadMilliseconds;
  public bool Verified;
  public bool Completed;
  public DateTime Updated = DateTime.MinValue;
   public string Summary {
    get {
     if(Status == "未测试") return String.IsNullOrWhiteSpace(Error) ? "未启用 · 默认不创建测试文件" : "未完成 · " + Error;
    var list = new List<string> { Status };
    if(Completed) list.Add("写 " + WriteMBps.ToString("0.0") + " MB/s");
    if(Completed) list.Add("读 " + ReadMBps.ToString("0.0") + " MB/s");
    if(Completed) list.Add(Verified ? "校验通过" : "校验失败");
    if(Updated != DateTime.MinValue) list.Add("更新 " + Updated.ToString("MM-dd HH:mm"));
    if(!String.IsNullOrWhiteSpace(Error)) list.Add(Error);
    return String.Join("  ·  ", list);
   }
  }
 }

 class Health {
  public int Index;
  public string Model = "未知型号", Media = "未知介质", Bus = "未知总线", Status = "未知";
  public string Firmware = "未知", Serial = "未知";
  public long? SizeBytes, BytesPerSector, Partitions, PowerOnHours, PowerCycleCount;
  public bool SmartFailed, SmartKnown;
  public double? Temperature, TemperatureMax, Wear, AvailableSpare, AvailableSpareThreshold;
  public long? MediaErrors, ReadErrors, WriteErrors, ReadErrorsCorrected, WriteErrorsCorrected;
  public long? ReadErrorsUncorrected, WriteErrorsUncorrected, MediaErrorsUncorrected, UnsafeShutdowns;
  public long? StartStopCycleCount, StartStopCycleCountMax, LoadUnloadCycleCount, LoadUnloadCycleCountMax;
  public long? ReadLatencyMax, WriteLatencyMax, FlushLatencyMax;
  public string ManufactureDate = "未知";
  public byte? CriticalWarning;
  public double? DataUnitsReadBytes, DataUnitsWrittenBytes, HostReadCommands, HostWriteCommands;
  public double? ControllerBusyMinutes, ErrorLogEntries;
  public string StandardSource = "未采集", ReliabilitySource = "未采集", SmartSource = "未采集";
  public string NativeSource = "未尝试", NativeNote = "";

  public string MediaLabel {
   get {
    var b = (Bus ?? "").ToLowerInvariant();
    var m = (Media ?? "").ToLowerInvariant();
    if(b.Contains("nvme") || b == "17") return "NVMe SSD";
    if(m.Contains("ssd") || m == "4") return "SATA SSD";
    if(m.Contains("hdd") || m.Contains("hard disk") || m == "3") return "HDD";
    return "未识别介质";
   }
  }
  public bool HasDirectData {
   get { return Status != "未知" || SmartFailed || Temperature.HasValue || Wear.HasValue || MediaErrors.HasValue || PowerOnHours.HasValue || AvailableSpare.HasValue || CriticalWarning.HasValue; }
  }
  public string Severity {
   get {
    var life = LifetimeSeverity;
    var thermal = ThermalRisk;
    if(life == "严重" || thermal == "严重") return "严重";
    if(life == "关注" || thermal == "关注") return "关注";
    if(life == "正常" || thermal == "正常") return "正常";
    return "未知";
   }
  }
  public Brush SeverityBrush { get { return Severity == "严重" ? Red : Severity == "关注" ? Amber : Severity == "正常" ? Teal : Muted; } }
  public string SerialMasked {
   get {
    if(String.IsNullOrWhiteSpace(Serial) || Serial == "未知") return "未知";
    var s = Serial.Trim();
    return s.Length <= 4 ? "••••" : "••••" + s.Substring(Math.Max(0, s.Length - 4));
   }
  }
  public string Summary() {
   var list = new List<string>();
   list.Add(MediaLabel);
   if(Status != "未知") list.Add("状态 " + Status);
   if(SmartFailed) list.Add("SMART 预测失败");
   if(Temperature.HasValue) list.Add("温度 " + Temperature.Value.ToString("0") + "°C" + (TemperatureMax.HasValue ? " / 上限 " + TemperatureMax.Value.ToString("0") + "°C" : ""));
   if(Wear.HasValue) list.Add("磨损 " + Wear.Value.ToString("0") + "%");
   if(CriticalWarning.HasValue && CriticalWarning.Value != 0) list.Add("NVMe 警告 0x" + CriticalWarning.Value.ToString("X2"));
   if((MediaErrors ?? 0) + (ReadErrors ?? 0) + (WriteErrors ?? 0) + (ReadErrorsUncorrected ?? 0) + (WriteErrorsUncorrected ?? 0) > 0) list.Add("存在错误计数");
   return String.Join(" · ", list);
  }
  public string SourceSummary() {
   var list = new List<string>();
   if(!String.IsNullOrEmpty(StandardSource)) list.Add("基础 " + StandardSource);
   if(!String.IsNullOrEmpty(ReliabilitySource)) list.Add("Windows 健康 " + ReliabilitySource);
   if(!String.IsNullOrEmpty(SmartSource)) list.Add("SMART " + SmartSource);
   if(!String.IsNullOrEmpty(NativeSource) && NativeSource != "未尝试") list.Add(NativeSource);
   return String.Join(" · ", list);
  }
  // 寿命证据只看设备自报的健康/退化字段；瞬时温度和性能不会偷偷混入这里。
  public bool HasLifetimeData {
   get { return Status != "未知" || SmartFailed || Wear.HasValue || AvailableSpare.HasValue || MediaErrors.HasValue || ReadErrors.HasValue || WriteErrors.HasValue || ReadErrorsUncorrected.HasValue || WriteErrorsUncorrected.HasValue || MediaErrorsUncorrected.HasValue || CriticalWarning.HasValue; }
  }
  public string LifetimeSeverity {
   get {
    var s = (Status ?? "").ToLowerInvariant();
    byte bits = (byte)(CriticalWarning ?? 0);
    bool critical = SmartFailed || s.Contains("unhealthy") || s.Contains("不健康") || s.Contains("critical") || s.Contains("严重") || s.Contains("failed")
      || (ReadErrorsUncorrected ?? 0) > 0 || (WriteErrorsUncorrected ?? 0) > 0 || (MediaErrorsUncorrected ?? 0) > 0
      || (AvailableSpare.HasValue && AvailableSpare.Value <= 0) || (Wear.HasValue && Wear.Value >= 100)
      // NVMe bit 1 是温度阈值，留给 ThermalRisk；其余关键位代表可靠性 / 只读 / 备份风险。
      || (bits & 0x1C) != 0;
    if(critical) return "严重";
    bool warning = s.Contains("warning") || s.Contains("警告") || s.Contains("degraded") || s.Contains("关注")
      || (MediaErrors ?? 0) > 0 || (ReadErrors ?? 0) > 0 || (WriteErrors ?? 0) > 0
      || (Wear.HasValue && Wear.Value >= 90)
      || (AvailableSpare.HasValue && AvailableSpareThreshold.HasValue && AvailableSpare.Value < AvailableSpareThreshold.Value)
      || (bits & 0x01) != 0;
    if(warning) return "关注";
    return HasLifetimeData ? "正常" : "未知";
   }
  }
  public string ThermalRisk {
   get {
    byte bits = (byte)(CriticalWarning ?? 0);
    if((bits & 0x02) != 0) return "严重";
    if(Temperature.HasValue && TemperatureMax.HasValue) {
     if(Temperature.Value > TemperatureMax.Value) return "严重";
     if(Temperature.Value >= TemperatureMax.Value * .9) return "关注";
     return "正常";
    }
    return "未知";
   }
  }
  }

 class MetricSample {
  public DateTime Time;
  public double Read, Write, Iops, Queue, Active;
 }

 class Drive : IDisposable {
  public string Name;
  public PerformanceCounter R, W, IopsCounter, QueueCounter, ActiveCounter;
  public double Read, Write, Iops, Queue, Active;
  public Queue<double> History = new Queue<double>();
  public Queue<MetricSample> Series = new Queue<MetricSample>();
  readonly object SeriesGate = new object();
  public double Sum;
  public int Streak, Index;
  public int RequiredStreak = 5;
  public int LastIntervalSeconds;
  public double LastZ = Double.NaN, BaselineMedian = Double.NaN, BaselineMad = Double.NaN, BaselineP95 = Double.NaN;
  public bool LastCandidate;
  public string State = "正在学习负载";
  public DateTime Last = DateTime.MinValue, Since = DateTime.MinValue;
  public bool CounterFault;

  static PerformanceCounter MakeCounter(string category, string counter, string instance) {
   try {
    var c = new PerformanceCounter(category, counter, instance, true);
    c.NextValue();
    return c;
   } catch { return null; }
  }
  static double ReadCounter(PerformanceCounter c) {
   try { return c == null ? 0 : Math.Max(0, c.NextValue()); } catch { return 0; }
  }
  public Drive(string n) : this(n, true) { }
  public Drive(string n, bool counters) {
   Name = n;
   int parsed;
   Index = Int32.TryParse(new String(n.Trim().TakeWhile(Char.IsDigit).ToArray()), out parsed) ? parsed : -1;
   if(!counters) return;
   R = MakeCounter("PhysicalDisk", "Disk Read Bytes/sec", n);
   W = MakeCounter("PhysicalDisk", "Disk Write Bytes/sec", n);
   IopsCounter = MakeCounter("PhysicalDisk", "Disk Transfers/sec", n);
   QueueCounter = MakeCounter("PhysicalDisk", "Current Disk Queue Length", n);
   ActiveCounter = MakeCounter("PhysicalDisk", "% Disk Time", n);
   CounterFault = R == null && W == null;
  }
   static double Median(double[] sorted) {
    if(sorted == null || sorted.Length == 0) return Double.NaN;
    int mid = sorted.Length / 2;
    return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
   }
   static double Percentile(double[] sorted, double probability) {
    if(sorted == null || sorted.Length == 0) return Double.NaN;
    double position = Math.Max(0, Math.Min(1, probability)) * (sorted.Length - 1);
    int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
    if(lower == upper) return sorted[lower];
    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
   }
   public void Sample(int sampleIntervalSeconds) {
    int interval = Math.Max(1, Math.Min(10, sampleIntervalSeconds));
    // 保持“约 5 秒连续证据”的物理时间含义；较慢采样时至少需要一个候选点。
    RequiredStreak = Math.Max(1, (int)Math.Ceiling(5.0 / interval));
    if(LastIntervalSeconds != 0 && LastIntervalSeconds != interval) { Sum = 0; Streak = 0; Since = DateTime.MinValue; }
    LastIntervalSeconds = interval;
    Read = ReadCounter(R);
   Write = ReadCounter(W);
   Iops = ReadCounter(IopsCounter);
   Queue = ReadCounter(QueueCounter);
   Active = Math.Min(100, ReadCounter(ActiveCounter));
   lock(SeriesGate) {
    Series.Enqueue(new MetricSample { Time = DateTime.UtcNow, Read = Read, Write = Write, Iops = Iops, Queue = Queue, Active = Active });
    while(Series.Count > 240) Series.Dequeue();
   }
   LastZ = Double.NaN; BaselineMedian = Double.NaN; BaselineMad = Double.NaN; BaselineP95 = Double.NaN; LastCandidate = false;
   if(CounterFault) { State = "性能计数器不可用"; return; }
   double y = Math.Log(1 + Write / 1000000.0);
   var now = DateTime.UtcNow;
    // 允许一次计时抖动，但长间隔必须清除连续证据；基线样本仍保留，不偷偷改写“正常”。
    if((now - Last).TotalSeconds > Math.Max(5.0, interval * 3.0)) { Sum = 0; Streak = 0; Since = DateTime.MinValue; }
   Last = now;
   if(Write <= 0 || Double.IsNaN(Write) || Double.IsInfinity(Write)) {
    Sum = 0; Streak = 0; Since = DateTime.MinValue; State = "写入空闲 / 无有效样本"; return;
   }
   bool candidate = false;
   if(History.Count >= 120) {
    var sorted = History.OrderBy(x => x).ToArray();
     double median = Median(sorted);
     var deviations = sorted.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToArray();
     double mad = Median(deviations);
     BaselineMedian = median; BaselineMad = mad; BaselineP95 = Percentile(sorted, .95);
    if(mad <= 1e-6) {
     State = "写入基线波动不足 · 暂不评分";
     Sum = 0; Streak = 0; Since = DateTime.MinValue;
    } else {
     double z = (y - median) / (1.4826 * mad);
     LastZ = z;
      // 证据按“秒”归一化，改变采样频率不会把同一事件简单地稀释或放大。
      Sum = Math.Min(40, Math.Max(0, Sum + (Math.Max(-4, Math.Min(4, z)) - 0.5) * interval));
      candidate = z > 3.5 && y > BaselineP95;
     LastCandidate = candidate;
     Streak = candidate ? Streak + 1 : 0;
     if(candidate) { if(Since == DateTime.MinValue) Since = now; }
     else Since = DateTime.MinValue;
      State = Streak >= RequiredStreak && Since != DateTime.MinValue && (now - Since).TotalSeconds >= 5 && Sum >= 8 ? "写入高于近期基线" : "近期写入无显著上移";
    }
   } else State = "学习中 · " + History.Count + " / 120";
   if(!candidate) History.Enqueue(y);
   if(History.Count > 1800) History.Dequeue();
  }
  public MetricSample[] SeriesSnapshot() { lock(SeriesGate) return Series.ToArray(); }
  public void Dispose() {
   foreach(var c in new [] { R, W, IopsCounter, QueueCounter, ActiveCounter }) if(c != null) c.Dispose();
  }
 }

 static int IntValue(object value, int fallback = -1) {
  try { return value == null ? fallback : Convert.ToInt32(value); } catch { return fallback; }
 }
 static long? LongValue(object value) {
  try { return value == null ? (long?)null : Convert.ToInt64(value); } catch { return null; }
 }
 static double? DoubleValue(object value) {
  try { if(value == null) return null; double d = Convert.ToDouble(value); return Double.IsNaN(d) || Double.IsInfinity(d) ? (double?)null : d; } catch { return null; }
 }
 static string StringValue(object value) { return value == null ? "未知" : Convert.ToString(value); }
 static object Property(ManagementObject o, string name) {
  try { var p = o.Properties[name]; return p == null ? null : p.Value; } catch { return null; }
 }
 static string MediaValue(object value) { var s = StringValue(value); return s == "3" ? "HDD" : s == "4" ? "SSD" : s; }
 static string BusValue(object value) { var s = StringValue(value); return s == "17" ? "NVMe" : s == "11" ? "SATA" : s; }
 static string HealthValue(object value) {
  var s = StringValue(value);
  if(s == "0") return "未知";
  if(s == "1") return "正常";
  if(s == "2") return "警告";
  if(s == "3") return "不健康";
  if(s.Equals("OK", StringComparison.OrdinalIgnoreCase)) return "正常";
  if(s.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) return "正常";
  if(s.Equals("Warning", StringComparison.OrdinalIgnoreCase)) return "警告";
  if(s.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase)) return "不健康";
  return s;
 }
 static int ExtractIndex(string value) {
  if(String.IsNullOrEmpty(value)) return -1;
  for(int i = 0; i < value.Length; i++) if(Char.IsDigit(value[i])) {
   int n = 0;
   while(i < value.Length && Char.IsDigit(value[i])) { n = n * 10 + (value[i] - '0'); i++; }
   return n;
  }
  return -1;
 }
 static Health GetHealth(Dictionary<int,Health> map, int index) {
  Health h;
  if(!map.TryGetValue(index, out h)) { h = new Health { Index = index }; map[index] = h; }
  return h;
 }
 static Dictionary<int,Health> QueryHealth() {
  var result = new Dictionary<int,Health>();
  bool standardSeen = false, reliabilitySeen = false, smartSeen = false;
  string standardError = "", reliabilityError = "", smartError = "";
  try {
   using(var q = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
   foreach(ManagementObject o in q.Get()) {
    int i = IntValue(Property(o, "Index"));
    if(i < 0) continue;
    var h = GetHealth(result, i);
    standardSeen = true;
    h.StandardSource = "Win32_DiskDrive";
    h.Model = StringValue(Property(o, "Model"));
    h.Media = StringValue(Property(o, "MediaType"));
    h.Bus = StringValue(Property(o, "InterfaceType"));
    h.Status = StringValue(Property(o, "Status"));
    h.Serial = StringValue(Property(o, "SerialNumber"));
    h.Firmware = StringValue(Property(o, "FirmwareRevision"));
    h.SizeBytes = LongValue(Property(o, "Size"));
    h.BytesPerSector = LongValue(Property(o, "BytesPerSector"));
    h.Partitions = LongValue(Property(o, "Partitions"));
   }
  } catch(Exception ex) { standardError = ex.GetType().Name; }
  try {
   using(var q = new ManagementObjectSearcher(@"root\microsoft\windows\storage", "SELECT * FROM MSFT_PhysicalDisk"))
   foreach(ManagementObject o in q.Get()) {
    int i = IntValue(Property(o, "DeviceId"));
    if(i < 0) i = ExtractIndex(StringValue(Property(o, "DeviceId")));
    if(i < 0) continue;
    var h = GetHealth(result, i);
    h.StandardSource = h.StandardSource == "未采集" ? "MSFT_PhysicalDisk" : h.StandardSource + " + MSFT_PhysicalDisk";
    var friendly = StringValue(Property(o, "FriendlyName"));
    if(friendly != "未知") h.Model = friendly;
    h.Media = MediaValue(Property(o, "MediaType"));
    h.Bus = BusValue(Property(o, "BusType"));
    h.Status = HealthValue(Property(o, "HealthStatus"));
    var size = LongValue(Property(o, "Size"));
    if(size.HasValue) h.SizeBytes = size;
   }
  } catch(Exception ex) { if(String.IsNullOrEmpty(standardError)) standardError = ex.GetType().Name; }
  try {
   using(var q = new ManagementObjectSearcher(@"root\microsoft\windows\storage", "SELECT * FROM MSFT_StorageReliabilityCounter"))
   foreach(ManagementObject o in q.Get()) {
    int i = IntValue(Property(o, "DeviceId"));
    if(i < 0) i = ExtractIndex(StringValue(Property(o, "DeviceId")));
    if(i < 0) continue;
    var h = GetHealth(result, i);
    reliabilitySeen = true;
    h.ReliabilitySource = "MSFT_StorageReliabilityCounter";
    h.Temperature = DoubleValue(Property(o, "Temperature"));
    h.TemperatureMax = DoubleValue(Property(o, "TemperatureMax"));
    h.Wear = DoubleValue(Property(o, "Wear"));
    h.PowerOnHours = LongValue(Property(o, "PowerOnHours"));
    h.PowerCycleCount = LongValue(Property(o, "PowerCycleCount"));
    h.MediaErrors = LongValue(Property(o, "MediaErrors"));
    h.ReadErrors = LongValue(Property(o, "ReadErrorsTotal"));
    h.WriteErrors = LongValue(Property(o, "WriteErrorsTotal"));
    h.ReadErrorsCorrected = LongValue(Property(o, "ReadErrorsCorrected"));
    h.WriteErrorsCorrected = LongValue(Property(o, "WriteErrorsCorrected"));
    h.ReadErrorsUncorrected = LongValue(Property(o, "ReadErrorsUncorrected"));
    h.WriteErrorsUncorrected = LongValue(Property(o, "WriteErrorsUncorrected"));
    h.MediaErrorsUncorrected = LongValue(Property(o, "MediaErrorsUncorrected"));
    h.UnsafeShutdowns = LongValue(Property(o, "UnsafeShutdowns"));
    h.AvailableSpare = DoubleValue(Property(o, "AvailableSpare"));
    h.AvailableSpareThreshold = DoubleValue(Property(o, "AvailableSpareThreshold"));
    h.StartStopCycleCount = LongValue(Property(o, "StartStopCycleCount"));
    h.StartStopCycleCountMax = LongValue(Property(o, "StartStopCycleCountMax"));
    h.LoadUnloadCycleCount = LongValue(Property(o, "LoadUnloadCycleCount"));
    h.LoadUnloadCycleCountMax = LongValue(Property(o, "LoadUnloadCycleCountMax"));
    h.ReadLatencyMax = LongValue(Property(o, "ReadLatencyMax"));
    h.WriteLatencyMax = LongValue(Property(o, "WriteLatencyMax"));
    h.FlushLatencyMax = LongValue(Property(o, "FlushLatencyMax"));
    h.ManufactureDate = StringValue(Property(o, "ManufactureDate"));
   }
  } catch(Exception ex) { reliabilityError = ex.GetType().Name; }
  try {
   using(var q = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM MSStorageDriver_FailurePredictStatus"))
   foreach(ManagementObject o in q.Get()) {
    smartSeen = true;
    int objectIndex = ExtractIndex(StringValue(Property(o, "InstanceName")));
    if(objectIndex >= 0) { GetHealth(result, objectIndex).SmartSource = "MSStorageDriver_FailurePredictStatus"; GetHealth(result, objectIndex).SmartKnown = Property(o, "PredictFailure") != null; }
    if(Convert.ToBoolean(Property(o, "PredictFailure") ?? false)) {
     int i = objectIndex;
     if(i >= 0) GetHealth(result, i).SmartFailed = true;
    }
   }
  } catch(Exception ex) { smartError = ex.GetType().Name; }

  foreach(var h in result.Values) {
   if(h.StandardSource == "未采集") h.StandardSource = standardSeen ? "未匹配到基础记录" : (String.IsNullOrEmpty(standardError) ? "未返回" : "接口不可用");
   if(h.ReliabilitySource == "未采集") h.ReliabilitySource = reliabilitySeen ? "未匹配到可靠性记录" : (String.IsNullOrEmpty(reliabilityError) ? "未提供" : "接口不可用");
   if(h.SmartSource == "未采集") h.SmartSource = smartSeen ? "未发现预测记录" : (String.IsNullOrEmpty(smartError) ? "未提供" : "不支持");

   NativeStorageHealth native;
   StorageProtocolReader.TryReadNvme(h.Index, out native);
   h.NativeSource = native.Source;
   h.NativeNote = native.Note;
   if(native.IdentifyOk || native.HealthOk) {
    if(!String.IsNullOrWhiteSpace(native.Model)) h.Model = native.Model;
    if(!String.IsNullOrWhiteSpace(native.Serial)) h.Serial = native.Serial;
    if(!String.IsNullOrWhiteSpace(native.Firmware)) h.Firmware = native.Firmware;
    h.Media = "SSD";
    h.Bus = "NVMe";
    if(native.Temperature.HasValue) h.Temperature = native.Temperature;
    if(native.AvailableSpare.HasValue) h.AvailableSpare = native.AvailableSpare;
    if(native.AvailableSpareThreshold.HasValue) h.AvailableSpareThreshold = native.AvailableSpareThreshold;
    if(native.PercentageUsed.HasValue) h.Wear = native.PercentageUsed;
    if(native.CriticalWarning.HasValue) h.CriticalWarning = native.CriticalWarning;
    if(native.DataUnitsReadBytes.HasValue) h.DataUnitsReadBytes = native.DataUnitsReadBytes;
    if(native.DataUnitsWrittenBytes.HasValue) h.DataUnitsWrittenBytes = native.DataUnitsWrittenBytes;
    if(native.HostReadCommands.HasValue) h.HostReadCommands = native.HostReadCommands;
    if(native.HostWriteCommands.HasValue) h.HostWriteCommands = native.HostWriteCommands;
    if(native.ControllerBusyMinutes.HasValue) h.ControllerBusyMinutes = native.ControllerBusyMinutes;
    if(native.PowerCycleCount.HasValue) h.PowerCycleCount = native.PowerCycleCount;
    if(native.PowerOnHours.HasValue) h.PowerOnHours = native.PowerOnHours;
    if(native.UnsafeShutdowns.HasValue) h.UnsafeShutdowns = native.UnsafeShutdowns;
    if(native.MediaErrors.HasValue) h.MediaErrors = native.MediaErrors;
    if(native.ErrorLogEntries.HasValue) h.ErrorLogEntries = native.ErrorLogEntries;
   }
  }
 return result;
 }

 static string DownloadPing0(string url) {
  var request = (HttpWebRequest)WebRequest.Create(url);
  request.Method = "GET";
  request.Timeout = 12000;
  request.ReadWriteTimeout = 12000;
  request.UserAgent = "DiskGuard/0.6";
  using(var response = (HttpWebResponse)request.GetResponse())
  using(var stream = response.GetResponseStream())
  using(var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
 }

 static bool TryNormalizeIp(string value, out string normalized) {
  normalized = "";
  IPAddress address;
  if(!IPAddress.TryParse((value ?? "").Trim(), out address)) return false;
  // Ping0 的示例接口使用 IPv4；IPv6 仍允许查询，但只接受解析成功的地址，避免把任意文本拼入 URL。
  normalized = address.ToString();
  return true;
 }

 static string JsonUnescape(string value) {
  if(String.IsNullOrEmpty(value)) return "";
  var sb = new StringBuilder();
  for(int i = 0; i < value.Length; i++) {
   char c = value[i];
   if(c != '\\' || i + 1 >= value.Length) { sb.Append(c); continue; }
   char next = value[++i];
   switch(next) {
    case '"': sb.Append('"'); break;
    case '\\': sb.Append('\\'); break;
    case '/': sb.Append('/'); break;
    case 'b': sb.Append('\b'); break;
    case 'f': sb.Append('\f'); break;
    case 'n': sb.Append('\n'); break;
    case 'r': sb.Append('\r'); break;
    case 't': sb.Append('\t'); break;
    case 'u':
     if(i + 4 < value.Length) {
      int code;
      if(Int32.TryParse(value.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) { sb.Append((char)code); i += 4; break; }
     }
     sb.Append('u'); break;
    default: sb.Append(next); break;
   }
  }
  return sb.ToString();
 }

 static string JsonField(string json, string key) {
  if(String.IsNullOrEmpty(json) || String.IsNullOrEmpty(key)) return "";
  string pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*(?<value>\"(?:\\\\.|[^\"\\\\])*\"|null|true|false|-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)";
  var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
  if(!match.Success) return "";
  string token = match.Groups["value"].Value.Trim();
  if(token == "null") return "";
  if(token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"') return JsonUnescape(token.Substring(1, token.Length - 2));
  return token;
 }

 static bool? ParseFlag(string value) {
  if(String.IsNullOrWhiteSpace(value)) return null;
  bool b;
  if(Boolean.TryParse(value, out b)) return b;
  double n;
  if(Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return Math.Abs(n) > 0.5;
  return null;
 }

 static string FlagText(string value, string yes, string no) {
  var flag = ParseFlag(value);
  return flag.HasValue ? (flag.Value ? yes : no) : (String.IsNullOrWhiteSpace(value) ? "未采集" : value);
 }

 static string MaskIp(string value) {
  if(String.IsNullOrWhiteSpace(value)) return "未采集";
  IPAddress address;
  if(!IPAddress.TryParse(value, out address)) return value;
  if(address.AddressFamily == AddressFamily.InterNetwork) {
   var parts = address.ToString().Split('.');
   if(parts.Length == 4) { parts[3] = "•••"; return String.Join(".", parts); }
  }
  var text = address.ToString();
  int colon = text.LastIndexOf(':');
  return colon > 0 ? text.Substring(0, colon + 1) + "•••" : "••••";
 }

 static string GeoLine(string value) {
  var s = (value ?? "").Trim();
  if(s.StartsWith("IP:", StringComparison.OrdinalIgnoreCase)) return s.Substring(3).Trim();
  if(s.StartsWith("IP：", StringComparison.OrdinalIgnoreCase)) return s.Substring(3).Trim();
  return s;
 }

 static Ping0Snapshot QueryPing0(string targetIp, string apiKey) {
  var snap = new Ping0Snapshot { Enabled = true, Status = "正在查询" };
  try {
   // .NET Framework 的默认协议可能仍是 TLS 1.0；Ping0 使用 HTTPS，这里只在用户开启查询后设置 TLS 1.2。
   ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
   string ip = "";
   string requested = (targetIp ?? "").Trim();
   if(!String.IsNullOrEmpty(requested)) {
    if(!TryNormalizeIp(requested, out ip)) {
     snap.Status = "节点地址无效"; snap.Error = "请输入有效的 IPv4/IPv6 地址"; snap.Updated = DateTime.Now; return snap;
    }
    snap.Ip = ip;
   } else {
    var geoText = DownloadPing0("https://ping0.cc/geo");
    var lines = geoText.Split(new [] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => GeoLine(x)).ToArray();
    if(lines.Length > 0) snap.Ip = lines[0];
    if(lines.Length > 1) snap.Location = lines[1];
    if(lines.Length > 2) snap.Asn = lines[2];
    if(lines.Length > 3) snap.Org = lines[3];
    ip = snap.Ip;
   }
   if(String.IsNullOrWhiteSpace(apiKey)) {
    snap.Status = String.IsNullOrWhiteSpace(ip) ? "未取得公网 IP" : "基础节点信息已取得";
    snap.Error = String.IsNullOrWhiteSpace(ip) ? "Ping0 未返回 IP" : "详细风险字段需要 API Key";
    snap.Updated = DateTime.Now;
    return snap;
   }
   if(String.IsNullOrWhiteSpace(ip)) {
    snap.Status = "无法查询风险"; snap.Error = "没有可查询的 IP 地址"; snap.Updated = DateTime.Now; return snap;
   }
   string normalizedIp;
   if(!TryNormalizeIp(ip, out normalizedIp)) {
    snap.Status = "公网 IP 无效"; snap.Error = "Ping0 返回的 IP 无法解析"; snap.Updated = DateTime.Now; return snap;
   }
   string url = "https://ping0.cc/apiloc/apikey(" + Uri.EscapeDataString(apiKey.Trim()) + ")/ip(" + Uri.EscapeDataString(normalizedIp) + ")";
   string json = DownloadPing0(url);
   var fromApi = JsonField(json, "ip");
   if(!String.IsNullOrWhiteSpace(fromApi)) snap.Ip = fromApi;
   snap.Location = JsonField(json, "location");
   snap.Asn = JsonField(json, "asn");
   snap.AsnName = JsonField(json, "asnname");
   snap.Org = JsonField(json, "org");
   snap.IpRisk = JsonField(json, "iprisk");
   snap.IsIdc = JsonField(json, "isidc");
   snap.IsNative = JsonField(json, "isnative");
   snap.AsnType = JsonField(json, "asntype");
   snap.OrgType = JsonField(json, "orgtype");
   snap.Status = snap.HasRisk ? "风险字段已取得" : "节点信息已取得";
   if(!snap.HasRisk) snap.Error = "接口未返回 iprisk";
  } catch(WebException ex) {
   snap.Status = "请求失败";
   snap.Error = ex.Status == WebExceptionStatus.Timeout ? "网络请求超时" : "网络不可用或接口拒绝请求";
  } catch(Exception ex) {
   snap.Status = "查询失败";
   snap.Error = ex.GetType().Name;
  }
  snap.Updated = DateTime.Now;
  return snap;
 }

 static ulong FnvUpdate(ulong hash, byte[] buffer, int offset, int count) {
  const ulong prime = 1099511628211UL;
  for(int i = offset; i < offset + count; i++) { hash ^= buffer[i]; hash *= prime; }
  return hash;
 }

 static byte[] ProbePattern() {
  var buffer = new byte[1024 * 1024];
  uint state = 0x4D475031; // 固定种子：每次测试写入完全相同的内容，不依赖随机数。
  for(int i = 0; i < buffer.Length; i++) { state = state * 1664525U + 1013904223U; buffer[i] = (byte)(state >> 24); }
  return buffer;
 }

 static ProbeResult ProbeFinish(ProbeResult result, string status, string error = "") {
  result.Status = status; result.Error = error ?? ""; result.Updated = DateTime.Now; return result;
 }

 static bool OwnProbeDirectory(string probeDir, string marker) {
  try { return File.Exists(marker) && File.ReadAllText(marker, Encoding.UTF8).Trim() == "Disk Guard probe v1"; } catch { return false; }
 }

  static bool CleanupProbeArtifact(string directory, out string message) {
   message = "";
   try {
    string requested = (directory ?? "").Trim();
    if(String.IsNullOrWhiteSpace(requested)) { message = "请先设置目标文件夹"; return false; }
    string full = Path.GetFullPath(requested);
    if(!Directory.Exists(full)) { message = "目标文件夹不存在"; return false; }
   string probeDir = Path.Combine(full, ".diskguard-probe");
   string marker = Path.Combine(probeDir, "owner.txt");
   if(!Directory.Exists(probeDir)) { message = "没有发现残留探针"; return true; }
   if(!OwnProbeDirectory(probeDir, marker)) { message = "发现同名目录但无法确认归属，未删除"; return false; }
   var file = Path.Combine(probeDir, "fixed-sequential.bin");
   if((File.GetAttributes(probeDir) & FileAttributes.ReparsePoint) != 0) { message = "探针目录是联接，未删除"; return false; }
   if(File.Exists(file)) File.Delete(file);
   if(File.Exists(marker)) File.Delete(marker);
   Directory.Delete(probeDir, false);
   message = "残留探针已清理"; return true;
  } catch(Exception ex) { message = "清理失败：" + ex.GetType().Name; return false; }
 }

  static ProbeResult RunProbe(string directory, int sizeMb, int maxSeconds) {
   var result = new ProbeResult { Target = directory ?? "", Bytes = Math.Max(32, Math.Min(512, sizeMb)) * 1024L * 1024L };
   string probeDir = "", marker = "", file = "";
   bool createdProbeDir = false;
   try {
    string requested = (directory ?? "").Trim();
    if(String.IsNullOrWhiteSpace(requested)) return ProbeFinish(result, "未测试", "请先设置目标文件夹");
    string full = Path.GetFullPath(requested);
   string root = Path.GetPathRoot(full);
   if(String.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return ProbeFinish(result, "未测试", "目标磁盘不存在");
   var drive = new DriveInfo(root);
   if(!drive.IsReady) return ProbeFinish(result, "未测试", "目标磁盘未就绪");
   if(drive.AvailableFreeSpace < result.Bytes * 2) return ProbeFinish(result, "未测试", "可用空间不足（至少需要测试文件大小的 2 倍）");
   Directory.CreateDirectory(full);
   probeDir = Path.Combine(full, ".diskguard-probe"); marker = Path.Combine(probeDir, "owner.txt"); file = Path.Combine(probeDir, "fixed-sequential.bin");
   if(Directory.Exists(probeDir)) {
    if((File.GetAttributes(probeDir) & FileAttributes.ReparsePoint) != 0) return ProbeFinish(result,"未测试","探针目录不能是目录联接");
    if(!OwnProbeDirectory(probeDir, marker)) return ProbeFinish(result, "未测试", "探针目录已存在但无法确认归属，为避免误删已停止");
    createdProbeDir = true; // 已通过归属标记校验，可在 finally 中清理上次中断留下的目录。
   } else {
    Directory.CreateDirectory(probeDir); createdProbeDir = true;
    File.WriteAllText(marker, "Disk Guard probe v1", Encoding.UTF8);
   }
   if(File.Exists(file)) File.Delete(file);
   var buffer = ProbePattern();
   ulong expected = 1469598103934665603UL;
   long written = 0;
   var writeWatch = Stopwatch.StartNew();
   using(var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan | FileOptions.WriteThrough)) {
    while(written < result.Bytes && writeWatch.Elapsed.TotalSeconds < Math.Max(10, Math.Min(120, maxSeconds))) {
     int count = (int)Math.Min(buffer.Length, result.Bytes - written);
     stream.Write(buffer, 0, count); expected = FnvUpdate(expected, buffer, 0, count); written += count;
    }
    stream.Flush(true);
   }
   writeWatch.Stop();
   if(written < result.Bytes) return ProbeFinish(result, "达到时间上限", "顺序写入未完成，测试文件已清理");
   result.WriteMilliseconds = Math.Max(0.1, writeWatch.Elapsed.TotalMilliseconds);
   result.WriteMBps = result.Bytes / 1000000.0 / Math.Max(.001, writeWatch.Elapsed.TotalSeconds);
   ulong actual = 1469598103934665603UL;
   long read = 0;
   var readWatch = Stopwatch.StartNew();
   using(var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan)) {
    while(read < result.Bytes && writeWatch.Elapsed.TotalSeconds + readWatch.Elapsed.TotalSeconds < Math.Max(10, Math.Min(120, maxSeconds))) {
     int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, result.Bytes - read));
     if(count <= 0) break;
     actual = FnvUpdate(actual, buffer, 0, count); read += count;
    }
   }
   readWatch.Stop();
   if(read < result.Bytes && writeWatch.Elapsed.TotalSeconds + readWatch.Elapsed.TotalSeconds >= Math.Max(10, Math.Min(120, maxSeconds))) return ProbeFinish(result,"达到时间上限","读回未完成");
   result.ReadMilliseconds = Math.Max(0.1, readWatch.Elapsed.TotalMilliseconds);
   result.ReadMBps = read / 1000000.0 / Math.Max(.001, readWatch.Elapsed.TotalSeconds);
   result.Completed = written == result.Bytes && read == result.Bytes;
   result.Verified = result.Completed && actual == expected;
   return ProbeFinish(result, result.Verified ? "测试完成" : "校验失败", result.Verified ? "" : "读取字节数或校验和不一致");
  } catch(UnauthorizedAccessException) { return ProbeFinish(result, "未测试", "没有目标文件夹的写入权限"); }
  catch(IOException ex) { return ProbeFinish(result, "未测试", "文件系统拒绝操作：" + ex.GetType().Name); }
  catch(Exception ex) { return ProbeFinish(result, "未测试", ex.GetType().Name); }
  finally {
   try { if(createdProbeDir && OwnProbeDirectory(probeDir, marker) && !String.IsNullOrWhiteSpace(file) && File.Exists(file)) File.Delete(file); } catch { }
   try { if(createdProbeDir && !String.IsNullOrWhiteSpace(marker) && File.Exists(marker) && OwnProbeDirectory(probeDir, marker)) File.Delete(marker); } catch { }
   try { if(createdProbeDir && !String.IsNullOrWhiteSpace(probeDir) && Directory.Exists(probeDir)) Directory.Delete(probeDir, false); } catch { }
  }
 }

 static TextBlock Text(string value, double size, Brush color) {
  return new TextBlock { Text = value, FontSize = size, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,7) };
 }
 UIElement HelpBadge(string tip) {
  var mark = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = InputFill, BorderBrush = CardStroke, BorderThickness = new Thickness(1), Cursor = Cursors.Help, ToolTip = new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 13, Foreground = Ink }, Background = InputFill, Foreground = Ink, BorderBrush = CardStroke, Padding = new Thickness(12), MaxWidth = 420 } };
  ToolTipService.SetInitialShowDelay(mark, 250);
  ToolTipService.SetShowDuration(mark, 30000);
  mark.Child = new TextBlock { Text = "?", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
  return mark;
 }
 DockPanel HeadingWithHelp(string title, string tip, double size = 15) {
  var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0,0,0,6), VerticalAlignment = VerticalAlignment.Center };
  var heading = Text(title, Math.Max(17, size), Ink); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0,0,8,0); heading.VerticalAlignment = VerticalAlignment.Center;
  row.Children.Add(heading);
  var help = HelpBadge(tip); DockPanel.SetDock(help, Dock.Right); row.Children.Add(help);
  return row;
 }
 TextBlock InlineLabel(string value, double width) {
  return new TextBlock { Text = value, Width = width, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,8,0) };
 }
 static Border Card(UIElement child, double radius = 22, Thickness? padding = null) {
  var border = new Border {
   Background = CardFill,
   CornerRadius = new CornerRadius(radius),
   Padding = padding ?? new Thickness(20),
   Margin = new Thickness(0,0,14,16),
   BorderBrush = CardStroke,
   BorderThickness = new Thickness(1),
   Effect = radius >= 16 ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 1, Opacity = DarkPalette ? .16 : .08, Color = Colors.Black } : null,
   Child = child
  };
  return border;
 }
 Button ActionButton(string label, Action action, HorizontalAlignment align = HorizontalAlignment.Left) {
  var b = new Button {
   Content = label,
   Padding = new Thickness(12,8,12,8),
   Margin = new Thickness(0,0,0,9),
   Background = SurfaceFill,
   Foreground = Ink,
   BorderThickness = new Thickness(0),
   HorizontalContentAlignment = align,
   Cursor = Cursors.Hand,
   FontSize = 13
  };
  var template = new ControlTemplate(typeof(Button));
  var border = new FrameworkElementFactory(typeof(Border));
  border.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
  border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
  var cp = new FrameworkElementFactory(typeof(ContentPresenter));
  cp.SetValue(FrameworkElement.MarginProperty, new Thickness(13,8,13,8));
  border.AppendChild(cp);
  template.VisualTree = border;
  b.Template = template;
  b.Click += (s,e) => action();
  return b;
 }
 Button SmallButton(string label, Action action) {
  var b = new Button {
   Content = label, FontSize = 12, Width = 28, Height = 26, Padding = new Thickness(0),
   Background = SurfaceFill, Foreground = Ink,
   BorderThickness = new Thickness(0), Cursor = Cursors.Hand
  };
  b.Click += (s,e) => action();
  return b;
 }
 TextBlock ValueText(string value, double size, Brush color) {
  return new TextBlock { Text = value, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = color, Margin = new Thickness(0,3,0,0) };
 }
 Border MetricCard(string label, string value, Brush color, out TextBlock valueText) {
  var box = new StackPanel();
  box.Children.Add(Text(label, 11, Muted));
  valueText = ValueText(value, 18, color);
  box.Children.Add(valueText);
  return Card(box, 18, new Thickness(15,13,15,12));
 }
 static string Rate(double value) { return (value / 1000000.0).ToString("0.0") + " MB/s"; }
 static string Count(long? value) { return value.HasValue ? value.Value.ToString("N0") : "未采集"; }
 static string Bytes(long? value) {
  if(!value.HasValue || value.Value <= 0) return "未知容量";
  double n = value.Value;
  string[] units = { "B", "KB", "MB", "GB", "TB" };
  int i = 0;
  while(n >= 1000 && i < units.Length - 1) { n /= 1000; i++; }
  return n.ToString(n >= 100 ? "0" : "0.0") + " " + units[i];
 }
 static string Bytes(double? value) {
  if(!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value) || value.Value < 0) return "未采集";
  double n = value.Value;
  string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
  int i = 0;
  while(n >= 1000 && i < units.Length - 1) { n /= 1000; i++; }
  return n.ToString(n >= 100 ? "0" : "0.0") + " " + units[i];
 }
 static string Number(double? value) {
  return value.HasValue && !Double.IsNaN(value.Value) && !Double.IsInfinity(value.Value) ? value.Value.ToString("N0") : "未采集";
 }
 static string HexByte(byte? value) {
  return value.HasValue ? "0x" + value.Value.ToString("X2") : "未采集";
 }
 static string OptionalNumber(double? value, string suffix = "") { return value.HasValue ? value.Value.ToString("0.##") + suffix : "未采集"; }
 static string MetricPair(string name, string value) { return name + "  " + value; }
 class UniformGridShim : System.Windows.Controls.Primitives.UniformGrid { public UniformGridShim() { Columns = 2; } }
 static Brush BadgeFill(Health h) {
  if(h == null) return new SolidColorBrush(Color.FromArgb(70,150,160,170));
  if(h.Severity == "严重") return new SolidColorBrush(Color.FromArgb(55,185,72,83));
  if(h.Severity == "关注") return new SolidColorBrush(Color.FromArgb(55,183,115,25));
  if(h.Severity == "正常") return new SolidColorBrush(Color.FromArgb(55,42,147,128));
  return new SolidColorBrush(Color.FromArgb(55,121,132,148));
 }

 static string ConfigPath() {
  try {
    var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskGuard");
    Directory.CreateDirectory(root);
    return System.IO.Path.Combine(root, "display-options.cfg");
  } catch { return null; }
 }
 void LoadOptions() {
  options = new DisplayOptions();
  try {
   var path = ConfigPath();
   if(String.IsNullOrEmpty(path) || !File.Exists(path)) return;
   foreach(var line in File.ReadAllLines(path)) {
    var p = line.Split(new [] { '=' }, 2);
    if(p.Length == 2) {
      if(p[0].Trim().Equals("theme", StringComparison.OrdinalIgnoreCase)) themeMode = ParseTheme(p[1].Trim());
      else if(p[0].Trim().Equals("font", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(p[1].Trim())) fontFamilyName = p[1].Trim();
     else if(p[0].Trim().Equals("ping0_enabled", StringComparison.OrdinalIgnoreCase)) ping0Enabled = p[1].Trim() == "1";
     else if(p[0].Trim().Equals("ping0_target", StringComparison.OrdinalIgnoreCase)) ping0TargetIp = p[1].Trim();
     else if(p[0].Trim().Equals("ping0_interval", StringComparison.OrdinalIgnoreCase)) {
      int minutes; if(Int32.TryParse(p[1].Trim(), out minutes)) ping0IntervalMinutes = Math.Max(15, Math.Min(240, minutes));
     }
     else if(p[0].Trim().Equals("performance_interval", StringComparison.OrdinalIgnoreCase)) {
      int seconds; if(Int32.TryParse(p[1].Trim(), out seconds)) performanceIntervalSeconds = Math.Max(1, Math.Min(10, seconds));
     }
     else if(p[0].Trim().Equals("health_interval", StringComparison.OrdinalIgnoreCase)) {
      int minutes; if(Int32.TryParse(p[1].Trim(), out minutes)) healthPollMinutes = Math.Max(1, Math.Min(60, minutes));
     }
     else if(p[0].Trim().Equals("probe_enabled", StringComparison.OrdinalIgnoreCase)) probeEnabled = p[1].Trim() == "1";
     else if(p[0].Trim().Equals("probe_path", StringComparison.OrdinalIgnoreCase)) probePath = p[1].Trim();
     else if(p[0].Trim().Equals("probe_size_mb", StringComparison.OrdinalIgnoreCase)) {
      int mb; if(Int32.TryParse(p[1].Trim(), out mb)) probeSizeMb = Math.Max(32, Math.Min(512, mb));
     }
     else if(p[0].Trim().Equals("probe_max_seconds", StringComparison.OrdinalIgnoreCase)) {
      int seconds; if(Int32.TryParse(p[1].Trim(), out seconds)) probeMaxSeconds = Math.Max(10, Math.Min(120, seconds));
     }
     else if(p[0].Trim().Equals("probe_interval", StringComparison.OrdinalIgnoreCase)) {
      int minutes; if(Int32.TryParse(p[1].Trim(), out minutes)) probeIntervalMinutes = Math.Max(0, Math.Min(10080, minutes));
     }
     else if(p[0].StartsWith("probe_disk_")) savedProbeTargets[p[0].Substring(11)] = p[1];
     else SetOption(p[0].Trim(), p[1].Trim() == "1", false);
    }
   }
  } catch { }
 }
 static ThemeMode ParseTheme(string value) {
  var s = (value ?? "").Trim().ToLowerInvariant();
  if(s == "dark" || s == "深色") return ThemeMode.Dark;
  if(s == "light" || s == "浅色") return ThemeMode.Light;
  return ThemeMode.System;
 }
 static string ThemeValue(ThemeMode mode) { return mode == ThemeMode.Dark ? "dark" : mode == ThemeMode.Light ? "light" : "system"; }
 void SaveOptions() {
  if(!settingsPersistence) return;
  try {
   var path = ConfigPath();
   if(String.IsNullOrEmpty(path)) return;
   var lines = new [] {
    "read=" + (options.Read ? "1" : "0"), "write=" + (options.Write ? "1" : "0"),
    "iops=" + (options.Iops ? "1" : "0"), "queue=" + (options.Queue ? "1" : "0"),
    "active=" + (options.Active ? "1" : "0"), "temperature=" + (options.Temperature ? "1" : "0"),
    "health=" + (options.Health ? "1" : "0"), "wear=" + (options.Wear ? "1" : "0"),
    "errors=" + (options.Errors ? "1" : "0"), "power_on_hours=" + (options.PowerOnHours ? "1" : "0"),
    "endurance=" + (options.Endurance ? "1" : "0"),
    "workload=" + (options.Workload ? "1" : "0"),
     "theme=" + ThemeValue(themeMode),
     "font=" + (fontFamilyName ?? "Microsoft YaHei UI"),
    "ping0_enabled=" + (ping0Enabled ? "1" : "0"),
    "ping0_target=" + (ping0TargetIp ?? ""),
    "ping0_interval=" + ping0IntervalMinutes.ToString(CultureInfo.InvariantCulture),
    "performance_interval=" + performanceIntervalSeconds.ToString(CultureInfo.InvariantCulture),
    "health_interval=" + healthPollMinutes.ToString(CultureInfo.InvariantCulture),
    "probe_enabled=" + (probeEnabled ? "1" : "0"),
    "probe_path=" + (probePath ?? ""),
    "probe_size_mb=" + probeSizeMb.ToString(CultureInfo.InvariantCulture),
    "probe_max_seconds=" + probeMaxSeconds.ToString(CultureInfo.InvariantCulture),
    "probe_interval=" + probeIntervalMinutes.ToString(CultureInfo.InvariantCulture)
   };
   foreach(var target in probeTargets) savedProbeTargets[target.Key] = (target.Enabled ? "1|" : "0|") + Convert.ToBase64String(Encoding.UTF8.GetBytes(target.Folder));
   File.WriteAllLines(path, lines.Concat(savedProbeTargets.Select(x => "probe_disk_" + x.Key + "=" + x.Value)), Encoding.UTF8);
  } catch { }
 }
 bool GetOption(string key) {
  switch((key ?? "").ToLowerInvariant()) {
   case "read": return options.Read;
   case "write": return options.Write;
   case "iops": return options.Iops;
   case "queue": return options.Queue;
   case "active": return options.Active;
   case "temperature": return options.Temperature;
   case "health": return options.Health;
   case "wear": return options.Wear;
   case "errors": return options.Errors;
   case "power_on_hours": return options.PowerOnHours;
   case "endurance": return options.Endurance;
   case "workload": return options.Workload;
   default: return true;
  }
 }
 void SetOption(string key, bool value, bool refresh) {
  switch((key ?? "").ToLowerInvariant()) {
   case "read": options.Read = value; break;
   case "write": options.Write = value; break;
   case "iops": options.Iops = value; break;
   case "queue": options.Queue = value; break;
   case "active": options.Active = value; break;
   case "temperature": options.Temperature = value; break;
   case "health": options.Health = value; break;
   case "wear": options.Wear = value; break;
   case "errors": options.Errors = value; break;
   case "power_on_hours": options.PowerOnHours = value; break;
   case "endurance": options.Endurance = value; break;
   case "workload": options.Workload = value; break;
  }
  if(refresh) { SaveOptions(); RenderCurrent(); if(floating != null && floating.IsVisible) floating.Refresh(); }
 }
 static bool SystemUsesDarkTheme() {
  try {
   using(var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
    return value != null && Convert.ToInt32(value) == 0;
   }
  } catch { return false; }
 }
 LinearGradientBrush MakeBackgroundGradient() {
  var gradient = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,1) };
  if(themeMode == ThemeMode.Dark || (themeMode == ThemeMode.System && SystemUsesDarkTheme())) {
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(20,25,37), 0));
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(25,35,49), .35));
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(27,44,54), .75));
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(53,42,55), 1));
  } else {
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(245,232,233), 0));
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(233,240,241), .35));
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(222,242,245), .75));
   gradient.GradientStops.Add(new GradientStop(Color.FromRgb(239,230,196), 1));
  }
  return gradient;
 }
 void ApplyThemePalette() {
  bool dark = themeMode == ThemeMode.Dark || (themeMode == ThemeMode.System && SystemUsesDarkTheme());
  DarkPalette = dark;
  Ink = new SolidColorBrush(dark ? Color.FromRgb(240,243,250) : Color.FromRgb(32,39,52));
  Muted = new SolidColorBrush(dark ? Color.FromRgb(168,181,201) : Color.FromRgb(121,132,148));
  Blue = new SolidColorBrush(dark ? Color.FromRgb(112,174,255) : Color.FromRgb(54,116,216));
  Teal = new SolidColorBrush(dark ? Color.FromRgb(91,214,180) : Color.FromRgb(42,147,128));
  Amber = new SolidColorBrush(dark ? Color.FromRgb(247,188,91) : Color.FromRgb(183,115,25));
  Red = new SolidColorBrush(dark ? Color.FromRgb(255,117,129) : Color.FromRgb(185,72,83));
  Violet = new SolidColorBrush(dark ? Color.FromRgb(190,168,255) : Color.FromRgb(112,92,190));
  CardFill = new SolidColorBrush(dark ? Color.FromArgb(235,36,44,59) : Color.FromArgb(226,255,255,255));
  CardStroke = new SolidColorBrush(dark ? Color.FromArgb(190,92,107,133) : Color.FromArgb(210,255,255,255));
  SurfaceFill = new SolidColorBrush(dark ? Color.FromArgb(210,40,49,65) : Color.FromArgb(180,255,255,255));
  SidebarFill = new SolidColorBrush(dark ? Color.FromArgb(205,29,36,50) : Color.FromArgb(195,248,248,251));
  InputFill = new SolidColorBrush(dark ? Color.FromArgb(235,43,53,71) : Color.FromArgb(220,255,255,255));
  InputBorder = new SolidColorBrush(dark ? Color.FromArgb(210,100,117,143) : Color.FromArgb(210,205,215,228));
  InputHighlight = new SolidColorBrush(dark ? Color.FromArgb(225,73,93,125) : Color.FromArgb(220,219,233,251));
  ChartFill = new SolidColorBrush(dark ? Color.FromArgb(140,21,28,41) : Color.FromArgb(90,245,248,252));
  TrackFill = new SolidColorBrush(dark ? Color.FromArgb(125,95,108,130) : Color.FromArgb(80,180,190,205));
  if(root != null) root.Background = MakeBackgroundGradient();
  if(sidebar != null) sidebar.Background = SidebarFill;
  if(floating != null) floating.RefreshTheme();
 }
 void RebuildShellForTheme() {
  var page = currentPage;
  BuildShell();
  if(page != PageKind.Overview) BuildPage(page);
 }
 void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
  if(themeMode != ThemeMode.System) return;
  try { Dispatcher.BeginInvoke(new Action(() => { ApplyThemePalette(); RebuildShellForTheme(); })); } catch { }
 }
 void ApplySamplingIntervals() {
  if(timer != null) timer.Interval = TimeSpan.FromSeconds(Math.Max(1, Math.Min(10, performanceIntervalSeconds)));
 }
 CheckBox OptionCheck(StackPanel panel, string label, string key) {
  var check = new CheckBox { Content = label, IsChecked = GetOption(key), FontSize = 13, Foreground = Ink, Margin = new Thickness(0,3,0,11), Cursor = Cursors.Hand };
  check.Checked += (s,e) => SetOption(key, true, true);
  check.Unchecked += (s,e) => SetOption(key, false, true);
  panel.Children.Add(check);
  return check;
 }
 CheckBox ChartCheck(string label, bool enabled, Brush color) {
  var check = new CheckBox { Content = label, IsChecked = enabled, FontSize = 11, Foreground = color, Margin = new Thickness(0,0,15,0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
  check.Checked += (s,e) => { if(!updatingDetailsControls) RenderPerformanceChart(); };
  check.Unchecked += (s,e) => { if(!updatingDetailsControls) RenderPerformanceChart(); };
  return check;
 }

 class FloatingPanel : Window {
  Guard host;
  StackPanel body;
  Border shell;
  public FloatingPanel(Guard owner) {
   host = owner;
   Title = "Disk Guard 悬浮监测";
   FontFamily = owner.FontFamily;
   Width = 340; Height = 210; MinWidth = 280; MinHeight = 150;
   WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
   AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
   WindowStartupLocation = WindowStartupLocation.Manual;
   Left = Math.Max(8, SystemParameters.WorkArea.Right - Width - 26);
   Top = Math.Max(8, SystemParameters.WorkArea.Bottom - Height - 26);
   shell = new Border {
    Background = SurfaceFill,
    BorderBrush = CardStroke,
    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22),
    Padding = new Thickness(16),
    Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = .16, Color = Colors.Black }
   };
   var layout = new DockPanel();
   var header = new Grid { Margin = new Thickness(0,0,0,10) };
   header.ColumnDefinitions.Add(new ColumnDefinition());
   header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   var titleStack = new StackPanel();
   titleStack.Children.Add(new TextBlock { Text = "●  磁盘观察室", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ink });
   titleStack.Children.Add(new TextBlock { Text = "独立悬浮 · 切换页面也不会消失", FontSize = 10, Foreground = Muted });
   Grid.SetColumn(titleStack, 0); header.Children.Add(titleStack);
   var close = host.SmallButton("×", () => Hide());
   Grid.SetColumn(close, 1); header.Children.Add(close);
   header.MouseLeftButtonDown += (s,e) => { if(e.ChangedButton == MouseButton.Left) try { DragMove(); } catch { } };
   DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
   body = new StackPanel();
   var bodyScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
   bodyScroll.Content = body; layout.Children.Add(bodyScroll);
   shell.Child = layout; Content = shell;
   Refresh();
  }
  public void HidePanel() { Hide(); }
   public void RefreshTheme() { FontFamily = host.FontFamily; if(shell == null) return; shell.Background = SurfaceFill; shell.BorderBrush = CardStroke; Refresh(); }
  public void Refresh() {
   if(body == null) return;
   body.Children.Clear();
   var nodeStack = new StackPanel();
   nodeStack.Children.Add(new TextBlock { Text = "节点纯净度参考 · Ping0", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Teal, Margin = new Thickness(0,0,0,3) });
   nodeStack.Children.Add(new TextBlock { Text = host.Ping0Summary(), FontSize = 10, Foreground = host.Ping0Brush(), TextWrapping = TextWrapping.Wrap });
   body.Children.Add(Card(nodeStack, 13, new Thickness(11,8,11,6)));
   var probeStack = new StackPanel();
   probeStack.Children.Add(new TextBlock { Text = "固定文件探针", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Violet, Margin = new Thickness(0,0,0,3) });
   probeStack.Children.Add(new TextBlock { Text = host.probe.Summary, FontSize = 10, Foreground = host.ProbeBrush(), TextWrapping = TextWrapping.Wrap });
   body.Children.Add(Card(probeStack, 13, new Thickness(11,8,11,6)));
   if(host.drives.Count == 0) {
     body.Children.Add(Text("正在连接性能计数器…", 13, Muted));
     body.Children.Add(Text("启动后会自动显示各物理磁盘指标。", 11, Muted));
    return;
   }
   foreach(var d in host.drives.Values.OrderBy(x => x.Index)) {
    var h = host.health.ContainsKey(d.Index) ? host.health[d.Index] : null;
    var cardStack = new StackPanel();
    var heading = new DockPanel();
     heading.Children.Add(new TextBlock { Text = "磁盘 " + (d.Index >= 0 ? d.Index.ToString() : d.Name), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ink });
     var stateText = new TextBlock { Text = h == null ? "采集中" : h.Severity, FontSize = 11, Foreground = h == null ? Muted : h.SeverityBrush, HorizontalAlignment = HorizontalAlignment.Right };
    DockPanel.SetDock(stateText, Dock.Right); heading.Children.Add(stateText); cardStack.Children.Add(heading);
    var metrics = new List<string>();
    if(host.options.Read) metrics.Add("↓ " + Rate(d.Read));
    if(host.options.Write) metrics.Add("↑ " + Rate(d.Write));
    if(host.options.Iops) metrics.Add("IOPS " + d.Iops.ToString("0"));
    if(host.options.Queue) metrics.Add("队列 " + d.Queue.ToString("0.0"));
    if(host.options.Active) metrics.Add("活跃 " + d.Active.ToString("0") + "%");
     cardStack.Children.Add(Text(metrics.Count == 0 ? "请在显示设置中选择指标" : String.Join("  ·  ", metrics), 12, Ink));
    if(h != null && (host.options.Temperature || host.options.Health || host.options.Wear || host.options.Errors || host.options.PowerOnHours || host.options.Endurance)) {
     var extra = new List<string>();
     if(host.options.Health) extra.Add("健康 " + h.Severity);
     if(host.options.Temperature) extra.Add("温度 " + OptionalNumber(h.Temperature, "°C"));
     if(host.options.Wear) extra.Add("磨损 " + OptionalNumber(h.Wear, "%"));
     if(host.options.Errors) extra.Add("错误 " + Count(h.MediaErrors));
     if(host.options.PowerOnHours) extra.Add("通电 " + Count(h.PowerOnHours) + " h");
     if(host.options.Endurance && (h.DataUnitsReadBytes.HasValue || h.DataUnitsWrittenBytes.HasValue)) extra.Add("累计读/写 " + Bytes(h.DataUnitsReadBytes) + " / " + Bytes(h.DataUnitsWrittenBytes));
      cardStack.Children.Add(Text(String.Join("  ·  ", extra), 10, Muted));
    }
     if(host.options.Workload) cardStack.Children.Add(Text(d.State, 10, d.State.Contains("高于") ? Amber : Muted));
    body.Children.Add(Card(cardStack, 15, new Thickness(12,10,12,6)));
   }
  }
 }

 public Guard() : this(true) { }
 public Guard(bool startMonitoring) {
  settingsPersistence = startMonitoring;
  Title = "Disk Guard · 磁盘观察室";
  Width = 920; Height = 620; MinWidth = 720; MinHeight = 500;
  FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
   WindowStartupLocation = WindowStartupLocation.CenterScreen;
   Background = Brushes.White; Icon = MakeLogo();
   if(startMonitoring) LoadOptions(); else options = new DisplayOptions();
   try { FontFamily = new FontFamily(String.IsNullOrWhiteSpace(fontFamilyName) ? "Microsoft YaHei UI" : fontFamilyName); } catch { fontFamilyName = "Microsoft YaHei UI"; FontFamily = new FontFamily(fontFamilyName); }
   ApplyThemePalette();
  BuildShell();
  timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(performanceIntervalSeconds) };
  timer.Tick += async (s,e) => await Sample();
  if(startMonitoring) timer.Start();
  bool normalRun = startMonitoring && !Environment.GetCommandLineArgs().Any(x => x.StartsWith("--preview"));
  closeToTray = normalRun;
  if(normalRun) { CreateTray(); LoadProbeTargets(); }
  Closing += (s,e) => { if(closeToTray && !exitRequested) { e.Cancel = true; HideToTray(); } };
  IsVisibleChanged += (s,e) => { if(MainVisible) RenderCurrent(); };
  StateChanged += (s,e) => { if(MainVisible) RenderCurrent(); };
  Closed += (s,e) => {
   if(tray != null) { tray.Visible = false; tray.Dispose(); }
   timer.Stop();
   SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
   if(floating != null) floating.Close();
   foreach(var drive in drives.Values) drive.Dispose();
  };
  SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
  if(startMonitoring) BeginHealthPoll(true);
   // 预览截图使用本地占位数据，避免 QA 启动时意外访问网络；正式运行仍遵循用户的 Ping0 开关。
   bool previewRun = Environment.GetCommandLineArgs().Any(x => x.StartsWith("--preview", StringComparison.OrdinalIgnoreCase));
   if(ping0Enabled && !previewRun) BeginPing0Poll(true);
  if(probeEnabled) lastProbeStart = DateTime.UtcNow;
 }

 void BuildShell() {
  root = new Grid();
  root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });
  root.ColumnDefinitions.Add(new ColumnDefinition());
  Content = root;
  root.Background = MakeBackgroundGradient();
  var nav = new DockPanel { Margin = new Thickness(22,28,22,20) };
  sidebar = new Border { Background = SidebarFill, Child = nav };
  root.Children.Add(sidebar);

  var footer = new StackPanel();
  footer.Children.Add(Text("●  数据摘要保存在本机", 11, Muted));
  footer.Children.Add(Text("只读监测 · 无需 Python", 11, Muted));
  footer.Children.Add(Text("Disk Guard / 0.6 Native", 10, Muted));
  DockPanel.SetDock(footer, Dock.Bottom); nav.Children.Add(footer);

  var links = new StackPanel(); nav.Children.Add(links);
  var brand = new StackPanel { Orientation = Orientation.Horizontal };
  brand.Children.Add(new Image { Source = MakeLogo(), Width = 36, Height = 36, Margin = new Thickness(0,0,10,0) });
  var brandText = new StackPanel();
  brandText.Children.Add(Text("磁盘观察室", 19, Ink));
  brandText.Children.Add(Text("看见负载，理解状态。", 10, Muted));
  brand.Children.Add(brandText); links.Children.Add(brand);
  links.Children.Add(new Border { Height = 24 });
  links.Children.Add(ActionButton("总览", () => BuildPage(PageKind.Overview)));
  links.Children.Add(ActionButton("详细指标", () => BuildPage(PageKind.Details)));
  links.Children.Add(ActionButton("显示设置", () => BuildPage(PageKind.Settings)));
  links.Children.Add(ActionButton("评估依据", () => BuildPage(PageKind.Evidence)));
  links.Children.Add(new Border { Height = 5 });
  links.Children.Add(ActionButton("打开 / 隐藏悬浮插件", () => ToggleFloating()));
  links.Children.Add(ActionButton("隐藏到系统托盘", () => { if(tray != null) HideToTray(); else WindowState = WindowState.Minimized; }));
  links.Children.Add(ActionButton("退出程序", ExitApplication));

  main = new Grid { Margin = new Thickness(32,32,24,20) };
  Grid.SetColumn(main, 1); root.Children.Add(main);
  BuildPage(PageKind.Overview);
 }

 static ImageSource MakeLogo() {
  var g = new DrawingGroup();
  g.Children.Add(new GeometryDrawing(new SolidColorBrush(DarkPalette ? Color.FromRgb(76,96,132) : Color.FromRgb(34,43,61)), null, new EllipseGeometry(new Point(16,16),15,15)));
  var p = new Pen(new SolidColorBrush(DarkPalette ? Color.FromRgb(155,224,255) : Color.FromRgb(122,220,255)), 2.1);
  g.Children.Add(new GeometryDrawing(null, p, Geometry.Parse("M 3,18 L 8,18 L 11,10 L 15,23 L 19,13 L 22,18 L 29,18")));
  return new DrawingImage(g);
 }

 void BuildPage(PageKind page) {
  currentPage = page;
  main.Children.Clear();
  overviewRead = overviewWrite = overviewIops = overviewQueue = overviewActive = overviewHealth = overviewStatus = overviewNode = overviewProbe = null;
  overviewDisks = detailsDisks = null;
  detailsDataStack = null; detailsChart = null; detailsChartCard = null; chartOptionsPanel = null;
  detailsLayerSelector = detailsDriveSelector = chartDomainSelector = themeSelector = null; chartReadCheck = chartWriteCheck = chartIopsCheck = chartQueueCheck = chartActiveCheck = null;
  detailsChartTitle = detailsSelectionHint = null;
   ping0StatusText = null; ping0IntervalSelector = null; probeStatusText = null; probeRows = null; probeSizeSelector = probeDurationSelector = probeScheduleSelector = null; performanceIntervalSelector = healthIntervalSelector = null; fontSelector = null;
  pageSubtitle = null;
  var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
  main.Children.Add(scroll);
  var panel = new StackPanel(); scroll.Content = panel;
  panel.Children.Add(Text("D I S K   G U A R D", 10, Muted));
  string title = page == PageKind.Overview ? "给每块硬盘，一份清晰的观察。" :
   page == PageKind.Details ? "所有指标，都能被看见。" :
   page == PageKind.Settings ? "把悬浮插件调成你的样子。" : "每个结论，都有依据。";
  panel.Children.Add(Text(title, 28, Ink));
  string subtitle = page == PageKind.Settings ? "显示、采集频率、可选网络节点与安全探针。" :
   page == PageKind.Details ? "按数据层查看：原始字段、计算指标、结论。" :
   page == PageKind.Evidence ? "保留流程图，并把学习样本与测试边界拆成章节。" :
   "实时观察读写变化；缺失字段保持未知。";
  pageSubtitle = Text(subtitle, 12, Muted);
  panel.Children.Add(pageSubtitle); panel.Children.Add(new Border { Height = 16 });
  if(page == PageKind.Overview) BuildOverview(panel);
  else if(page == PageKind.Details) BuildDetails(panel);
  else if(page == PageKind.Settings) BuildSettings(panel);
  else BuildEvidence(panel);
  var anim = new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(180));
  panel.BeginAnimation(OpacityProperty, anim);
  RenderCurrent();
 }

 void BuildOverview(StackPanel panel) {
  var heroStack = new StackPanel();
  heroStack.Children.Add(HeadingWithHelp("实时状态", "性能计数器只读采样；程序不会修改文件、执行修复或自动运行测试。", 16));
  heroStack.Children.Add(Text("只读观察 · 不主动修改文件", 11, Muted));
  var hero = Card(heroStack, 20, new Thickness(20,16,20,13));
  panel.Children.Add(hero);

  var metrics = new UniformGridShim { Columns = 5 };
  metrics.Children.Add(MetricCard("总读取", "—", Blue, out overviewRead));
  metrics.Children.Add(MetricCard("总写入", "—", Teal, out overviewWrite));
  metrics.Children.Add(MetricCard("总 IOPS", "—", Ink, out overviewIops));
  metrics.Children.Add(MetricCard("队列长度", "—", Ink, out overviewQueue));
  metrics.Children.Add(MetricCard("平均活跃", "—", Amber, out overviewActive));
  panel.Children.Add(metrics);

  var healthStack = new StackPanel();
  healthStack.Children.Add(HeadingWithHelp("健康证据", "寿命结论只使用设备自报的 SMART / Health、磨损、备用空间和错误字段；温度与瞬时负载单独展示。", 16));
  overviewHealth = Text("正在读取 Windows 存储状态、温度、磨损和错误计数…", 12, Muted);
  healthStack.Children.Add(overviewHealth);
  panel.Children.Add(Card(healthStack));
  var nodeStack = new StackPanel();
  nodeStack.Children.Add(HeadingWithHelp("节点纯净度参考 · Ping0", "第三方网络节点参考，不参与硬盘寿命结论；默认关闭，只有启用后才访问 Ping0。", 16));
  nodeStack.Children.Add(Text("默认关闭 · 不访问第三方", 11, Muted));
  overviewNode = Text(Ping0Summary(), 11, Ping0Brush());
  nodeStack.Children.Add(overviewNode);
  panel.Children.Add(Card(nodeStack, 18, new Thickness(17,13,17,9)));
  var probeStack = new StackPanel();
  probeStack.Children.Add(HeadingWithHelp("固定文件读写探针", "默认关闭。启用后在你指定的文件夹创建固定大小的顺序写入文件，Flush 后顺序读回并校验，最后删除；只用于同一台电脑的趋势参考，不是寿命百分比。", 15));
  overviewProbe = Text(probe.Summary, 11, ProbeBrush());
  probeStack.Children.Add(overviewProbe);
  panel.Children.Add(Card(probeStack, 18, new Thickness(17,13,17,9)));
  panel.Children.Add(Text("物理磁盘", 16, Ink));
  overviewDisks = new StackPanel(); panel.Children.Add(overviewDisks);
  overviewStatus = Text("正在连接系统计数器…", 11, Muted); panel.Children.Add(overviewStatus);
 }

 ComboBox StyledComboBox(double width, double height, Thickness margin) {
  var combo = new ComboBox { Width = width, Height = height, Margin = margin, Background = InputFill, BorderBrush = InputBorder, Foreground = Ink, Padding = new Thickness(8,5,8,5) };
  // 经典 WPF 默认模板在部分 Windows 主题下会把选中项强制画成浅灰底 + 白字。
  // 这里使用完全自绘的模板，关闭系统主题对闭合态和 Popup 的隐式覆盖。
  combo.Resources[SystemColors.WindowBrushKey] = InputFill;
  combo.Resources[SystemColors.WindowTextBrushKey] = Ink;
  combo.Resources[SystemColors.ControlBrushKey] = InputFill;
  combo.Resources[SystemColors.ControlTextBrushKey] = Ink;
  combo.Resources[SystemColors.HighlightBrushKey] = InputHighlight;
  combo.Resources[SystemColors.HighlightTextBrushKey] = Ink;
  var itemStyle = new Style(typeof(ComboBoxItem));
  itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
  itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, InputFill));
  itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
  itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
  itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8,5,8,5)));
  var itemTemplate = new ControlTemplate(typeof(ComboBoxItem));
  var itemBorder = new FrameworkElementFactory(typeof(Border));
  itemBorder.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  itemBorder.SetValue(Border.PaddingProperty, new Thickness(10,8,10,8));
  var itemContent = new FrameworkElementFactory(typeof(ContentPresenter));
  itemContent.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  itemBorder.AppendChild(itemContent); itemTemplate.VisualTree = itemBorder;
  itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
  var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
  hover.Setters.Add(new Setter(Control.BackgroundProperty, InputHighlight));
  hover.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
  itemStyle.Triggers.Add(hover);
  combo.ItemContainerStyle = itemStyle;
  var template = new ControlTemplate(typeof(ComboBox));
  var rootPanel = new FrameworkElementFactory(typeof(Grid));
  var outer = new FrameworkElementFactory(typeof(Border));
  outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
  outer.SetValue(Border.BorderThicknessProperty, new Thickness(1));
  outer.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  outer.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  var selection = new FrameworkElementFactory(typeof(ContentPresenter));
  selection.SetValue(FrameworkElement.MarginProperty, new Thickness(9,2,28,2));
  selection.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
  selection.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  selection.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("SelectionBoxItemTemplate") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  selection.SetBinding(System.Windows.Documents.TextElement.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  outer.AppendChild(selection);
  rootPanel.AppendChild(outer);
  var arrow = new FrameworkElementFactory(typeof(TextBlock));
  arrow.SetValue(TextBlock.TextProperty, "▼");
  arrow.SetValue(TextBlock.FontSizeProperty, 8.0);
  arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
  arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
  arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0,0,9,0));
  arrow.SetBinding(System.Windows.Documents.TextElement.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  rootPanel.AppendChild(arrow);
  // A real toggle owns only the closed field. Item clicks and keyboard navigation
  // remain with ComboBox/ComboBoxItem instead of being intercepted in preview.
  var toggle = new FrameworkElementFactory(typeof(ToggleButton), "DropDownToggle");
  toggle.SetValue(Control.BackgroundProperty, Brushes.Transparent);
  toggle.SetValue(UIElement.FocusableProperty, false);
  toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
  toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Mode = BindingMode.TwoWay });
  var toggleTemplate = new ControlTemplate(typeof(ToggleButton));
  var hitSurface = new FrameworkElementFactory(typeof(Border));
  hitSurface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
  toggleTemplate.VisualTree = hitSurface;
  toggle.SetValue(Control.TemplateProperty, toggleTemplate);
  rootPanel.AppendChild(toggle);
  var popup = new FrameworkElementFactory(typeof(Popup));
  popup.Name = "PART_Popup";
  popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
  popup.SetValue(Popup.AllowsTransparencyProperty, true);
  popup.SetValue(Popup.StaysOpenProperty, true);
  popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
  popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Mode = BindingMode.TwoWay });
  popup.SetBinding(Popup.PlacementTargetProperty, new Binding(".") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  popup.SetBinding(Popup.WidthProperty, new Binding("ActualWidth") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  var popupBorder = new FrameworkElementFactory(typeof(Border));
  popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
  popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
  popupBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0,3,0,0));
  popupBorder.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  popupBorder.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
  var popupScroll = new FrameworkElementFactory(typeof(ScrollViewer));
  popupScroll.SetValue(FrameworkElement.MaxHeightProperty, 300.0);
  popupScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
  popupScroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
  popupScroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
  popupScroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
  popupBorder.AppendChild(popupScroll);
  popup.AppendChild(popupBorder);
  rootPanel.AppendChild(popup);
  template.VisualTree = rootPanel;
  combo.Template = template;
  return combo;
 }

 TextBox StyledTextBox(double width, string value, Thickness margin) {
  var box = new TextBox { Width = width, Height = 32, Text = value ?? "", Margin = margin, Background = InputFill, BorderBrush = InputBorder, Foreground = Ink, Padding = new Thickness(8,5,8,5), FontSize = 12 };
  box.Resources[SystemColors.WindowBrushKey] = InputFill;
  box.Resources[SystemColors.WindowTextBrushKey] = Ink;
  box.Resources[SystemColors.ControlBrushKey] = InputFill;
  box.Resources[SystemColors.ControlTextBrushKey] = Ink;
  box.Resources[SystemColors.HighlightBrushKey] = InputHighlight;
  box.Resources[SystemColors.HighlightTextBrushKey] = Ink;
  var border = new FrameworkElementFactory(typeof(Border));
  border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
  border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
  border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
  border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
  var content = new FrameworkElementFactory(typeof(ScrollViewer)); content.Name = "PART_ContentHost";
  border.AppendChild(content); box.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
  box.HorizontalAlignment = HorizontalAlignment.Left;
  return box;
 }

 PasswordBox StyledPasswordBox(double width, Thickness margin) {
  var box = new PasswordBox { Width = width, Height = 32, Margin = margin, Background = InputFill, BorderBrush = InputBorder, Foreground = Ink, Padding = new Thickness(8,5,8,5), FontSize = 12 };
  box.Resources[SystemColors.WindowBrushKey] = InputFill;
  box.Resources[SystemColors.WindowTextBrushKey] = Ink;
  box.Resources[SystemColors.ControlBrushKey] = InputFill;
  box.Resources[SystemColors.ControlTextBrushKey] = Ink;
  box.Resources[SystemColors.HighlightBrushKey] = InputHighlight;
  box.Resources[SystemColors.HighlightTextBrushKey] = Ink;
  return box;
 }

 void BuildDetails(StackPanel panel) {
  var controlStack = new StackPanel();
  controlStack.Children.Add(HeadingWithHelp("查看方式", "原始数据是设备直接报告值；计算指标只使用同一硬盘的历史；结论由来源、缺失和持续性规则筛选。", 16));
  var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
  controls.Children.Add(InlineLabel("数据层", 44));
  detailsLayerSelector = StyledComboBox(150, 32, new Thickness(8,0,16,0));
  detailsLayerSelector.Items.Add(new ComboBoxItem { Content = "原始数据", Tag = DataViewKind.Raw });
  detailsLayerSelector.Items.Add(new ComboBoxItem { Content = "计算指标", Tag = DataViewKind.Derived });
  detailsLayerSelector.Items.Add(new ComboBoxItem { Content = "结论", Tag = DataViewKind.Conclusion });
  detailsLayerSelector.SelectedIndex = selectedDataView == DataViewKind.Raw ? 0 : selectedDataView == DataViewKind.Derived ? 1 : 2;
  detailsLayerSelector.SelectionChanged += (s,e) => {
   if(updatingDetailsControls) return;
   var item = detailsLayerSelector.SelectedItem as ComboBoxItem;
   if(item != null && item.Tag is DataViewKind) selectedDataView = (DataViewKind)item.Tag;
   RenderDetailsView();
  };
  controls.Children.Add(detailsLayerSelector);
  controls.Children.Add(InlineLabel("硬盘", 38));
   detailsDriveSelector = StyledComboBox(190, 32, new Thickness(8,0,16,0));
  detailsDriveSelector.SelectionChanged += (s,e) => {
   if(updatingDetailsControls) return;
   var item = detailsDriveSelector.SelectedItem as ComboBoxItem;
   selectedDetailsDriveName = item == null || item.Tag == null ? "" : (item.Tag as string) ?? "";
   RenderDetailsView();
  };
  controls.Children.Add(detailsDriveSelector);
  var refresh = ActionButton("刷新健康", () => BeginHealthPoll(true));
  refresh.Margin = new Thickness(0,0,0,0); refresh.VerticalAlignment = VerticalAlignment.Center;
  controls.Children.Add(refresh);
  controlStack.Children.Add(controls);
  detailsSelectionHint = Text("正在准备硬盘筛选器…", 10, Muted);
  controlStack.Children.Add(detailsSelectionHint);
  var roleLegend = new UniformGridShim { Columns = 3, Margin = new Thickness(0,3,0,0) };
  roleLegend.Children.Add(InfoTile("设备健康 / 退化", "参与寿命结论", Ink));
  roleLegend.Children.Add(InfoTile("瞬时运行状态", "当前工作量", Ink));
  roleLegend.Children.Add(InfoTile("累计暴露背景", "使用记录", Ink));
  controlStack.Children.Add(roleLegend);
  panel.Children.Add(Card(controlStack, 18, new Thickness(17,14,17,9)));

  var chartStack = new StackPanel();
  var chartHeading = new DockPanel { LastChildFill = false, Margin = new Thickness(0,0,0,4) };
  detailsChartTitle = Text("动态性能曲线 · 等待采样", 16, Ink); detailsChartTitle.Margin = new Thickness(0); chartHeading.Children.Add(detailsChartTitle);
  var chartHelp = HelpBadge("性能数据按采样间隔进入内存环形队列，最多保留最近 4 分钟；时域显示变化，频域显示周期性，不参与寿命评分。"); DockPanel.SetDock(chartHelp, Dock.Right); chartHeading.Children.Add(chartHelp);
  chartStack.Children.Add(chartHeading);
  chartOptionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,2,0,8) };
  chartReadCheck = ChartCheck("读取", true, Blue); chartOptionsPanel.Children.Add(chartReadCheck);
  chartWriteCheck = ChartCheck("写入", true, Teal); chartOptionsPanel.Children.Add(chartWriteCheck);
  chartIopsCheck = ChartCheck("IOPS", false, Ink); chartOptionsPanel.Children.Add(chartIopsCheck);
  chartQueueCheck = ChartCheck("队列", false, Violet); chartOptionsPanel.Children.Add(chartQueueCheck);
  chartActiveCheck = ChartCheck("活跃", true, Amber); chartOptionsPanel.Children.Add(chartActiveCheck);
  var chartLabel = InlineLabel("图形", 32); chartLabel.FontSize = 10; chartOptionsPanel.Children.Add(chartLabel);
  chartDomainSelector = StyledComboBox(120, 28, new Thickness(7,0,0,0)); chartDomainSelector.VerticalAlignment = VerticalAlignment.Center;
  chartDomainSelector.Items.Add(new ComboBoxItem { Content = "时域趋势", Tag = ChartDomain.Time });
  chartDomainSelector.Items.Add(new ComboBoxItem { Content = "频率谱", Tag = ChartDomain.Frequency });
  chartDomainSelector.SelectedIndex = chartDomain == ChartDomain.Time ? 0 : 1;
  chartDomainSelector.SelectionChanged += (s,e) => {
   if(updatingDetailsControls) return;
   var item = chartDomainSelector.SelectedItem as ComboBoxItem;
   if(item != null && item.Tag is ChartDomain) chartDomain = (ChartDomain)item.Tag;
   RenderPerformanceChart();
  };
  chartOptionsPanel.Children.Add(chartDomainSelector);
  chartStack.Children.Add(chartOptionsPanel);
  detailsChart = new Canvas { Height = 190, Background = ChartFill, ClipToBounds = true };
  detailsChart.SizeChanged += (s,e) => RenderPerformanceChart();
  chartStack.Children.Add(detailsChart);
  detailsChartCard = Card(chartStack, 20, new Thickness(17,14,17,14));
  panel.Children.Add(detailsChartCard);

  detailsDisks = new StackPanel(); detailsDataStack = detailsDisks; panel.Children.Add(detailsDisks);
 }

 void BuildSettings(StackPanel panel) {
  var cardStack = new StackPanel();
  cardStack.Children.Add(HeadingWithHelp("悬浮插件显示哪些信息", "勾选项只控制悬浮窗显示，不会改变底层采集；选择会保存到本机的小型配置文件。", 17));
  cardStack.Children.Add(Text("实时性能 · 健康字段 · 累计背景", 11, Muted));
  var columns = new UniformGridShim { Columns = 2 };
  var left = new StackPanel(); var right = new StackPanel();
  OptionCheck(left, "读取速度", "read"); OptionCheck(left, "写入速度", "write");
  OptionCheck(left, "IOPS", "iops"); OptionCheck(left, "队列长度", "queue");
  OptionCheck(left, "磁盘活跃度", "active"); OptionCheck(left, "负载判定", "workload");
  OptionCheck(right, "健康状态", "health"); OptionCheck(right, "温度 / 上限", "temperature");
  OptionCheck(right, "SSD 磨损", "wear"); OptionCheck(right, "错误计数", "errors");
  OptionCheck(right, "通电小时", "power_on_hours"); OptionCheck(right, "累计读写 / NVMe", "endurance");
   columns.Children.Add(left); columns.Children.Add(right); cardStack.Children.Add(columns);
   panel.Children.Add(Card(cardStack));

   var typeStack = new StackPanel();
   typeStack.Children.Add(HeadingWithHelp("字体与层级", "标题、正文、下拉菜单和悬浮窗统一继承这里的字体；中文字体不可用时由 Windows 自动回退。", 17));
   typeStack.Children.Add(Text("统一字体 · 统一层级 · 不改变数据采集", 11, Muted));
   var fontRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
   fontRow.Children.Add(InlineLabel("界面字体", 150));
   fontSelector = StyledComboBox(230, 32, new Thickness(0));
   var fontChoices = new [] { "Microsoft YaHei UI", "Segoe UI", "Microsoft JhengHei UI" };
   foreach(var name in fontChoices) fontSelector.Items.Add(new ComboBoxItem { Content = name, Tag = name });
   int fontIndex = Array.IndexOf(fontChoices, fontFamilyName);
   fontSelector.SelectedIndex = fontIndex < 0 ? 0 : fontIndex;
   fontSelector.SelectionChanged += (s,e) => {
    var item = fontSelector.SelectedItem as ComboBoxItem;
    var name = item == null ? "" : (item.Tag as string) ?? "";
    if(String.IsNullOrWhiteSpace(name) || name == fontFamilyName) return;
    fontFamilyName = name;
    try { FontFamily = new FontFamily(fontFamilyName); } catch { fontFamilyName = "Microsoft YaHei UI"; FontFamily = new FontFamily(fontFamilyName); }
    SaveOptions(); ApplyThemePalette(); RebuildShellForTheme();
   };
   fontRow.Children.Add(fontSelector); typeStack.Children.Add(fontRow);
   panel.Children.Add(Card(typeStack, 18, new Thickness(17,14,17,10)));

   var samplingStack = new StackPanel();
  samplingStack.Children.Add(HeadingWithHelp("采集频率", "性能计数器是瞬时原始数据；健康 / 温度 / SMART 是变化较慢的设备字段。频率越高，内存样本更新越快，但不会改变硬盘。", 17));
  var perfRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,8) };
  perfRow.Children.Add(InlineLabel("性能原始数据", 150));
  performanceIntervalSelector = StyledComboBox(150, 32, new Thickness(0));
  performanceIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 1 秒", Tag = 1 });
  performanceIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 2 秒", Tag = 2 });
  performanceIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 5 秒", Tag = 5 });
  performanceIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 10 秒", Tag = 10 });
  performanceIntervalSelector.SelectedIndex = performanceIntervalSeconds <= 1 ? 0 : performanceIntervalSeconds <= 2 ? 1 : performanceIntervalSeconds <= 5 ? 2 : 3;
  performanceIntervalSelector.SelectionChanged += (s,e) => { var item = performanceIntervalSelector.SelectedItem as ComboBoxItem; if(item != null && item.Tag != null) { performanceIntervalSeconds = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture); ApplySamplingIntervals(); SaveOptions(); RenderCurrent(); } };
  perfRow.Children.Add(performanceIntervalSelector);
  samplingStack.Children.Add(perfRow);
  var healthRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,2) };
  healthRow.Children.Add(InlineLabel("健康 / 温度 / SMART", 150));
  healthIntervalSelector = StyledComboBox(150, 32, new Thickness(0));
  healthIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 1 分钟", Tag = 1 });
  healthIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 5 分钟", Tag = 5 });
  healthIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 15 分钟", Tag = 15 });
  healthIntervalSelector.Items.Add(new ComboBoxItem { Content = "每 30 分钟", Tag = 30 });
  healthIntervalSelector.SelectedIndex = healthPollMinutes <= 1 ? 0 : healthPollMinutes <= 5 ? 1 : healthPollMinutes <= 15 ? 2 : 3;
  healthIntervalSelector.SelectionChanged += (s,e) => { var item = healthIntervalSelector.SelectedItem as ComboBoxItem; if(item != null && item.Tag != null) { healthPollMinutes = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture); SaveOptions(); } };
  healthRow.Children.Add(healthIntervalSelector);
  samplingStack.Children.Add(healthRow);
  panel.Children.Add(Card(samplingStack, 18, new Thickness(17,14,17,10)));

  var themeStack = new StackPanel();
  themeStack.Children.Add(HeadingWithHelp("主题模式", "浅色、深色或跟随 Windows 应用主题；切换后会重绘当前页面、下拉菜单和悬浮插件。", 17));
  themeStack.Children.Add(Text("浅色 · 深色 · 跟随系统", 11, Muted));
  themeSelector = StyledComboBox(230, 32, new Thickness(0,2,0,0));
  themeSelector.Items.Add(new ComboBoxItem { Content = "跟随系统", Tag = ThemeMode.System });
  themeSelector.Items.Add(new ComboBoxItem { Content = "浅色模式", Tag = ThemeMode.Light });
  themeSelector.Items.Add(new ComboBoxItem { Content = "深色模式", Tag = ThemeMode.Dark });
  themeSelector.SelectedIndex = themeMode == ThemeMode.System ? 0 : themeMode == ThemeMode.Light ? 1 : 2;
  themeSelector.SelectionChanged += (s,e) => {
   if(updatingDetailsControls) return;
   var item = themeSelector.SelectedItem as ComboBoxItem;
   if(item == null || !(item.Tag is ThemeMode)) return;
   themeMode = (ThemeMode)item.Tag;
   SaveOptions(); ApplyThemePalette(); RebuildShellForTheme();
  };
  var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,4,0,0) };
  themeRow.Children.Add(InlineLabel("主题模式",150)); themeRow.Children.Add(themeSelector); themeStack.Children.Add(themeRow);
  panel.Children.Add(Card(themeStack, 18, new Thickness(17,14,17,10)));

  var probeStack = new StackPanel();
  probeStack.Children.Add(HeadingWithHelp("固定文件读写探针", "测试不读取用户已有的大文件，而是在目标文件夹创建固定大小、固定内容的顺序文件；写入并 Flush 后顺序读回、校验，再删除。默认关闭，只有启用并手动或按计划运行。", 17));
  probeStack.Children.Add(Text("这是可控的实际读写，不是硬盘寿命分数；每次会产生设定大小的真实写入。", 11, Muted));
  var probeEnable = new CheckBox { Content = "启用探针功能（默认关闭）", IsChecked = probeEnabled, FontSize = 13, Foreground = Ink, Margin = new Thickness(0,3,0,8), Cursor = Cursors.Hand };
  probeEnable.Checked += (s,e) => { probeEnabled = true; lastProbeStart = DateTime.UtcNow; SaveOptions(); RenderProbeVisuals(); };
  probeEnable.Unchecked += (s,e) => { probeEnabled = false; SaveOptions(); RenderProbeVisuals(); };
  probeStack.Children.Add(probeEnable);
  probeRows = new StackPanel(); probeStack.Children.Add(probeRows); BuildProbeRows();
  var probeOptions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,8) };
  probeOptions.Children.Add(InlineLabel("文件大小", 75));
  probeSizeSelector = StyledComboBox(110, 32, new Thickness(0,0,16,0));
  foreach(var mb in new [] { 32, 64, 128, 256, 512 }) probeSizeSelector.Items.Add(new ComboBoxItem { Content = mb + " MB", Tag = mb });
  probeSizeSelector.SelectedIndex = probeSizeMb <= 32 ? 0 : probeSizeMb <= 64 ? 1 : probeSizeMb <= 128 ? 2 : probeSizeMb <= 256 ? 3 : 4;
  probeSizeSelector.SelectionChanged += (s,e) => { var item = probeSizeSelector.SelectedItem as ComboBoxItem; if(item != null && item.Tag != null) { probeSizeMb = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture); SaveOptions(); } };
  probeOptions.Children.Add(probeSizeSelector);
  probeOptions.Children.Add(InlineLabel("时限", 38));
  probeDurationSelector = StyledComboBox(110, 32, new Thickness(0,0,16,0));
  foreach(var seconds in new [] { 10, 30, 60, 120 }) probeDurationSelector.Items.Add(new ComboBoxItem { Content = seconds + " 秒", Tag = seconds });
  probeDurationSelector.SelectedIndex = probeMaxSeconds <= 10 ? 0 : probeMaxSeconds <= 30 ? 1 : probeMaxSeconds <= 60 ? 2 : 3;
  probeDurationSelector.SelectionChanged += (s,e) => { var item = probeDurationSelector.SelectedItem as ComboBoxItem; if(item != null && item.Tag != null) { probeMaxSeconds = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture); SaveOptions(); } };
  probeOptions.Children.Add(probeDurationSelector);
  probeOptions.Children.Add(InlineLabel("计划", 38));
  probeScheduleSelector = StyledComboBox(110, 32, new Thickness(0));
  probeScheduleSelector.Items.Add(new ComboBoxItem { Content = "仅手动", Tag = 0 });
  probeScheduleSelector.Items.Add(new ComboBoxItem { Content = "每 1 小时", Tag = 60 });
  probeScheduleSelector.Items.Add(new ComboBoxItem { Content = "每 6 小时", Tag = 360 });
  probeScheduleSelector.Items.Add(new ComboBoxItem { Content = "每天", Tag = 1440 });
  probeScheduleSelector.Items.Add(new ComboBoxItem { Content = "每周", Tag = 10080 });
  probeScheduleSelector.SelectedIndex = probeIntervalMinutes <= 0 ? 0 : probeIntervalMinutes <= 60 ? 1 : probeIntervalMinutes <= 360 ? 2 : probeIntervalMinutes <= 1440 ? 3 : 4;
  probeScheduleSelector.SelectionChanged += (s,e) => { var item = probeScheduleSelector.SelectedItem as ComboBoxItem; if(item != null && item.Tag != null) { probeIntervalMinutes = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture); lastProbeStart = DateTime.UtcNow; SaveOptions(); } };
  probeOptions.Children.Add(probeScheduleSelector);
  probeStack.Children.Add(probeOptions);
  var probeButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,4) };
  var refreshDisks = ActionButton("刷新硬盘", () => { if(probeTask == null) { SaveOptions(); LoadProbeTargets(); } }, HorizontalAlignment.Left); probeButtons.Children.Add(refreshDisks);
  var probeNow = ActionButton("测试勾选硬盘", () => { SaveOptions(); BeginProbeIfDue(true); }, HorizontalAlignment.Center); probeNow.Margin = new Thickness(0,0,10,0); probeButtons.Children.Add(probeNow);
  probeStack.Children.Add(probeButtons);
  probeStatusText = Text(probe.Summary, 10, ProbeBrush()); probeStack.Children.Add(probeStatusText);
  panel.Children.Add(Card(probeStack, 18, new Thickness(17,14,17,10)));

  var pingStack = new StackPanel();
  pingStack.Children.Add(HeadingWithHelp("节点纯净度参考 · Ping0", "第三方网络节点参考，不参与硬盘寿命结论；只在启用后查询公网 IP / 目标 IP。详细 iprisk 字段需 API Key，并可能产生服务费用。", 17));
  pingStack.Children.Add(Text("可选联网 · 不上传磁盘数据", 11, Muted));
  var pingEnable = new CheckBox { Content = "启用 Ping0 节点查询", IsChecked = ping0Enabled, FontSize = 13, Foreground = Ink, Margin = new Thickness(0,3,0,8), Cursor = Cursors.Hand };
  pingEnable.Checked += (s,e) => { ping0Enabled = true; ping0 = new Ping0Snapshot { Enabled = true, Status = "正在查询" }; SaveOptions(); RenderPing0Visuals(); BeginPing0Poll(true); };
  pingEnable.Unchecked += (s,e) => { ping0Enabled = false; ping0 = new Ping0Snapshot(); ping0ApiKey = ""; SaveOptions(); RenderPing0Visuals(); RenderCurrent(); };
  pingStack.Children.Add(pingEnable);
  var targetRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,7) };
  targetRow.Children.Add(new TextBlock { Text = "目标 IP（留空=当前公网 IPv4）", Width = 185, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
  var targetBox = StyledTextBox(220, ping0TargetIp, new Thickness(0,0,10,0));
  targetBox.ToolTip = "留空时使用 Ping0 /geo 获取当前公网地址；填写后仅查询该节点。";
  targetBox.LostFocus += (s,e) => { ping0TargetIp = targetBox.Text.Trim(); SaveOptions(); };
  targetRow.Children.Add(targetBox);
  pingStack.Children.Add(targetRow);
  var keyRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,7) };
  keyRow.Children.Add(new TextBlock { Text = "API Key（可选）", Width = 185, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
  var keyBox = StyledPasswordBox(220, new Thickness(0,0,10,0));
  keyBox.Password = ping0ApiKey;
  keyBox.ToolTip = "仅保存在本次运行的内存中，不写入配置文件；关闭程序后需要重新输入。";
  keyBox.PasswordChanged += (s,e) => ping0ApiKey = keyBox.Password;
  keyRow.Children.Add(keyBox);
  var checkNow = ActionButton("立即检测", () => { if(!ping0Enabled) { ping0StatusText.Text = "请先勾选“启用 Ping0 节点查询”。"; ping0StatusText.Foreground = Amber; } else { ping0TargetIp = targetBox.Text.Trim(); BeginPing0Poll(true); } }, HorizontalAlignment.Center);
  checkNow.Margin = new Thickness(0);
  keyRow.Children.Add(checkNow);
  pingStack.Children.Add(keyRow);
  var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,0,4) };
  intervalRow.Children.Add(new TextBlock { Text = "自动更新间隔", Width = 185, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
  ping0IntervalSelector = StyledComboBox(140, 32, new Thickness(0));
  ping0IntervalSelector.Items.Add(new ComboBoxItem { Content = "15 分钟", Tag = 15 });
  ping0IntervalSelector.Items.Add(new ComboBoxItem { Content = "30 分钟", Tag = 30 });
  ping0IntervalSelector.Items.Add(new ComboBoxItem { Content = "60 分钟", Tag = 60 });
  ping0IntervalSelector.SelectedIndex = ping0IntervalMinutes <= 15 ? 0 : ping0IntervalMinutes <= 30 ? 1 : 2;
  ping0IntervalSelector.SelectionChanged += (s,e) => { var item = ping0IntervalSelector.SelectedItem as ComboBoxItem; if(item != null && item.Tag != null) { ping0IntervalMinutes = Convert.ToInt32(item.Tag, CultureInfo.InvariantCulture); SaveOptions(); } };
  intervalRow.Children.Add(ping0IntervalSelector);
  pingStack.Children.Add(intervalRow);
  ping0StatusText = Text(Ping0Summary(), 10, Ping0Brush());
  pingStack.Children.Add(ping0StatusText);
  pingStack.Children.Add(Text("隐私边界：不会上传磁盘型号、读写数据或文件内容；开启后只会把公网 IP / 目标 IP 发给 Ping0。iprisk 在界面中按 Ping0 原值显示，不自定义跨运营商阈值，也不参与硬盘寿命评分。", 10, Muted));
  panel.Children.Add(Card(pingStack, 18, new Thickness(17,14,17,10)));

  var opacityStack = new StackPanel();
  opacityStack.Children.Add(HeadingWithHelp("悬浮透明度", "悬浮窗独立置顶、可拖动；切换主页面或最小化主窗口时仍保持显示。", 17));
  opacityStack.Children.Add(Text("轻盈 · 置顶 · 可拖动", 11, Muted));
  var slider = new Slider { Minimum = .55, Maximum = 1.0, Value = floating == null ? .94 : floating.Opacity, TickFrequency = .05, IsSnapToTickEnabled = false, Margin = new Thickness(0,6,0,6) };
  slider.ValueChanged += (s,e) => { if(floating != null) floating.Opacity = e.NewValue; };
  opacityStack.Children.Add(slider);
  var opacityButtons = new StackPanel { Orientation = Orientation.Horizontal };
  opacityButtons.Children.Add(ActionButton("半透明", () => { slider.Value = .72; }, HorizontalAlignment.Center));
  opacityButtons.Children.Add(ActionButton("清晰", () => { slider.Value = .96; }, HorizontalAlignment.Center));
  opacityButtons.Children.Add(ActionButton("打开悬浮", () => ToggleFloating(true), HorizontalAlignment.Center));
  opacityStack.Children.Add(opacityButtons);
  panel.Children.Add(Card(opacityStack));

  var reset = ActionButton("恢复推荐指标", () => {
   options = new DisplayOptions(); SaveOptions(); BuildPage(PageKind.Settings);
   if(floating != null && floating.IsVisible) floating.Refresh();
  });
  panel.Children.Add(reset);
 }

 Border FlowNode(string step, string title, string detail, Brush color) {
  var stack = new StackPanel();
  stack.Children.Add(Text(step, 10, color));
  stack.Children.Add(ValueText(title, 12, Ink));
  stack.Children.Add(Text(detail, 9, Muted));
  var node = Card(stack, 14, new Thickness(9,8,9,6));
  node.Width = 100; node.Margin = new Thickness(0,0,3,5); node.BorderBrush = color;
  return node;
 }
 TextBlock FlowArrow() { return new TextBlock { Text = "›", FontSize = 22, Foreground = Muted, Width = 10, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0,0,2,5) }; }
 Border TreeBlock(string title, string detail, Brush color) {
  var stack = new StackPanel();
  stack.Children.Add(Text(title, 12, color));
  stack.Children.Add(Text(detail, 10, Muted));
  var node = Card(stack, 14, new Thickness(12,9,12,6));
  node.BorderBrush = color; node.BorderThickness = new Thickness(2,1,1,1);
  return node;
 }
 Border FormulaBlock(string title, string formula, string use, Brush color) {
  var stack = new StackPanel();
  stack.Children.Add(Text(title, 12, color));
  stack.Children.Add(Text(formula, 11, Ink));
  stack.Children.Add(Text(use, 9, Muted));
  var node = Card(stack, 14, new Thickness(12,9,12,7));
  node.BorderBrush = color;
  return node;
 }
 Border ChapterBlock(string title, string lead, Brush color, params string[] points) {
  var stack = new StackPanel();
  var heading = Text(title, 13, color); heading.Margin = new Thickness(0,0,0,4); stack.Children.Add(heading);
  if(!String.IsNullOrWhiteSpace(lead)) { var intro = Text(lead, 10, Muted); intro.Margin = new Thickness(0,0,0,4); stack.Children.Add(intro); }
  int number = 1;
  foreach(var point in points ?? new string[0]) { var item = Text(number + ". " + point, 10, Ink); item.Margin = new Thickness(0,0,0,4); stack.Children.Add(item); number++; }
  var card = Card(stack, 14, new Thickness(13,10,13,7)); card.BorderBrush = color; return card;
 }

 void BuildEvidence(StackPanel panel) {
  var body = new StackPanel();
  body.Children.Add(HeadingWithHelp("理解模型 · 从数据到结论", "这里把原始数据、学习样本、计算、筛选、结论和安全测试分章节说明；页面仍保留流程图。", 18));
  body.Children.Add(Text("章节 1 · 数据流", 14, Ink));
  var flowScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
  var flow = new StackPanel { Orientation = Orientation.Horizontal };
  flow.Children.Add(FlowNode("01", "采集", "计数器 / SMART / 温度", Blue)); flow.Children.Add(FlowArrow());
  flow.Children.Add(FlowNode("02", "分类", "寿命 · 瞬时 · 累计", Teal)); flow.Children.Add(FlowArrow());
  flow.Children.Add(FlowNode("03", "计算", "基线 / 频谱 / 趋势", Violet)); flow.Children.Add(FlowArrow());
  flow.Children.Add(FlowNode("04", "筛选", "来源 / 缺失 / 持续性", Amber)); flow.Children.Add(FlowArrow());
  flow.Children.Add(FlowNode("05", "结论", "寿命证据 + 当前风险", Red));
  flowScroll.Content = flow; body.Children.Add(flowScroll);
  body.Children.Add(HeadingWithHelp("数据分类树", "同一个字段只进入它对应的角色；瞬时工作量和累计背景不会直接变成寿命下降。", 15));
  var tree = new UniformGridShim { Columns = 2 };
  tree.Children.Add(TreeBlock("├─ 寿命证据 · 进入 LifetimeSeverity", "SMART 预测失败、设备状态、磨损、备用空间、介质 / 读写错误、不可纠正错误、NVMe 可靠性警告。", Red));
  tree.Children.Add(TreeBlock("├─ 瞬时运行 · 进入 ThermalRisk / 负载标签", "读取、写入、IOPS、队列、活跃度、当前温度；只描述此刻的工作量和热状态。", Blue));
  tree.Children.Add(TreeBlock("├─ 累计背景 · 只用于长期上下文", "通电小时、累计读写、异常关机、控制器忙时间、错误日志；没有跨重启差分时不单独判定寿命。", Amber));
  tree.Children.Add(TreeBlock("└─ 身份与来源 · 不直接判定风险", "型号、介质、总线、容量、固件、序列号、接口来源；用于解释和匹配设备特性。", Muted));
  body.Children.Add(tree);
  body.Children.Add(HeadingWithHelp("主要计算节点", "各计算节点输出不同类型的指标，不把不同物理含义强行合成单一分数。", 15));
  var formulas = new UniformGridShim { Columns = 2 };
   formulas.Children.Add(FormulaBlock("本盘负载基线", "y = ln(1 + W / 1,000,000)；z = (y − median) / (1.4826 × MAD)", "z > 3.5 且超过本盘 95 分位，并用连续样本覆盖约 5 秒，才标记负载异常；不进入寿命结论。", Blue));
  formulas.Children.Add(FormulaBlock("频率谱", "去均值 → Hann 窗 → Xₖ = Σ xₙe⁻ⁱ²πkn/N；fₖ = k/(NΔt)", "用于观察周期性 I/O；它是工作模式分析，不是健康评分。", Violet));
  formulas.Children.Add(FormulaBlock("温度风险", "当前温度 ÷ 设备自己报告的温度上限", "只判断当前热风险；没有设备上限就显示未知，不使用跨品牌绝对阈值。", Amber));
  formulas.Children.Add(FormulaBlock("寿命证据", "设备自报健康字段 → 严重 / 关注 / 正常 / 未知", "不把“今天写得多”换算成寿命下降；长期变化需要同一硬盘跨重启快照的差分。", Red));
  body.Children.Add(formulas);
  body.Children.Add(Text("章节 2 · 学习样本", 14, Ink));
  var learning = new UniformGridShim { Columns = 2 };
  learning.Children.Add(ChapterBlock("样本从哪里来", "只对当前硬盘的性能计数器做时间序列学习。", Blue,
    "按设置中的性能采样间隔读取读 / 写 / IOPS / 队列 / 活跃度；负载基线实际使用写入速度，其余字段主要用于图表与上下文；默认每 1 秒。",
   "写入速度先变换为 y = ln(1 + W / 1,000,000)，压缩极端峰值，不把单位差异误当异常。",
    "启动后先收集 120 个有效写入样本；长时间空闲只清除连续事件状态，不会偷偷把下一次高负载改写成新基线；候选异常样本不写回基线。"));
  learning.Children.Add(ChapterBlock("如何判定负载事件", "这不是寿命学习，也不会跨硬盘比较。", Violet,
    "对最近最多 1800 个基线样本计算中位数、MAD 和 95 分位；1.4826×MAD 只是把稳健尺度换到近似标准差尺度。",
    "z = (当前 y − 中位数) / (1.4826×MAD)；同时 z > 3.5 且超过本盘 95 分位，才成为候选。",
    "E ← E + (clip(z, −4, 4) − 0.5) × Δt，E 限制在 0–40；候选需持续覆盖约 5 秒，并至少满足 ceil(5 / 采样间隔) 个连续点，且 E ≥ 8 才显示“写入高于近期基线”。",
    "间断超过 max(5 秒, 3×采样间隔) 会清零连续状态；长时间空闲只清除连续证据，保留同一硬盘的历史基线。",
    "样本目前只保存在内存，重启后重新学习。它只解释当前工作量，不参与寿命结论。"));
  body.Children.Add(learning);
  body.Children.Add(Text("章节 3 · 固定文件读写探针", 14, Ink));
  var probeExplain = new UniformGridShim { Columns = 2 };
  probeExplain.Children.Add(ChapterBlock("测试流程", "默认关闭；只有启用后手动或按计划运行。", Teal,
   "验证目标文件夹和可用空间（至少为测试文件大小的 2 倍），创建带归属标记的 .diskguard-probe 目录。",
   "使用固定种子生成相同的 1 MB 内容块，按顺序写入设定大小的固定文件并 Flush(true)。",
   "重新打开文件顺序读回，用 FNV-1a 校验和验证读回内容；写入 / 读取分别计时并换算为十进制 MB/s。",
   "测试完成、超时或异常都会尝试删除测试文件、标记和目录；发现无法确认归属的同名目录时不会删除。"));
  probeExplain.Children.Add(ChapterBlock("安全边界与偏差", "它是可控的实际读写，不是硬盘寿命或健康诊断。", Amber,
   "不读取用户已有的大文件，不做随机写、全盘扫描、自检或修复；固定文件内容和大小让不同日期的结果可复现。",
   "文件大小可设 32–512 MB，单次时限可设 10–120 秒；更大的文件和更高频率会增加真实写入量，请按需选择。",
   "结果仍会受缓存、温度、后台进程和剩余空间影响，因此只作为同机、同路径、相近环境下的趋势参考。",
   "计划测试默认“仅手动”；设置每小时、每 6 小时、每天或每周后，程序只在目标路径有效时运行。"));
  body.Children.Add(probeExplain);
  body.Children.Add(Text("章节 4 · 只读与边界", 14, Ink));
  body.Children.Add(Text("默认监测路径只读，不创建测试文件、不扫描文件内容、不自动修复；原生协议层只发送查询型 IOCTL，频域计算在内存中完成。健康字段缺失时显示未知，程序不会输出没有物理依据的剩余寿命百分比。", 11, Muted));
  panel.Children.Add(Card(body));
 }

 void ToggleFloating(bool show = false) {
  if(floating == null) floating = new FloatingPanel(this);
  if(show || !floating.IsVisible) {
   floating.Refresh();
   floating.Show();
   floating.Topmost = true;
   floating.Activate();
  } else floating.HidePanel();
 }

 void BeginHealthPoll(bool force = false) {
  if(healthTask != null && !healthTask.IsCompleted) return;
  if(!force && (DateTime.UtcNow - lastHealthPollStart).TotalMinutes < Math.Max(1, healthPollMinutes)) return;
  lastHealthPollStart = DateTime.UtcNow;
  healthTask = Task.Run(() => QueryHealth());
  healthTask.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => {
   if(t.Status == TaskStatus.RanToCompletion) health = t.Result;
   healthTask = null;
   RenderCurrent();
   if(floating != null && floating.IsVisible) floating.Refresh();
  })), TaskScheduler.Default);
 }

 string Ping0Summary() {
  if(!ping0Enabled) return "未启用 · 默认不访问 Ping0，也不会发送公网 IP";
  if(ping0Task != null && !ping0Task.IsCompleted && ping0.Status == "正在查询") return "正在查询 Ping0 节点信息…";
  var parts = new List<string>();
  if(!String.IsNullOrWhiteSpace(ping0.Status)) parts.Add(ping0.Status);
  if(!String.IsNullOrWhiteSpace(ping0.Ip)) parts.Add("IP " + MaskIp(ping0.Ip));
  if(!String.IsNullOrWhiteSpace(ping0.Location)) parts.Add(ping0.Location);
  if(!String.IsNullOrWhiteSpace(ping0.Asn)) parts.Add(ping0.Asn + (String.IsNullOrWhiteSpace(ping0.AsnName) ? "" : " " + ping0.AsnName));
  if(!String.IsNullOrWhiteSpace(ping0.IpRisk)) parts.Add("iprisk " + ping0.IpRisk + "（Ping0 原值）");
  if(!String.IsNullOrWhiteSpace(ping0.IsNative)) parts.Add(FlagText(ping0.IsNative, "原生 IP", "非原生 IP"));
  if(!String.IsNullOrWhiteSpace(ping0.IsIdc)) parts.Add(FlagText(ping0.IsIdc, "机房节点", "非机房节点"));
  if(!String.IsNullOrWhiteSpace(ping0.Org) && String.IsNullOrWhiteSpace(ping0.AsnName)) parts.Add(ping0.Org);
  if(ping0.Updated != DateTime.MinValue) parts.Add("更新 " + ping0.Updated.ToString("MM-dd HH:mm"));
  if(!String.IsNullOrWhiteSpace(ping0.Error)) {
   if(ping0.Error == "详细风险字段需要 API Key") parts.Add("风险值未采集 · 需 API Key");
   else parts.Add(ping0.Error);
  }
  return parts.Count == 0 ? "已启用 · 等待检测" : String.Join("  ·  ", parts);
 }

 Brush Ping0Brush() {
  if(!ping0Enabled) return Muted;
  if(ping0.Status == "正在查询") return Blue;
  if(ping0.Status == "请求失败" || ping0.Status == "查询失败" || ping0.Status == "节点地址无效" || ping0.Status == "公网 IP 无效") return Red;
  return ping0.HasRisk ? Violet : Teal;
 }

 Brush ProbeBrush() {
  if(!probeEnabled) return Muted;
  if(probe.Status == "测试中") return Blue;
  if(probe.Status == "测试完成" && probe.Verified) return Teal;
  if(probe.Status == "校验失败" || probe.Error.StartsWith("文件系统") || probe.Error.Contains("权限")) return Red;
  return Amber;
 }

 string ProbeScheduleText() {
  if(probeIntervalMinutes <= 0) return "仅手动";
  if(probeIntervalMinutes < 1440) return "每 " + (probeIntervalMinutes / 60.0).ToString("0.#") + " 小时";
  if(probeIntervalMinutes % 10080 == 0) return "每 " + (probeIntervalMinutes / 10080).ToString("0") + " 周";
  return "每 " + (probeIntervalMinutes / 1440).ToString("0") + " 天";
 }

 void RenderProbeVisuals() {
  string summary = probe.Summary;
  if(overviewProbe != null) { overviewProbe.Text = summary; overviewProbe.Foreground = ProbeBrush(); }
  if(probeStatusText != null) { probeStatusText.Text = summary; probeStatusText.Foreground = ProbeBrush(); }
 }

 void BeginProbeIfDue(bool force = false) {
  if(!probeEnabled) { if(force && probeStatusText != null) probeStatusText.Text = "请先启用探针功能"; return; }
  if(probeTask != null) return;
  var targets = probeTargets.Where(x => x.Enabled).Select(x => new ProbeTarget { Key=x.Key, Index=x.Index, Folder=x.Folder }).ToArray();
  if(targets.Length == 0) { if(force && probeStatusText != null) probeStatusText.Text = "请勾选至少一块硬盘"; return; }
  if(!force && probeIntervalMinutes <= 0) return;
  var now = DateTime.UtcNow;
  if(!force && (now - lastProbeStart).TotalMinutes < probeIntervalMinutes) return;
  lastProbeStart = now;
  string target = String.Join(" / ", targets.Select(x => "磁盘 " + x.Index));
  int size = probeSizeMb;
  int duration = probeMaxSeconds;
  probe = new ProbeResult { Status = "测试中", Target = target, Bytes = Math.Max(32, Math.Min(512, size)) * 1024L * 1024L };
  RenderProbeVisuals();
  probeTask = Task.Run(() => {
   foreach(var item in targets) {
    try {
     item.Result = TargetMatches(item, item.Folder, DiscoverProbeTargets()) ? RunProbe(item.Folder,size,duration) : ProbeFinish(new ProbeResult(),"未测试","路径不属于该硬盘，或映射不唯一/含目录联接");
    } catch { item.Result = ProbeFinish(new ProbeResult(),"未测试","无法核对硬盘与路径"); }
   }
   return new ProbeResult { Status = "批次完成", Error = String.Join("；", targets.Select(x => "磁盘 " + x.Index + "：" + x.Result.Summary)), Updated = DateTime.Now };
  });
  probeTask.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => {
   if(t.Status == TaskStatus.RanToCompletion && t.Result != null) probe = t.Result;
   else probe = new ProbeResult { Status = "未测试", Error = "后台任务未完成", Updated = DateTime.Now };
   probeTask = null;
   foreach(var item in targets) { var original = probeTargets.FirstOrDefault(x => x.Key == item.Key); if(original != null) original.Result = item.Result; }
   if(MainVisible) BuildProbeRows();
   RenderProbeVisuals();
   RenderCurrent();
  })), TaskScheduler.Default);
 }

 void RenderPing0Visuals() {
  string summary = Ping0Summary();
  if(overviewNode != null) { overviewNode.Text = summary; overviewNode.Foreground = Ping0Brush(); }
  if(ping0StatusText != null) { ping0StatusText.Text = summary; ping0StatusText.Foreground = Ping0Brush(); }
 }

 void BeginPing0Poll(bool force = false) {
  if(!ping0Enabled) return;
  if(ping0Task != null && !ping0Task.IsCompleted) return;
  var now = DateTime.UtcNow;
  if(!force && (now - lastPing0PollStart).TotalMinutes < ping0IntervalMinutes) return;
  lastPing0PollStart = now;
  string target = ping0TargetIp ?? "";
  string key = ping0ApiKey ?? "";
  ping0 = new Ping0Snapshot { Enabled = true, Status = "正在查询" };
  RenderPing0Visuals();
  ping0Task = Task.Run(() => QueryPing0(target, key));
  ping0Task.ContinueWith(t => Dispatcher.BeginInvoke(new Action(() => {
   if(t.Status == TaskStatus.RanToCompletion && t.Result != null) ping0 = t.Result;
   else { ping0 = new Ping0Snapshot { Enabled = true, Status = "查询失败", Error = "后台任务未完成", Updated = DateTime.Now }; }
   ping0Task = null;
   RenderPing0Visuals();
   RenderCurrent();
  })), TaskScheduler.Default);
 }

 void RenderCurrent() {
  if(MainVisible && currentPage == PageKind.Overview) RenderOverview();
  if(MainVisible && currentPage == PageKind.Details) RenderDetails();
  if(floating != null && floating.IsVisible) floating.Refresh();
 }

 void RenderOverview() {
  if(overviewRead == null) return;
  var values = drives.Values.ToArray();
  double totalRead = values.Sum(d => d.Read), totalWrite = values.Sum(d => d.Write);
  double totalIops = values.Sum(d => d.Iops), totalQueue = values.Sum(d => d.Queue);
  double active = values.Length == 0 ? 0 : values.Average(d => d.Active);
  overviewRead.Text = Rate(totalRead);
  overviewWrite.Text = Rate(totalWrite);
  overviewIops.Text = totalIops.ToString("0");
  overviewQueue.Text = totalQueue.ToString("0.0");
  overviewActive.Text = active.ToString("0") + "%";
  if(overviewNode != null) { overviewNode.Text = Ping0Summary(); overviewNode.Foreground = Ping0Brush(); }
  if(overviewProbe != null) { overviewProbe.Text = probe.Summary; overviewProbe.Foreground = ProbeBrush(); }
  if(overviewDisks == null) return;
  overviewDisks.Children.Clear();
  var facts = new List<string>();
  foreach(var d in values.OrderBy(x => x.Index)) {
   Health h; health.TryGetValue(d.Index, out h);
   var stack = new StackPanel();
   var head = new DockPanel();
   head.Children.Add(new TextBlock { Text = "磁盘 " + (d.Index >= 0 ? d.Index.ToString() : d.Name), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ink });
    var badge = new Border { Background = BadgeFill(h), CornerRadius = new CornerRadius(10), Padding = new Thickness(8,3,8,3), Child = new TextBlock { Text = h == null ? "采集中" : h.Severity, Foreground = h == null ? Muted : h.SeverityBrush, FontSize = 10 } };
   DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge); stack.Children.Add(head);
   stack.Children.Add(Text("↓ " + Rate(d.Read) + "   ↑ " + Rate(d.Write) + "   IOPS " + d.Iops.ToString("0") + "   队列 " + d.Queue.ToString("0.0") + "   活跃 " + d.Active.ToString("0") + "%", 12, Ink));
   stack.Children.Add(Text(options.Workload ? d.State : "负载判定已隐藏", 11, d.State.Contains("高于") ? Amber : Muted));
   stack.Children.Add(Text(h == null ? "型号 / 温度 / 磨损 / 错误：未采集" : h.Summary(), 10, Muted));
   overviewDisks.Children.Add(Card(stack, 18, new Thickness(16,13,16,7)));
   if(h != null) facts.Add("磁盘 " + d.Index + " · 寿命证据 " + h.LifetimeSeverity + " · 当前温度 " + h.ThermalRisk + " · " + h.Summary());
  }
  if(overviewHealth != null) overviewHealth.Text = facts.Count == 0 ? "暂未取得硬盘健康字段；缺失数据不会被解释成健康。" : String.Join(Environment.NewLine, facts.Distinct());
  var critical = values.Select(d => health.ContainsKey(d.Index) ? health[d.Index] : null).Any(h => h != null && h.Severity == "严重");
  if(overviewStatus != null) overviewStatus.Text = values.Length == 0 ? "等待性能计数器实例…" : (critical ? "发现严重健康证据，请优先备份重要数据。" : "每 " + performanceIntervalSeconds + " 秒刷新 · " + values.Length + " 块物理磁盘 · 负载异常不等于损坏");
 }

 void RenderDetails() { RenderDetailsView(); }

 void RefreshDetailsDriveSelector() {
  if(detailsDriveSelector == null) return;
  var ordered = drives.Values.OrderBy(x => x.Index).ToArray();
  updatingDetailsControls = true;
  bool rebuild = detailsDriveSelector.Items.Count != ordered.Length + 1;
  if(rebuild) {
   detailsDriveSelector.Items.Clear();
   detailsDriveSelector.Items.Add(new ComboBoxItem { Content = "全部硬盘（合计）", Tag = "" });
   foreach(var d in ordered) {
    Health h; health.TryGetValue(d.Index, out h);
    string model = h == null || String.IsNullOrWhiteSpace(h.Model) || h.Model == "未知" ? "型号采集中" : h.Model;
    detailsDriveSelector.Items.Add(new ComboBoxItem { Content = "磁盘 " + (d.Index >= 0 ? d.Index.ToString() : d.Name) + " · " + model, Tag = d.Name });
   }
  } else {
   for(int i = 0; i < ordered.Length; i++) {
    var item = detailsDriveSelector.Items[i + 1] as ComboBoxItem;
    if(item == null) continue;
    Health h; health.TryGetValue(ordered[i].Index, out h);
    string model = h == null || String.IsNullOrWhiteSpace(h.Model) || h.Model == "未知" ? "型号采集中" : h.Model;
    item.Content = "磁盘 " + (ordered[i].Index >= 0 ? ordered[i].Index.ToString() : ordered[i].Name) + " · " + model;
    item.Tag = ordered[i].Name;
   }
  }
  int selected = 0;
  if(!String.IsNullOrEmpty(selectedDetailsDriveName)) {
   for(int i = 0; i < detailsDriveSelector.Items.Count; i++) {
    var item = detailsDriveSelector.Items[i] as ComboBoxItem;
    if(item != null && (item.Tag as string) == selectedDetailsDriveName) { selected = i; break; }
   }
  }
  detailsDriveSelector.SelectedIndex = Math.Min(selected, Math.Max(0, detailsDriveSelector.Items.Count - 1));
  updatingDetailsControls = false;
 }

 List<Drive> SelectedDetailsDrives() {
  var all = drives.Values.OrderBy(x => x.Index).ToList();
  if(String.IsNullOrEmpty(selectedDetailsDriveName)) return all;
  var one = all.Where(x => x.Name == selectedDetailsDriveName).ToList();
  return one.Count == 0 ? all : one;
 }

 DockPanel DriveHeading(Drive d, Health h) {
  var heading = new DockPanel { LastChildFill = false, Margin = new Thickness(0,0,0,12) };
  heading.Children.Add(new TextBlock { Text = "磁盘 " + (d.Index >= 0 ? d.Index.ToString() : d.Name) + "   ·   " + (h == null ? "型号采集中" : h.Model), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ink });
  var badge = new Border { Background = BadgeFill(h), CornerRadius = new CornerRadius(10), Padding = new Thickness(8,3,8,3), Child = new TextBlock { Text = h == null ? "采集中" : h.Severity, Foreground = h == null ? Muted : h.SeverityBrush, FontSize = 10 } };
  DockPanel.SetDock(badge, Dock.Right); heading.Children.Add(badge);
  return heading;
 }

 Border DenseCard(UIElement child, double radius = 14, Thickness? padding = null) {
  var border = Card(child, radius, padding ?? new Thickness(10,7,10,6));
  border.Margin = new Thickness(0,0,6,6);
  return border;
 }

 Border InfoTile(string label, string value, Brush color) {
  // Identity and exposure are neutral facts; an unavailable value is never green.
  string[] neutral = { "型号", "介质", "总线", "容量", "固件", "序列号", "扇区大小", "分区数", "采样周期", "通电小时", "通电次数", "控制器忙", "NVMe 累计读取", "NVMe 累计写入", "主机读 / 写命令", "制造日期", "数据来源", "原生接口", "历史样本", "近期基线中位数", "基线 MAD（对数尺度）", "基线 95 分位", "寿命字段覆盖" };
  if(neutral.Contains(label)) color = Ink;
  if(new [] { "读取速度", "写入速度", "IOPS", "队列", "活跃", "设备温度上限", "启停次数", "载入卸载", "最大读 / 写延迟" }.Contains(label)) color = Ink;
  if(value == "未知" || value == "未识别介质" || value == "未知容量" || value == "学习中" || value == "未采集" || value == "未采集 / 未采集" || value == "未知 · 未采集") color = Muted;
  var stack = new StackPanel();
  stack.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Ink, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,5) });
  var valueText = ValueText(value, 13, color); valueText.Margin = new Thickness(0,1,0,0); valueText.TextWrapping = TextWrapping.Wrap;
  stack.Children.Add(valueText);
  return DenseCard(stack, 12, new Thickness(8,7,8,6));
 }

 void AddInfo(Panel panel, string label, string value, Brush color) { panel.Children.Add(InfoTile(label, value, color)); }

 static string BaselineRate(double value) {
  if(Double.IsNaN(value) || Double.IsInfinity(value)) return "学习中";
  try { return Rate(Math.Max(0, (Math.Exp(value) - 1) * 1000000.0)); } catch { return "学习中"; }
 }
 static string RobustValue(double value) { return Double.IsNaN(value) || Double.IsInfinity(value) ? "学习中" : value.ToString("0.00"); }
 static double Clamp(double value, double min, double max) { return Math.Min(max, Math.Max(min, value)); }

 Border MeterTile(string label, double? value, double max, string display, Brush color) {
  var stack = new StackPanel();
  var head = new DockPanel();
  head.Children.Add(Text(label, 10, Muted));
  var valueText = new TextBlock { Text = display, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = color, HorizontalAlignment = HorizontalAlignment.Right };
  DockPanel.SetDock(valueText, Dock.Right); head.Children.Add(valueText); stack.Children.Add(head);
  if(value.HasValue) {
   var bar = new ProgressBar { Minimum = 0, Maximum = max, Value = Clamp(value.Value, 0, max), Height = 7, Margin = new Thickness(0,5,0,0), Background = TrackFill, Foreground = color, BorderThickness = new Thickness(0) };
   stack.Children.Add(bar);
  } else stack.Children.Add(Text("暂无可用标定值", 9, Muted));
  return DenseCard(stack, 14, new Thickness(10,7,10,6));
 }

 void BuildRawView(List<Drive> selected) {
  detailsDataStack.Children.Add(HeadingWithHelp("原始数据", "设备直接报告值。名称、介质、总线等身份信息使用中性色；警示为橙色，明确正常状态为绿色，缺失数据为灰色。", 20));
  foreach(var d in selected) {
   Health h; health.TryGetValue(d.Index, out h);
   var stack = new StackPanel(); stack.Children.Add(DriveHeading(d, h));
   stack.Children.Add(HeadingWithHelp("瞬时运行状态", "每 " + performanceIntervalSeconds + " 秒采样。曲线颜色用于区分读写序列，不表示读写行为的好坏。", 17));
   var perf = new UniformGridShim { Columns = 6 };
    AddInfo(perf, "读取速度", Rate(d.Read), Blue); AddInfo(perf, "写入速度", Rate(d.Write), Teal); AddInfo(perf, "IOPS", d.Iops.ToString("0"), Ink); AddInfo(perf, "队列", d.Queue.ToString("0.00"), Violet); AddInfo(perf, "活跃", d.Active.ToString("0") + "%", Amber); AddInfo(perf, "采样周期", performanceIntervalSeconds + " s", Muted);
   stack.Children.Add(perf);
   stack.Children.Add(HeadingWithHelp("设备信息", "型号、介质、总线、容量等是中性属性；设备状态和错误字段在健康证据中展示。", 17));
   var identity = new UniformGridShim { Columns = 6 };
   if(h == null) {
    AddInfo(identity, "健康接口", "未采集", Muted);
    AddInfo(identity, "数据来源", "等待健康轮询", Muted);
   } else {
    AddInfo(identity, "型号", h.Model, Ink); AddInfo(identity, "介质", h.MediaLabel, Teal);
    AddInfo(identity, "总线", h.Bus, Teal); AddInfo(identity, "容量", Bytes(h.SizeBytes), Ink);
    AddInfo(identity, "固件", h.Firmware, Ink); AddInfo(identity, "序列号", h.SerialMasked, Muted);
    AddInfo(identity, "扇区大小", h.BytesPerSector.HasValue ? Count(h.BytesPerSector) + " B" : "未采集", Muted); AddInfo(identity, "分区数", Count(h.Partitions), Muted);
   }
   stack.Children.Add(identity);
   if(h != null) {
   stack.Children.Add(HeadingWithHelp("健康证据", "设备报告的磨损、备用空间、错误和警告。缺失字段不等于正常；历史累计错误也不等于当前正在发生错误。", 17));
   var lifetimeRaw = new UniformGridShim { Columns = 6 };
   AddInfo(lifetimeRaw, "设备状态", h.Status, h.Status == "正常" || h.Status == "OK" ? Teal : h.Status == "未知" ? Muted : Amber); AddInfo(lifetimeRaw, "SMART 预测失败", h.SmartFailed ? "是" : h.SmartKnown ? "否" : "未采集", h.SmartFailed ? Red : h.SmartKnown ? Teal : Muted);
   AddInfo(lifetimeRaw, "磨损百分比", OptionalNumber(h.Wear, "%"), !h.Wear.HasValue ? Muted : h.Wear.Value >= 90 ? Amber : Teal); AddInfo(lifetimeRaw, "备用空间", OptionalNumber(h.AvailableSpare, "%"), !h.AvailableSpare.HasValue ? Muted : h.AvailableSpareThreshold.HasValue && h.AvailableSpare.Value < h.AvailableSpareThreshold.Value ? Amber : Teal);
   AddInfo(lifetimeRaw, "介质错误", Count(h.MediaErrors), !h.MediaErrors.HasValue ? Muted : h.MediaErrors.Value > 0 ? Red : Teal); AddInfo(lifetimeRaw, "读 / 写错误", Count(h.ReadErrors) + " / " + Count(h.WriteErrors), !h.ReadErrors.HasValue && !h.WriteErrors.HasValue ? Muted : (h.ReadErrors ?? 0) + (h.WriteErrors ?? 0) > 0 ? Red : Teal);
   AddInfo(lifetimeRaw, "不可纠正读 / 写", Count(h.ReadErrorsUncorrected) + " / " + Count(h.WriteErrorsUncorrected), !h.ReadErrorsUncorrected.HasValue && !h.WriteErrorsUncorrected.HasValue ? Muted : (h.ReadErrorsUncorrected ?? 0) + (h.WriteErrorsUncorrected ?? 0) > 0 ? Red : Teal); AddInfo(lifetimeRaw, "不可纠正介质", Count(h.MediaErrorsUncorrected), !h.MediaErrorsUncorrected.HasValue ? Muted : h.MediaErrorsUncorrected.Value > 0 ? Red : Teal);
   AddInfo(lifetimeRaw, "NVMe 临界警告", HexByte(h.CriticalWarning), h.CriticalWarning.HasValue && (h.CriticalWarning.Value & 0x1D) != 0 ? Amber : Muted);
   stack.Children.Add(lifetimeRaw);
   stack.Children.Add(HeadingWithHelp("温度与累计记录", "温度描述当前热状态；通电时间和累计读写是使用记录。不安全关机非零以橙色提醒，但不直接换算寿命损失。", 17));
   var contextRaw = new UniformGridShim { Columns = 6 };
   AddInfo(contextRaw, "当前温度", OptionalNumber(h.Temperature, "°C"), h.ThermalRisk == "严重" ? Red : h.ThermalRisk == "关注" ? Amber : Ink); AddInfo(contextRaw, "设备温度上限", OptionalNumber(h.TemperatureMax, "°C"), Muted);
   AddInfo(contextRaw, "通电小时", Count(h.PowerOnHours) + (h.PowerOnHours.HasValue ? " h" : ""), Muted); AddInfo(contextRaw, "通电次数", Count(h.PowerCycleCount), Muted);
   AddInfo(contextRaw, "不安全关机", Count(h.UnsafeShutdowns), !h.UnsafeShutdowns.HasValue ? Muted : h.UnsafeShutdowns.Value > 0 ? Amber : Teal); AddInfo(contextRaw, "控制器忙", OptionalNumber(h.ControllerBusyMinutes, " min"), h.ControllerBusyMinutes.HasValue ? Ink : Muted);
   AddInfo(contextRaw, "NVMe 累计读取", Bytes(h.DataUnitsReadBytes), Muted); AddInfo(contextRaw, "NVMe 累计写入", Bytes(h.DataUnitsWrittenBytes), Muted);
   AddInfo(contextRaw, "主机读 / 写命令", Number(h.HostReadCommands) + " / " + Number(h.HostWriteCommands), Muted); AddInfo(contextRaw, "读 / 写已纠正", Count(h.ReadErrorsCorrected) + " / " + Count(h.WriteErrorsCorrected), Muted);
   AddInfo(contextRaw, "启停次数", Count(h.StartStopCycleCount) + " / 上限 " + Count(h.StartStopCycleCountMax), Muted); AddInfo(contextRaw, "载入卸载", Count(h.LoadUnloadCycleCount) + " / 上限 " + Count(h.LoadUnloadCycleCountMax), Muted);
   AddInfo(contextRaw, "最大读 / 写延迟", OptionalNumber(h.ReadLatencyMax, " ms") + " / " + OptionalNumber(h.WriteLatencyMax, " ms"), Muted); AddInfo(contextRaw, "错误日志条目", Number(h.ErrorLogEntries), h.ErrorLogEntries.HasValue && h.ErrorLogEntries.Value > 0 ? Amber : h.ErrorLogEntries.HasValue ? Teal : Muted);
   AddInfo(contextRaw, "制造日期", h.ManufactureDate, Muted);
   stack.Children.Add(contextRaw);
   var sourceRaw = new UniformGridShim { Columns = 2 };
   AddInfo(sourceRaw, "数据来源", h.SourceSummary(), Muted); AddInfo(sourceRaw, "原生接口", h.NativeSource, Muted);
   stack.Children.Add(sourceRaw);
    if(!String.IsNullOrWhiteSpace(h.NativeNote)) stack.Children.Add(HeadingWithHelp("接口帮助", h.NativeNote, 17));
   }
   detailsDataStack.Children.Add(Card(stack, 18, new Thickness(0,0,0,14)));
  }
 }

 void BuildDerivedView(List<Drive> selected) {
  detailsDataStack.Children.Add(HeadingWithHelp("计算指标", "使用本盘写入历史的对数变换、中位数和 MAD 计算负载偏移。详细公式和学习规则见评估依据。", 20));
  foreach(var d in selected) {
   Health h; health.TryGetValue(d.Index, out h);
   var stack = new StackPanel(); stack.Children.Add(DriveHeading(d, h));
   var grid = new UniformGridShim { Columns = 6 };
   AddInfo(grid, "负载状态（仅当前）", d.State, d.State.Contains("高于") ? Amber : Blue);
   AddInfo(grid, "稳健 z 分数（仅当前）", RobustValue(d.LastZ), Double.IsNaN(d.LastZ) ? Muted : (d.LastZ > 3.5 ? Amber : Ink));
   AddInfo(grid, "近期基线中位数", BaselineRate(d.BaselineMedian), Muted);
   AddInfo(grid, "基线 MAD（对数尺度）", RobustValue(d.BaselineMad), Muted);
   AddInfo(grid, "基线 95 分位", BaselineRate(d.BaselineP95), Muted);
    AddInfo(grid, "候选连续样本", d.Streak.ToString() + " / " + d.RequiredStreak, d.Streak >= d.RequiredStreak ? Amber : Teal);
    AddInfo(grid, "累积负载证据（秒归一，非寿命）", d.Sum.ToString("0.0") + " / 40", d.Sum >= 8 ? Amber : Teal);
   AddInfo(grid, "历史样本", d.History.Count.ToString("N0") + "（最多 1800）", Muted);
   if(h == null) AddInfo(grid, "寿命证据结论", "未知 · 未采集", Muted);
   else {
    AddInfo(grid, "寿命证据结论", h.LifetimeSeverity, h.LifetimeSeverity == "严重" ? Red : h.LifetimeSeverity == "关注" ? Amber : h.LifetimeSeverity == "正常" ? Teal : Muted);
    AddInfo(grid, "当前温度风险", h.ThermalRisk, h.ThermalRisk == "严重" ? Red : h.ThermalRisk == "关注" ? Amber : h.ThermalRisk == "正常" ? Teal : Muted);
    AddInfo(grid, "寿命字段覆盖", HealthCoverage(h), Muted);
   }
   stack.Children.Add(grid);
   if(h != null) {
   var meters = new UniformGridShim { Columns = 3 };
    meters.Children.Add(MeterTile("SSD 磨损（设备报告）", h.Wear, 100, OptionalNumber(h.Wear, "%"), !h.Wear.HasValue ? Muted : h.Wear.Value >= 90 ? Amber : Teal));
    meters.Children.Add(MeterTile("备用空间（设备报告）", h.AvailableSpare, 100, OptionalNumber(h.AvailableSpare, "%"), !h.AvailableSpare.HasValue ? Muted : h.AvailableSpareThreshold.HasValue && h.AvailableSpare.Value < h.AvailableSpareThreshold.Value ? Amber : Teal));
    if(h.Temperature.HasValue && h.TemperatureMax.HasValue) meters.Children.Add(MeterTile("温度 / 设备上限", h.Temperature, h.TemperatureMax.Value, h.Temperature.Value.ToString("0.0") + " / " + h.TemperatureMax.Value.ToString("0") + " °C", h.ThermalRisk == "严重" ? Red : h.ThermalRisk == "关注" ? Amber : Teal));
    else meters.Children.Add(InfoTile("温度（设备阈值未知）", OptionalNumber(h.Temperature, "°C"), h.Temperature.HasValue ? Ink : Muted));
    stack.Children.Add(meters);
   }
   stack.Children.Add(Text("长期磨损速率  未计算（需要跨重启的轻量快照；不会用今天的工作强度冒充寿命变化）", 10, Muted));
   detailsDataStack.Children.Add(Card(stack, 18, new Thickness(0,0,0,14)));
  }
  var formula = new StackPanel();
  formula.Children.Add(Text("为什么这样算", 13, Ink));
   formula.Children.Add(Text("① ln(1 + 写入速度 / 1,000,000) 压缩极端峰值；② 中位数代表本盘近期典型状态；③ MAD 对离群点稳健，乘 1.4826 后与标准差同尺度；④ E ← E + (clip(z, −4, 4) − 0.5) × Δt，并限制在 0–40；⑤ z > 3.5 且超过本盘 95 分位，并用连续样本覆盖约 5 秒（至少 ceil(5 / 采样间隔) 个点）才标记“高于近期基线”。这只是负载证据，不是硬盘寿命百分比。", 10, Muted));
  var formulaHelp = String.Join("\n", formula.Children.OfType<TextBlock>().Select(x => x.Text));
  detailsDataStack.Children.Add(HeadingWithHelp("计算帮助", formulaHelp));
  var mapping = new StackPanel();
  mapping.Children.Add(Text("指标如何被使用", 13, Ink));
  mapping.Children.Add(Text("性能采样 → 当前负载事件；温度 / 设备上限 → 当前热风险；通电、累计读写、异常关机 → 累计背景；磨损、备用空间、SMART、不可纠正错误 → 寿命证据。只有最后一类直接进入寿命结论，前两类不会因为今天工作量大就把寿命判低。", 10, Muted));
  detailsDataStack.Children.Add(HeadingWithHelp("指标用途", String.Join("\n", mapping.Children.OfType<TextBlock>().Select(x => x.Text))));
 }

 static string HealthCoverage(Health h) {
  if(h == null) return "0 项";
  int n = 0;
  if(h.Status != "未知") n++; if(h.SmartFailed) n++; if(h.Wear.HasValue) n++; if(h.AvailableSpare.HasValue) n++;
  if(h.MediaErrors.HasValue || h.ReadErrors.HasValue || h.WriteErrors.HasValue) n++;
  if(h.ReadErrorsUncorrected.HasValue || h.WriteErrorsUncorrected.HasValue || h.MediaErrorsUncorrected.HasValue) n++;
  if(h.CriticalWarning.HasValue) n++;
  return n.ToString() + " 项寿命证据字段";
 }

 List<string> LifetimeReasons(Health h) {
  var reasons = new List<string>();
  if(h == null || !h.HasLifetimeData) { reasons.Add("寿命相关字段不足，保持“未知”；缺失不是正常。 "); return reasons; }
  if(h.SmartFailed) reasons.Add("SMART 预测失败");
  if(h.Status != "未知" && h.Status != "正常") reasons.Add("设备状态报告为“" + h.Status + "”");
  if(h.CriticalWarning.HasValue && (h.CriticalWarning.Value & 0x1D) != 0) reasons.Add("NVMe 可靠性 / 备用空间 / 只读相关警告 0x" + h.CriticalWarning.Value.ToString("X2"));
  if(h.Wear.HasValue && h.Wear.Value >= 100) reasons.Add("磨损百分比达到设备上限");
  else if(h.Wear.HasValue && h.Wear.Value >= 90) reasons.Add("磨损百分比接近设备上限（≥90%）");
  if(h.AvailableSpare.HasValue && h.AvailableSpareThreshold.HasValue && h.AvailableSpare.Value < h.AvailableSpareThreshold.Value) reasons.Add("备用空间低于设备阈值");
  if((h.ReadErrorsUncorrected ?? 0) > 0 || (h.WriteErrorsUncorrected ?? 0) > 0 || (h.MediaErrorsUncorrected ?? 0) > 0) reasons.Add("存在不可纠正错误计数");
  else if((h.MediaErrors ?? 0) > 0 || (h.ReadErrors ?? 0) > 0 || (h.WriteErrors ?? 0) > 0) reasons.Add("存在可报告的介质 / 读写错误计数");
  if(reasons.Count == 0) reasons.Add("当前采集到的直接健康字段未触发风险规则");
  return reasons;
 }

 List<string> OperationalReasons(Health h, Drive d) {
  var reasons = new List<string>();
  if(h == null || h.ThermalRisk == "未知") reasons.Add("没有设备温度上限，当前温度只作记录，不用绝对阈值猜测风险");
  else if(h.ThermalRisk == "严重") reasons.Add("当前温度超过设备报告上限或触发温度警告");
  else if(h.ThermalRisk == "关注") reasons.Add("当前温度接近设备报告上限");
  else reasons.Add("当前温度未超过设备报告上限");
  if(d != null && d.State.Contains("高于")) reasons.Add("近期写入高于本盘基线；这是工作负载事件，不等于寿命下降");
  else if(d != null) reasons.Add("近期负载未触发持续异常规则");
  return reasons;
 }

 void BuildConclusionView(List<Drive> selected) {
  detailsDataStack.Children.Add(Text("结论", 18, Ink));
  detailsDataStack.Children.Add(Text("结论是规则筛选后的证据摘要，不是跨品牌、跨介质的寿命百分比。速度快慢不会抵消 SMART、温度或错误字段中的硬证据。", 11, Muted));
  foreach(var d in selected) {
   Health h; health.TryGetValue(d.Index, out h);
   string severity = h == null ? "未知" : h.Severity;
   Brush color = h == null ? Muted : h.SeverityBrush;
   var stack = new StackPanel(); stack.Children.Add(DriveHeading(d, h));
   var verdicts = new UniformGridShim { Columns = 2 };
   string life = h == null ? "未知" : h.LifetimeSeverity;
   string thermal = h == null ? "未知" : h.ThermalRisk;
   Brush lifeColor = life == "严重" ? Red : life == "关注" ? Amber : life == "正常" ? Teal : Muted;
   Brush thermalColor = thermal == "严重" ? Red : thermal == "关注" ? Amber : thermal == "正常" ? Teal : Muted;
   verdicts.Children.Add(InfoTile("寿命证据（参与寿命评估）", life + (h == null ? "" : " · " + HealthCoverage(h)), lifeColor));
   verdicts.Children.Add(InfoTile("当前运行风险（不等于寿命）", thermal, thermalColor));
   stack.Children.Add(verdicts);
   stack.Children.Add(Text("寿命结论依据", 12, Ink));
   foreach(var reason in LifetimeReasons(h)) stack.Children.Add(Text("• " + reason, 11, lifeColor == Red ? Red : Muted));
   stack.Children.Add(Text("当前状态依据", 12, Ink));
   foreach(var reason in OperationalReasons(h, d)) stack.Children.Add(Text("• " + reason, 11, thermalColor == Red ? Red : Muted));
   if(h != null) stack.Children.Add(Text("来源  " + h.SourceSummary(), 10, Muted));
   var card = Card(stack, 18, new Thickness(17,14,17,11));
   card.BorderBrush = new SolidColorBrush(Color.FromArgb(180, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B));
   detailsDataStack.Children.Add(card);
  }
  var note = new StackPanel();
  note.Children.Add(Text("解释边界", 13, Ink));
  note.Children.Add(Text("寿命证据只由设备健康 / 退化字段触发；当前温度和瞬时负载单独展示。正常 = 已采集的寿命字段未触发规则；未知 = 寿命字段不足；关注 / 严重 = 至少一个寿命证据触发规则。长期磨损趋势需要保存跨重启的摘要快照，当前不会凭单日负载推断寿命。", 10, Muted));
  detailsDataStack.Children.Add(Card(note, 16, new Thickness(15,12,15,8)));
 }

 class ChartLine {
  public string Label;
  public Brush Color;
  public List<double> Values;
  public string Unit;
  public List<double> Frequencies;
 }

 class SpectrumResult {
  public List<double> Frequencies = new List<double>();
  public List<double> Magnitudes = new List<double>();
 }

 List<MetricSample> ChartSeries(List<Drive> selected) {
  var result = new List<MetricSample>();
  if(selected == null || selected.Count == 0) return result;
  var arrays = selected.Select(x => x.SeriesSnapshot()).ToArray();
  int max = arrays.Max(x => x.Length);
  for(int i = 0; i < max; i++) {
   int fromEnd = max - 1 - i;
   var point = new MetricSample { Time = DateTime.UtcNow };
   int count = 0;
   foreach(var array in arrays) {
    int index = array.Length - 1 - fromEnd;
    if(index < 0 || index >= array.Length) continue;
    var p = array[index]; point.Time = p.Time; point.Read += p.Read; point.Write += p.Write; point.Iops += p.Iops; point.Queue += p.Queue; point.Active += p.Active; count++;
   }
   if(count > 0) result.Add(point);
  }
  return result;
 }

 void CanvasText(Canvas canvas, string value, double left, double top, Brush color, double size) {
  var text = new TextBlock { Text = value, FontSize = size, Foreground = color, IsHitTestVisible = false };
  Canvas.SetLeft(text, left); Canvas.SetTop(text, top); canvas.Children.Add(text);
 }

 SpectrumResult ComputeSpectrum(List<double> values, List<MetricSample> points) {
  var result = new SpectrumResult();
  if(values == null || values.Count < 8) return result;
  int n = 1; while(n * 2 <= Math.Min(values.Count, 128)) n *= 2;
  if(n < 8) return result;
  int start = values.Count - n;
  double dt = 1.0;
  var intervals = new List<double>();
  if(points != null) for(int i = Math.Max(1, start); i < points.Count; i++) {
   double seconds = (points[i].Time - points[i - 1].Time).TotalSeconds;
   if(seconds > .05 && seconds < 10) intervals.Add(seconds);
  }
  if(intervals.Count > 0) { var sortedIntervals = intervals.OrderBy(x => x).ToArray(); dt = sortedIntervals[sortedIntervals.Length / 2]; }
  double mean = values.Skip(start).Take(n).Average();
  var signal = new double[n];
  for(int i = 0; i < n; i++) {
   double centered = values[start + i] - mean;
   double window = n == 1 ? 1 : .5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1));
   signal[i] = centered * window;
  }
  for(int k = 1; k <= n / 2; k++) {
   double real = 0, imaginary = 0;
   for(int i = 0; i < n; i++) {
    double angle = 2 * Math.PI * k * i / n;
    real += signal[i] * Math.Cos(angle);
    imaginary -= signal[i] * Math.Sin(angle);
   }
   result.Frequencies.Add(k / (n * dt));
   result.Magnitudes.Add(2 * Math.Sqrt(real * real + imaginary * imaginary) / n);
  }
  return result;
 }

 StreamGeometry SmoothGeometry(List<Point> points) {
  var geometry = new StreamGeometry();
  if(points == null || points.Count == 0) return geometry;
  using(var ctx = geometry.Open()) {
   ctx.BeginFigure(points[0], false, false);
   for(int i = 0; i < points.Count - 1; i++) {
    Point p0 = i > 0 ? points[i - 1] : points[i];
    Point p1 = points[i];
    Point p2 = points[i + 1];
    Point p3 = i + 2 < points.Count ? points[i + 2] : p2;
    var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
    var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
    ctx.BezierTo(c1, c2, p2, true, true);
   }
  }
  try { geometry.Freeze(); } catch { }
  return geometry;
 }

 void RenderPerformanceChart() {
  if(!MainVisible) return;
  if(detailsChart == null) return;
  detailsChart.Children.Clear();
  var selected = SelectedDetailsDrives();
  var points = ChartSeries(selected);
  double width = detailsChart.ActualWidth; if(width < 100) width = 640;
  double height = detailsChart.ActualHeight; if(height < 100) height = 190;
  bool spectrum = chartDomain == ChartDomain.Frequency;
  if(detailsChartTitle != null) detailsChartTitle.Text = spectrum ? "频率谱 · 等待样本" : "动态性能曲线 · 等待采样";
  if(points.Count < (spectrum ? 8 : 2)) {
   CanvasText(detailsChart, selected.Count == 0 ? "等待发现硬盘…" : (spectrum ? "正在积累频率样本（至少需要 8 秒）…" : "正在积累动态样本（至少需要 2 秒）…"), 18, height / 2 - 10, Muted, 11);
   return;
  }
  var lines = new List<ChartLine>();
  if(chartReadCheck == null || chartReadCheck.IsChecked == true) {
   var line = new ChartLine { Label = "读取", Color = Blue, Values = points.Select(x => x.Read).ToList(), Unit = "MB/s" };
   if(spectrum) { var spec = ComputeSpectrum(line.Values, points); line.Values = spec.Magnitudes; line.Frequencies = spec.Frequencies; line.Unit = "Hz"; }
   if(!spectrum || line.Values.Count > 0) lines.Add(line);
  }
  if(chartWriteCheck == null || chartWriteCheck.IsChecked == true) {
   var line = new ChartLine { Label = "写入", Color = Teal, Values = points.Select(x => x.Write).ToList(), Unit = "MB/s" };
   if(spectrum) { var spec = ComputeSpectrum(line.Values, points); line.Values = spec.Magnitudes; line.Frequencies = spec.Frequencies; line.Unit = "Hz"; }
   if(!spectrum || line.Values.Count > 0) lines.Add(line);
  }
  if(chartIopsCheck != null && chartIopsCheck.IsChecked == true) {
   var line = new ChartLine { Label = "IOPS", Color = Ink, Values = points.Select(x => x.Iops).ToList(), Unit = "次/s" };
   if(spectrum) { var spec = ComputeSpectrum(line.Values, points); line.Values = spec.Magnitudes; line.Frequencies = spec.Frequencies; line.Unit = "Hz"; }
   if(!spectrum || line.Values.Count > 0) lines.Add(line);
  }
  if(chartQueueCheck != null && chartQueueCheck.IsChecked == true) {
   var line = new ChartLine { Label = "队列", Color = Violet, Values = points.Select(x => x.Queue).ToList(), Unit = "长度" };
   if(spectrum) { var spec = ComputeSpectrum(line.Values, points); line.Values = spec.Magnitudes; line.Frequencies = spec.Frequencies; line.Unit = "Hz"; }
   if(!spectrum || line.Values.Count > 0) lines.Add(line);
  }
  if(chartActiveCheck != null && chartActiveCheck.IsChecked == true) {
   var line = new ChartLine { Label = "活跃", Color = Amber, Values = points.Select(x => x.Active).ToList(), Unit = "%" };
   if(spectrum) { var spec = ComputeSpectrum(line.Values, points); line.Values = spec.Magnitudes; line.Frequencies = spec.Frequencies; line.Unit = "Hz"; }
   if(!spectrum || line.Values.Count > 0) lines.Add(line);
  }
  if(lines.Count == 0) { CanvasText(detailsChart, "请至少勾选一条曲线", 18, height / 2 - 10, Muted, 11); return; }
  bool normalized = spectrum || lines.Count > 1;
  double left = 48, right = 12, top = 16, bottom = 26, plotWidth = Math.Max(20, width - left - right), plotHeight = Math.Max(20, height - top - bottom);
  double singleMax = lines.Count == 1 && !spectrum ? lines[0].Values.Max() : 1;
  if(singleMax <= 0) singleMax = 1;
  double maxFrequency = spectrum && lines[0].Frequencies != null && lines[0].Frequencies.Count > 0 ? lines[0].Frequencies.Max() : 1;
  for(int i = 0; i <= 4; i++) {
   double y = top + plotHeight * i / 4.0;
   var grid = new Line { X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(55,121,132,148)), StrokeThickness = 1 };
   detailsChart.Children.Add(grid);
   string label = normalized ? (100 - i * 25).ToString("0") + (spectrum ? "%" : "") : (singleMax * (4 - i) / 4.0).ToString(singleMax >= 100 ? "0" : "0.0");
   CanvasText(detailsChart, label, 4, y - 8, Muted, 9);
  }
  if(spectrum) {
   int bins = lines[0].Values.Count;
   double groupWidth = plotWidth / Math.Max(1, bins);
   for(int lineIndex = 0; lineIndex < lines.Count; lineIndex++) {
    var line = lines[lineIndex];
    double hi = line.Values.Count == 0 ? 0 : line.Values.Max(); if(hi <= 0) hi = 1;
    for(int i = 0; i < line.Values.Count; i++) {
     double normalizedValue = line.Values[i] * 100 / hi;
     double barWidth = Math.Max(1, groupWidth / lines.Count * .72);
     var bar = new Rectangle { Width = barWidth, Height = plotHeight * Clamp(normalizedValue, 0, 100) / 100.0, Fill = line.Color, Opacity = .62 };
     Canvas.SetLeft(bar, left + groupWidth * i + groupWidth * lineIndex / lines.Count + (groupWidth / lines.Count - barWidth) / 2);
     Canvas.SetTop(bar, top + plotHeight - bar.Height); detailsChart.Children.Add(bar);
    }
   }
   CanvasText(detailsChart, "0 Hz", left, height - 19, Muted, 9);
   CanvasText(detailsChart, maxFrequency.ToString("0.###") + " Hz", width - 65, height - 19, Muted, 9);
  } else {
   for(int lineIndex = 0; lineIndex < lines.Count; lineIndex++) {
    var line = lines[lineIndex];
    var pointList = new List<Point>();
    double lo = line.Values.Min(), hi = line.Values.Max();
    for(int i = 0; i < line.Values.Count; i++) {
     double value = line.Values[i];
     double normalizedValue = normalized ? (hi - lo <= 1e-9 ? 50 : (value - lo) * 100 / (hi - lo)) : value * 100 / singleMax;
     pointList.Add(new Point(left + plotWidth * i / Math.Max(1, line.Values.Count - 1), top + plotHeight * (100 - Clamp(normalizedValue, 0, 100)) / 100.0));
    }
    detailsChart.Children.Add(new System.Windows.Shapes.Path { Data = SmoothGeometry(pointList), Stroke = line.Color, StrokeThickness = 2.6, Opacity = .92, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
    var last = pointList[pointList.Count - 1];
    var dot = new Ellipse { Width = 7, Height = 7, Fill = line.Color, Opacity = .95 };
    Canvas.SetLeft(dot, last.X - 3.5); Canvas.SetTop(dot, last.Y - 3.5); detailsChart.Children.Add(dot);
   }
   CanvasText(detailsChart, points[0].Time.ToLocalTime().ToString("HH:mm:ss"), left, height - 19, Muted, 9);
   CanvasText(detailsChart, points[points.Count - 1].Time.ToLocalTime().ToString("HH:mm:ss"), width - 65, height - 19, Muted, 9);
  }
  double legendLeft = left + 8;
  foreach(var line in lines) {
   string latest;
   if(spectrum) {
    int peak = 0; for(int i = 1; i < line.Values.Count; i++) if(line.Values[i] > line.Values[peak]) peak = i;
    latest = "峰值 " + (line.Frequencies == null || line.Frequencies.Count == 0 ? "—" : line.Frequencies[peak].ToString("0.###") + " Hz");
   } else latest = line.Unit == "MB/s" ? Rate(line.Values[line.Values.Count - 1]) : line.Unit == "%" ? line.Values[line.Values.Count - 1].ToString("0") + "%" : line.Values[line.Values.Count - 1].ToString(line.Unit == "长度" ? "0.00" : "0");
   CanvasText(detailsChart, "● " + line.Label + " " + latest, legendLeft, 0, line.Color, 9);
   legendLeft += 105;
  }
  if(detailsChartTitle != null) detailsChartTitle.Text = spectrum ? "频率谱 · 主峰频率（每条指标独立归一化）" : "平滑时域趋势 · " + (normalized ? "多指标独立归一化" : lines[0].Label + "（" + lines[0].Unit + "）");
 }

 void RenderDetailsView() {
  if(detailsDataStack == null) return;
  RefreshDetailsDriveSelector();
  var selected = SelectedDetailsDrives();
  if(detailsSelectionHint != null) detailsSelectionHint.Text = selected.Count == 0 ? "尚未发现物理磁盘" : "已选 " + selected.Count + " 块硬盘 · 原始字段不做跨盘比较";
  if(detailsDataStack == null) return;
  detailsDataStack.Children.Clear();
  if(drives.Count == 0) {
   detailsDataStack.Children.Add(Card(Text("正在连接性能计数器…", 13, Muted)));
   if(detailsChartTitle != null) detailsChartTitle.Text = "动态性能曲线 · 等待采样";
   RenderPerformanceChart(); return;
  }
  if(selectedDataView == DataViewKind.Raw) BuildRawView(selected);
  else if(selectedDataView == DataViewKind.Derived) BuildDerivedView(selected);
  else BuildConclusionView(selected);
  if(detailsChartCard != null) detailsChartCard.Visibility = selectedDataView == DataViewKind.Conclusion ? Visibility.Collapsed : Visibility.Visible;
  RenderPerformanceChart();
 }

 static string FormatBool(bool value) { return value ? "1" : "0"; }

 async Task Sample() {
  if(busy) return;
  busy = true;
  string error = null;
  try {
   await Task.Run(() => {
    try {
     if(drives.Count == 0) {
      var category = new PerformanceCounterCategory("PhysicalDisk");
      foreach(var name in category.GetInstanceNames().Where(x => x != "_Total")) if(!drives.ContainsKey(name)) drives[name] = new Drive(name);
     }
      foreach(var drive in drives.Values) drive.Sample(performanceIntervalSeconds);
    } catch(Exception ex) { error = ex.Message; }
   });
   RenderCurrent();
   if(currentPage == PageKind.Overview && overviewStatus != null && error != null) overviewStatus.Text = "计数器不可用：" + error;
   bool activeDisk = drives.Values.Any(d => d.Read > 0 || d.Write > 0 || d.Active > 0.5);
   if(activeDisk) BeginHealthPoll();
   BeginPing0Poll();
   BeginProbeIfDue();
  } finally { busy = false; }
 }

 [STAThread]
 public static void Main(string[] args) {
  var app = new Application();
   var win = new Guard();
   app.SessionEnding += (s,e) => win.exitRequested = true;
   if(args.Contains("--preview-dark")) { win.themeMode = ThemeMode.Dark; win.ApplyThemePalette(); win.RebuildShellForTheme(); }
   if(args.Contains("--preview-light")) { win.themeMode = ThemeMode.Light; win.ApplyThemePalette(); win.RebuildShellForTheme(); }
  if(args.Contains("--preview-details")) win.Loaded += (s,e) => win.BuildPage(PageKind.Details);
  if(args.Contains("--preview-details-raw")) win.Loaded += (s,e) => { win.BuildPage(PageKind.Details); if(win.detailsChartCard != null) win.detailsChartCard.Visibility = Visibility.Collapsed; };
  if(args.Contains("--preview-evidence")) win.Loaded += (s,e) => win.BuildPage(PageKind.Evidence);
  if(args.Contains("--preview-settings")) win.Loaded += (s,e) => { win.BuildPage(PageKind.Settings); if(args.Contains("--preview-settings-open")) win.Dispatcher.BeginInvoke(new Action(() => { if(win.themeSelector != null) win.themeSelector.IsDropDownOpen = true; })); };
  if(args.Contains("--preview-ping0")) win.Loaded += (s,e) => { win.ping0Enabled = true; win.lastPing0PollStart = DateTime.UtcNow; win.ping0 = new Ping0Snapshot { Enabled = true, Status = "风险字段已取得", Ip = "203.0.113.42", Location = "示例地区", Asn = "AS64500", AsnName = "Example ISP", Org = "Example Network", IpRisk = "5", IsNative = "true", IsIdc = "false", AsnType = "isp", OrgType = "isp", Updated = DateTime.Now }; win.BuildPage(PageKind.Overview); };
  if(args.Contains("--preview-frequency")) win.Loaded += (s,e) => { win.BuildPage(PageKind.Details); win.chartDomain = ChartDomain.Frequency; if(win.chartDomainSelector != null) win.chartDomainSelector.SelectedIndex = 1; win.RenderPerformanceChart(); };
  if(args.Contains("--preview")) {
   win.Loaded += (s,e) => {
    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(args.Contains("--preview-frequency") ? 10 : 3) };
    t.Tick += (s2,e2) => {
     t.Stop();
     var bitmap = new RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, PixelFormats.Pbgra32);
     bitmap.Render(win);
     var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
     using(var file = File.Create(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"))) png.Save(file);
     win.Close();
    };
    t.Start();
   };
  }
  app.Run(win);
 }
}
