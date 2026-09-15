using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

public partial class Guard {
 // One registry drives the full view, floating view and persisted selectors.
 // Layers describe provenance; health/temperature/workload describe purpose.
 class MetricDefinition {
  public string Id, Title, Layer, Section, Legacy, Help;
  public bool NeedsPerformance, NeedsHealth;
  public Func<Drive,Health,string> Read;
 }
 readonly Dictionary<string,bool> metricChoices = new Dictionary<string,bool>();
 readonly Dictionary<string,bool> floatingGroups = new Dictionary<string,bool>();
 // Public defaults contain selection IDs only, never user paths, addresses or credentials.
 static readonly HashSet<string> RecommendedMetrics = new HashSet<string> {
  "read", "write", "raw_Temperature", "raw_Wear", "raw_UnsafeShutdowns",
  "health", "workload", "node_ip", "node_location"
 };
 List<MetricDefinition> metricCatalog;
 const string RawLayer = "原始数据", DerivedLayer = "计算指标", ConclusionLayer = "结论", ExtraLayer = "附加功能";
 const string LayerHelp = "分类以本软件为边界：系统/设备直接返回的字段属于原始数据（即使系统内部已计算）；本软件加工出的数值属于计算指标；按阈值或规则作出的状态判断属于结论。单位换算、格式化不改变类别。数值编码的风险等级仍是结论。缺失/过期是数据可用性说明，不是硬盘状态。";

 List<MetricDefinition> Metrics() {
  if(metricCatalog != null) return metricCatalog;
  var list = new List<MetricDefinition>();
  Action<string,string,string,string,string,Func<Drive,Health,string>,bool,bool,string> add = (id,title,layer,section,legacy,read,perf,healthData,help) =>
   list.Add(new MetricDefinition { Id=id,Title=title,Layer=layer,Section=section,Legacy=legacy,Read=read,NeedsPerformance=perf,NeedsHealth=healthData,Help=help });
  add("read","读取速度",RawLayer,"性能计数器","read",(d,h)=>Observed(d.Read/1000000,d.ReadValid,"0.0"," MB/s"),true,false,"Windows 直接返回的区间读取速率；这里只换算显示单位。");
  add("write","写入速度",RawLayer,"性能计数器","write",(d,h)=>Observed(d.Write/1000000,d.WriteValid,"0.0"," MB/s"),true,false,"Windows 直接返回的区间写入速率。");
  add("iops","IOPS",RawLayer,"性能计数器","iops",(d,h)=>Observed(d.Iops,d.IopsValid,"0",""),true,false,"区间每秒完成的读写请求数，由系统计数器提供。");
  add("queue","瞬时队列长度",RawLayer,"性能计数器","queue",(d,h)=>Observed(d.Queue,d.QueueValid,"0.00",""),true,false,"一次采样的未完成请求数，不是时间平均队列长度。");
  add("idle","空闲时间占比",RawLayer,"性能计数器",null,(d,h)=>Observed(d.Idle,d.ActiveValid,"0.0","%"),true,false,"Windows % Idle Time 原始返回值。");
  Action<string,string,string,string> field = (name,title,section,legacy) => {
   FieldInfo info=typeof(Health).GetField(name);
   if(info == null) throw new InvalidOperationException("Unknown health field: "+name);
   add("raw_"+name,title,RawLayer,section,legacy,(d,h)=>FormatSourceValue(name,info.GetValue(h)),false,true,"设备/系统报告字段；字段本身不是本程序的综合评价。展开评估依据查看用途与边界。");
  };
  field("Model","型号","设备身份",null); field("Media","介质（系统报告）","设备身份",null); field("Bus","总线","设备身份",null);
  field("SizeBytes","容量","设备身份",null); field("Firmware","固件","设备身份",null);
  add("raw_Serial","序列号（脱敏）",RawLayer,"设备身份",null,(d,h)=>h.SerialMasked,false,true,"只展示序列号末尾，不改变原始数据的归属。");
  field("BytesPerSector","扇区大小","设备身份",null); field("Partitions","分区数","设备身份",null); field("ManufactureDate","制造日期","设备身份",null);
  field("Status","设备报告状态","健康与错误原始字段",null);
  add("raw_Smart","SMART 预测失败",RawLayer,"健康与错误原始字段",null,(d,h)=>h.SmartFailed ? "是" : h.SmartKnown ? "否" : "无法获取",false,true,"来自设备的判定原样展示；本程序综合健康结论在结论层。");
  field("Wear","磨损百分比（设备报告）","健康与错误原始字段","wear");
  field("AvailableSpare","备用空间","健康与错误原始字段",null); field("AvailableSpareThreshold","备用空间阈值","健康与错误原始字段",null);
  field("CriticalWarning","NVMe 临界警告位","健康与错误原始字段",null); field("MediaErrors","介质错误累计","健康与错误原始字段","errors");
  field("ReadErrors","读取错误累计","健康与错误原始字段",null); field("WriteErrors","写入错误累计","健康与错误原始字段",null);
  field("ReadErrorsCorrected","已纠正读取错误","健康与错误原始字段",null); field("WriteErrorsCorrected","已纠正写入错误","健康与错误原始字段",null);
  field("ReadErrorsUncorrected","不可纠正读取错误","健康与错误原始字段",null); field("WriteErrorsUncorrected","不可纠正写入错误","健康与错误原始字段",null);
  field("MediaErrorsUncorrected","不可纠正介质错误","健康与错误原始字段",null); field("ErrorLogEntries","错误日志条目累计","健康与错误原始字段",null);
  field("Temperature","当前温度","温度与使用记录","temperature"); field("TemperatureMax","设备温度上限","温度与使用记录",null);
  field("PowerOnHours","通电小时","温度与使用记录","power_on_hours"); field("PowerCycleCount","通电次数","温度与使用记录",null);
  field("UnsafeShutdowns","不安全关机次数","温度与使用记录",null); field("ControllerBusyMinutes","控制器忙碌累计","温度与使用记录",null);
  field("DataUnitsReadBytes","累计读取量","温度与使用记录","endurance"); field("DataUnitsWrittenBytes","累计写入量","温度与使用记录","endurance");
  field("HostReadCommands","主机读取命令累计","温度与使用记录",null); field("HostWriteCommands","主机写入命令累计","温度与使用记录",null);
  field("StartStopCycleCount","启停次数","温度与使用记录",null); field("StartStopCycleCountMax","启停次数上限","温度与使用记录",null);
  field("LoadUnloadCycleCount","载入卸载次数","温度与使用记录",null); field("LoadUnloadCycleCountMax","载入卸载上限","温度与使用记录",null);
  field("ReadLatencyMax","最大读取延迟","温度与使用记录",null); field("WriteLatencyMax","最大写入延迟","温度与使用记录",null); field("FlushLatencyMax","最大刷新延迟","温度与使用记录",null);
  add("raw_Source","数据来源",RawLayer,"来源与接口",null,(d,h)=>h.SourceSummary(),false,true,"来源元数据，不参与风险评分。");
  field("NativeSource","原生接口","来源与接口",null); field("NativeNote","接口说明","来源与接口",null);
  foreach(int attributeId in new [] {1,4,5,7,9,10,12,187,188,190,192,193,194,196,197,198,199}) {
   byte id=(byte)attributeId;
   string hint=id==5?"常见重映射":id==197?"常见待处理扇区":id==198?"常见离线不可纠正":id==199?"常见接口 CRC":id==194||id==190?"常见温度编码":id==9?"常见通电时间":id==193?"常见载入卸载":"厂商定义";
   add("ata_"+id,"ATA "+id+" · "+hint,RawLayer,"ATA SMART 原始属性",null,(d,h)=>{AtaAttribute a;return h.Ata.Attributes.TryGetValue(id,out a)?a.Display:"未返回 / 接口未支持";},false,true,"常见名称仅供识别，不保证厂商采用相同含义或单位。展示当前/最差规范化值、设备阈值和完整 48 位 RAW；不会把 RAW 直接换算成寿命。");
  }
  add("ata_source","ATA 采集来源",RawLayer,"ATA SMART 原始属性",null,(d,h)=>h.Ata.Source+" · "+h.Ata.Note,false,true,"WMI 只读 SMART 与 SATA/ATA 只读透传；USB/SAS/RAID 桥接未覆盖时明确说明。");
  add("ata_attributes","全部 ATA 属性",RawLayer,"ATA SMART 原始属性",null,(d,h)=>h.Ata.Valid?String.Join("\n",h.Ata.Attributes.Values.OrderBy(a=>a.Id).Select(a=>a.Id+": "+a.Display)):"未返回 / 接口未支持",false,true,"完整属性列表，未识别的属性保留原始编码，不猜测单位或用途。");
  foreach(var f in SpecFields) {
   var specField=f;
   add("manual_"+f.Id,"手填 · "+f.Title,RawLayer,"手填标称输入 · 非设备实测",null,(d,h)=>ManualValue(h,specField.Id),false,false,"来自用户填写，未经核验；单位："+f.Unit+"。不会覆盖设备报告值。");
   add("reference_"+f.Id,f.Title+"对标值",DerivedLayer,"手填参照计算 · 非寿命",null,(d,h)=>ManualCalculation(d,h,specField.Id),false,false,"只在所需字段齐全时计算。速率比例不等于容量利用率；次数和 TBW 比例不等于磨损或故障概率。温度差值用 °C 表示。");
  }
  add("manual_source","手填资料来源",RawLayer,"手填标称输入 · 非设备实测",null,(d,h)=>ManualValue(h,"source"),false,false,"用户注明的资料，软件不会自动访问或核验。");
  add("manual_thermal_state","手填温度范围判断",ConclusionLayer,"手填参照 · 独立于设备告警",null,(d,h)=>ManualTemperatureState(h),false,false,"允许只填写上界或下界；判断只覆盖已填写的边界，不替代设备告警。");

  add("active","忙碌占比",DerivedLayer,"区间计算","active",(d,h)=>Observed(d.Active,d.ActiveValid,"0.0","%"),true,false,"100% − 空闲时间占比。不等于带宽利用率，也不是健康判断。");
  add("throughput","总吞吐量",DerivedLayer,"区间计算",null,(d,h)=>Observed((d.Read+d.Write)/1000000,d.ReadValid&&d.WriteValid,"0.0"," MB/s"),true,false,"读取速率 + 写入速率。");
  add("write_share","写入流量占比",DerivedLayer,"区间计算",null,(d,h)=>!d.ReadValid||!d.WriteValid ? "无法获取" : d.Read+d.Write<=0 ? "不适用：无读写流量" : (100*d.Write/(d.Read+d.Write)).ToString("0.0")+"%",true,false,"写入速率 / 总吞吐量；无流量时不能补成 0%。");
  add("request_size","平均请求大小",DerivedLayer,"区间计算",null,(d,h)=>!d.ReadValid||!d.WriteValid||!d.IopsValid ? "无法获取" : d.Iops<=0 ? "不适用：无已完成请求" : ((d.Read+d.Write)/d.Iops/1000).ToString("0.0")+" kB",true,false,"总吞吐量 / IOPS；组合计数器是区间估计，不区分顺序/随机请求。");
  add("temperature_margin","温度余量",DerivedLayer,"设备参照差值",null,(d,h)=>!h.Temperature.HasValue ? "无法获取温度" : !h.TemperatureMax.HasValue ? "阈值未知" : (h.TemperatureMax.Value-h.Temperature.Value).ToString("0.0")+" °C",false,true,"设备温度上限 − 当前温度。差值是计算指标，是否越限属于结论。");
  add("spare_margin","备用空间余量",DerivedLayer,"设备参照差值",null,(d,h)=>!h.AvailableSpare.HasValue ? "无法获取" : !h.AvailableSpareThreshold.HasValue ? "阈值未知" : (h.AvailableSpare.Value-h.AvailableSpareThreshold.Value).ToString("0.0")+" 百分点",false,true,"备用空间百分比 − 设备阈值百分比。");
  add("baseline_median","写入基线中位数",DerivedLayer,"近期统计",null,(d,h)=>BaselineRate(d.BaselineMedian),true,false,"仅本次运行有效写入样本的对数中位数，反变换后展示。");
  add("baseline_mad","基线 MAD（对数）",DerivedLayer,"近期统计",null,(d,h)=>RobustValue(d.BaselineMad),true,false,"对数写入样本相对中位数的绝对偏差中位数。");
  add("baseline_p95","写入基线 P95",DerivedLayer,"近期统计",null,(d,h)=>BaselineRate(d.BaselineP95),true,false,"近期有效写入样本的 95 分位，不是设备容量上限。");
  add("z","稳健 z 分数",DerivedLayer,"近期统计",null,(d,h)=>RobustValue(d.LastZ),true,false,"(对数写入 − 中位数) / (1.4826 × MAD)。数值本身不代表故障概率。");
  add("samples","有效写入样本数",DerivedLayer,"近期统计",null,(d,h)=>d.History.Count+" / 1800",false,false,"程序对有效样本计数；仅内存保存。不是设备原始字段。");
  add("streak","候选连续样本数",DerivedLayer,"规则中间量",null,(d,h)=>d.Streak.ToString(),true,false,"对满足候选规则的样本计数。可复算的中间统计量，不是最终状态。");
  add("evidence","累计偏移统计量",DerivedLayer,"规则中间量",null,(d,h)=>d.Sum.ToString("0.0")+" / 40",true,false,"规则累积的数值统计量，非耐久度、风险概率或剩余寿命。");
  add("coverage","健康字段覆盖数",DerivedLayer,"数据可用性",null,(d,h)=>HealthCoverage(h),false,true,"程序对可用字段计数，不是置信度。");

  add("health","健康证据结论",ConclusionLayer,"设备阈值与告警","health",(d,h)=>h.LifetimeSeverity=="未知" ? "证据不足，无法评价" : h.LifetimeSeverity,false,true,"依据设备报告与错误记录综合判断。未见告警不保证全部健康；历史错误不代表当前新增。");
  add("thermal","温度状态",ConclusionLayer,"设备阈值与告警",null,(d,h)=>h.ThermalRisk=="未知" ? "温度或设备阈值不足" : h.ThermalRisk=="关注" ? "达到或超过设备温度上限" : h.ThermalRisk=="严重" ? "设备报告温度告警" : "未超过设备温度上限",false,true,"只使用明确设备阈值或协议温度告警，不用摄氏温度比例判断。");
  add("workload","近期写入状态",ConclusionLayer,"相对参照","workload",(d,h)=>d.WriteValid ? d.State : "无法获取写入数据",true,false,"只反映近期写入偏移；学习不足时不判断，无写入不等于整盘空闲。");
  add("bottleneck","性能瓶颈",ConclusionLayer,"证据不足时暂停",null,(d,h)=>"无法判定：缺少区间延迟和同类负载参照",false,false,"不以固定 MB/s、IOPS 或队列长度门槛判断不同硬盘。");
  add("longterm","长期退化趋势",ConclusionLayer,"证据不足时暂停",null,(d,h)=>"样本不足：尚无跨重启可比快照",false,false,"不把近期工作量或一次探针速度换算成剩余寿命。");

  add("node_risk","节点风控值",ExtraLayer,"网络节点 · 非硬盘",null,(d,h)=>ping0.HasRisk ? ping0.IpRisk+" / 100" : "无法获取："+ping0.Status,false,false,"第三方 Ping0 评分，与硬盘监测无关；勾选只控制显示，不会启用查询或付费。");
  add("node_ip","节点 IP",ExtraLayer,"网络节点 · 非硬盘",null,(d,h)=>String.IsNullOrWhiteSpace(ping0.Ip) ? "无法获取" : ping0.Ip,false,false,"本程序的网络出口，未必等同其他应用的出口。");
  add("node_location","节点位置",ExtraLayer,"网络节点 · 非硬盘",null,(d,h)=>String.IsNullOrWhiteSpace(ping0.Location) ? "无法获取" : ping0.Location,false,false,"第三方定位。");
  add("node_status","节点查询状态",ExtraLayer,"网络节点 · 非硬盘",null,(d,h)=>ping0.Status,false,false,"查询成功/失败不是硬盘或节点安全判断。");
  foreach(string fieldName in new [] {"Asn","AsnName","Org","IsIdc","IsNative","AsnType","OrgType"}) {
   string name=fieldName;
   string title=name=="Asn"?"节点 ASN":name=="AsnName"?"ASN 名称":name=="Org"?"节点组织":name=="IsIdc"?"节点机房属性":name=="IsNative"?"节点原生属性":name=="AsnType"?"ASN 类型":"组织类型";
   add("node_"+name,title,ExtraLayer,"网络节点 · 非硬盘",null,(d,h)=>{ var value=(string)typeof(Ping0Snapshot).GetField(name).GetValue(ping0);return String.IsNullOrWhiteSpace(value)?"无法获取":value;},false,false,"第三方返回的网络属性，不参与硬盘评价。");
  }
  add("probe_status","文件探针状态",ExtraLayer,"主动测试工具 · 非被动监测",null,(d,h)=>probe.Summary,false,false,"独立主动测试工具；显示不会启用或安排测试，结果不冒充每块盘的监测数据。");
  metricCatalog=list; return list;
 }

 static string FormatSourceValue(string name, object value) {
  if(value == null) return "无法获取";
  if(value is string) return String.IsNullOrWhiteSpace((string)value) ? "无法获取" : (string)value;
  double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);
  if(Double.IsNaN(number)||Double.IsInfinity(number)) return "无法获取";
  if(name=="SizeBytes"||name=="DataUnitsReadBytes"||name=="DataUnitsWrittenBytes") return Bytes(number);
  if(name=="CriticalWarning") return "0x"+((byte)value).ToString("X2");
  if(name=="Temperature"||name=="TemperatureMax") return number.ToString("0.0")+" °C";
  if(name=="Wear"||name=="AvailableSpare"||name=="AvailableSpareThreshold") return number.ToString("0.0")+"%";
  string unit=name=="PowerOnHours" ? " h" : name=="ControllerBusyMinutes" ? " min" : name.EndsWith("LatencyMax") ? " ms" : name=="BytesPerSector" ? " B" : "";
  return number.ToString("N0")+unit;
 }
 bool MetricSelected(string id) {
  bool selected;
  if(metricChoices.TryGetValue(id,out selected)) return selected;
  var m=Metrics().FirstOrDefault(x=>x.Id==id);
  return m!=null && (m.Legacy!=null ? GetOption(m.Legacy) : RecommendedMetrics.Contains(id));
 }
 IEnumerable<string> MetricChoiceLines() { return metricChoices.OrderBy(x=>x.Key).Select(x=>"metric_"+x.Key+"="+(x.Value?"1":"0")); }
 string MetricValue(MetricDefinition m, Drive d, Health h) {
  if(h!=null && h.Bus=="NVMe" && m.Id.StartsWith("ata_"))return "设备协议不适用（NVMe）";
  if(h!=null && h.MediaLabel=="HDD" && new [] {"raw_Wear","raw_AvailableSpare","raw_AvailableSpareThreshold","raw_CriticalWarning","raw_DataUnitsReadBytes","raw_DataUnitsWrittenBytes","raw_HostReadCommands","raw_HostWriteCommands","raw_ControllerBusyMinutes","raw_UnsafeShutdowns","raw_ErrorLogEntries","spare_margin"}.Contains(m.Id))return "本接口不适用（SSD/NVMe 字段）";
  if(m.NeedsPerformance && !PerformanceFresh(d)) return "等待采样 / 数据已过期";
  if(m.NeedsHealth && !HealthFresh(h)) return h==null ? "无法获取" : "数据已过期";
  return m.Read(d,h);
 }
 Brush MetricColor(MetricDefinition m, Drive d, Health h, string value) {
  if(value.Contains("无法")||value.Contains("不足")||value.Contains("未知")||value.Contains("过期")||value.Contains("学习")||value.Contains("不适用")||value.Contains("未填写")||value.Contains("未返回")) return Muted;
  if(m.Layer==ConclusionLayer) {
   string state=m.Id=="health"&&h!=null ? h.LifetimeSeverity : m.Id=="thermal"&&h!=null ? h.ThermalRisk : "";
   return state=="严重" ? Red : state=="关注" ? Amber : state=="未见告警" ? Teal : Ink;
  }
  // Colors of input values don't silently add a second, hidden assessment.
  if(m.Id=="raw_UnsafeShutdowns" && (h.UnsafeShutdowns??0)>0) return Amber;
  return Ink;
 }
 string MetricHelp(MetricDefinition m, Drive d, Health h) {
  if(m.Id=="health" && HealthFresh(h)) return m.Help+"\n"+String.Join("\n",LifetimeReasons(h))+"\n来源："+h.SourceSummary();
  return m.Help;
 }
 void BuildMetricSelectors(StackPanel panel) {
  panel.Children.Add(HeadingWithHelp("悬浮窗 · 分类与逐项显示",LayerHelp+" 勾选仅影响显示，缺失字段也保留明确提示。",17));
  foreach(string layer in new [] {RawLayer,DerivedLayer,ConclusionLayer,ExtraLayer}) {
   var content=new StackPanel();
   foreach(var section in Metrics().Where(x=>x.Layer==layer).GroupBy(x=>x.Section)) {
    content.Children.Add(Text(section.Key,13,Muted));
    var columns=new UniformGridShim {Columns=2};
    foreach(var m in section) {
     var cell=new StackPanel(); var check=OptionCheck(cell,m.Title,"metric_"+m.Id); check.ToolTip=m.Help;
     columns.Children.Add(cell);
    }
    content.Children.Add(columns);
   }
   var expander=new Expander {Header=layer, Content=content, IsExpanded=layer==ConclusionLayer, Foreground=Ink, FontSize=UiFont(14), Margin=new Thickness(0,8,0,6)};
   panel.Children.Add(expander);
  }
 }
 static bool HasDisplayData(string value) {
  if(String.IsNullOrWhiteSpace(value))return false;
  return !new[]{"无法","未采集","未返回","未填写","未知","未尝试","等待","数据已过期","样本不足","证据不足","阈值未知","缺少","不适用","设备类型不适用","设备类型未知","设备协议不适用","本接口不适用","温度或设备阈值不足","学习"}.Any(x=>value.Trim().StartsWith(x,StringComparison.Ordinal));
 }
 bool ShowMetricValue(string value) {return detailsAvailabilityFilter==0 || HasDisplayData(value)==(detailsAvailabilityFilter==1);}
 void BuildCatalogView(List<Drive> selected, string layer) {
  detailsDataStack.Children.Add(HeadingWithHelp(layer,LayerHelp,20));
  foreach(var d in selected) {
   Health h; health.TryGetValue(d.Index,out h);
   var stack=new StackPanel();
   var title=Text("磁盘 "+d.Index+" · "+(h==null?"型号无法获取":h.Model),15,Ink);stack.Children.Add(title);
   detailValueUpdates.Add(()=>{Health current;health.TryGetValue(d.Index,out current);title.Text="磁盘 "+d.Index+" · "+(current==null?"型号无法获取":current.Model);});
   foreach(var section in Metrics().Where(x=>x.Layer==layer).GroupBy(x=>x.Section)) {
    var sectionHeading=HeadingWithHelp(section.Key,LayerHelp,16);stack.Children.Add(sectionHeading);
    var sectionItems=new List<FrameworkElement>();
    var grid=new UniformGridShim {Columns=layer==ConclusionLayer ? 3 : 6};
    foreach(var m in section) {
     string value=MetricValue(m,d,h); var tile=InfoTile(m.Title,value,MetricColor(m,d,h,value));
     tile.ToolTip=MetricHelp(m,d,h);
     FrameworkElement display=tile;
     if(m.Id=="ata_attributes") {
      var scroll=new ScrollViewer {Content=tile,MaxHeight=240,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
      display=new Expander {Header="完整 ATA 属性 · 点击展开",Content=scroll,Foreground=Ink,FontSize=UiFont(14),Margin=new Thickness(0,8,0,8)};stack.Children.Add(display);
     } else grid.Children.Add(tile);
     sectionItems.Add(display);display.Visibility=ShowMetricValue(value)?Visibility.Visible:Visibility.Collapsed;
     var valueText=(TextBlock)((StackPanel)tile.Child).Children[1];
     detailValueUpdates.Add(()=>{
      Health current;health.TryGetValue(d.Index,out current);
      string next=MetricValue(m,d,current);if(valueText.Text!=next)valueText.Text=next;
      display.Visibility=ShowMetricValue(next)?Visibility.Visible:Visibility.Collapsed;
      valueText.Foreground=MetricColor(m,d,current,next);
      string tip=MetricHelp(m,d,current);if(!Equals(tile.ToolTip,tip))tile.ToolTip=tip;
     });
    }
    stack.Children.Add(grid);
    Action updateSection=()=>{bool any=sectionItems.Any(x=>x.Visibility==Visibility.Visible);sectionHeading.Visibility=any?Visibility.Visible:Visibility.Collapsed;grid.Visibility=grid.Children.Cast<UIElement>().Any(x=>x.Visibility==Visibility.Visible)?Visibility.Visible:Visibility.Collapsed;};
    updateSection();detailValueUpdates.Add(updateSection);
   }
   var empty=Text("当前筛选下没有匹配指标",13,Muted);stack.Children.Add(empty);
   Action updateEmpty=()=>empty.Visibility=stack.Children.OfType<UniformGridShim>().Any(g=>g.Visibility==Visibility.Visible)||stack.Children.OfType<Expander>().Any(x=>x.Visibility==Visibility.Visible)?Visibility.Collapsed:Visibility.Visible;
   updateEmpty();detailValueUpdates.Add(updateEmpty);
   detailsDataStack.Children.Add(Card(stack,18,new Thickness(16,16,16,14)));
  }
 }
 StackPanel lastFloatingBody;
 string floatingLayoutKey;
 readonly List<Action> floatingValueUpdates=new List<Action>();
 void FillFloating(StackPanel body) {
  string key=String.Join("|",drives.Values.OrderBy(x=>x.Index).Select(x=>x.Name+":"+x.GetHashCode()))+"|"+String.Join("|",Metrics().Where(x=>MetricSelected(x.Id)).Select(x=>x.Id))+"|"+CardFill.GetHashCode();
  if(lastFloatingBody==body && floatingLayoutKey==key) {foreach(var update in floatingValueUpdates)update();return;}
  lastFloatingBody=body;floatingLayoutKey=key;floatingValueUpdates.Clear();
  body.Children.Clear();
  if(drives.Count==0) body.Children.Add(Text("等待硬盘采样…",13,Muted));
  foreach(var d in drives.Values.OrderBy(x=>x.Index)) {
   Health h; health.TryGetValue(d.Index,out h);
   var disk=new StackPanel(); disk.Children.Add(Text("磁盘 "+d.Index,14,Ink));
   foreach(string layer in new [] {RawLayer,DerivedLayer,ConclusionLayer}) {
    var picked=Metrics().Where(x=>x.Layer==layer&&MetricSelected(x.Id)).ToList();
    if(picked.Count==0) continue;
    var rows=new StackPanel();
    foreach(var m in picked) {
     string value=MetricValue(m,d,h);
     var row=new Grid {Margin=new Thickness(0,3,0,5)};
     row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
     row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1.2,GridUnitType.Star)});
     var label=Text(m.Title,12,Muted); label.Margin=new Thickness(0,0,10,0); row.Children.Add(label);
     var text=Text(value,13,MetricColor(m,d,h,value)); Grid.SetColumn(text,1); row.Children.Add(text); row.ToolTip=MetricHelp(m,d,h);
     floatingValueUpdates.Add(()=>{Health current;health.TryGetValue(d.Index,out current);string next=MetricValue(m,d,current);if(text.Text!=next)text.Text=next;text.Foreground=MetricColor(m,d,current,next);string tip=MetricHelp(m,d,current);if(!Equals(row.ToolTip,tip))row.ToolTip=tip;});
     rows.Children.Add(row);
    }
    string groupKey=d.Name+"|"+layer; bool expanded;
    var group=new Expander {Header=layer+" · "+picked.Count,Content=rows,Foreground=Ink,FontSize=UiFont(13),Margin=new Thickness(0,7,0,0),IsExpanded=!floatingGroups.TryGetValue(groupKey,out expanded)||expanded};
    group.Expanded+=(s,e)=>floatingGroups[groupKey]=true; group.Collapsed+=(s,e)=>floatingGroups[groupKey]=false;
    disk.Children.Add(group);
   }
   if(disk.Children.Count==1) disk.Children.Add(Text("尚未勾选磁盘指标 · 点右上角“指标”设置",12,Muted));
   body.Children.Add(Card(disk,15,new Thickness(12,10,12,8)));
  }
  var extras=Metrics().Where(x=>x.Layer==ExtraLayer&&MetricSelected(x.Id)).ToList();
  if(extras.Count>0) {
   var extra=new StackPanel(); extra.Children.Add(Text("附加功能 · 独立于硬盘评价",13,Ink));
   foreach(var m in extras) {var text=Text(m.Title+"  "+MetricValue(m,null,null),13,Ink);text.ToolTip=m.Help;extra.Children.Add(text);floatingValueUpdates.Add(()=>text.Text=m.Title+"  "+MetricValue(m,null,null));}
   body.Children.Add(Card(extra,15,new Thickness(12,10,12,8)));
  }
 }
}
