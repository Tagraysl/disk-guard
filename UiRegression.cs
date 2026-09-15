using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;

class UiRegression {
 static BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
 static void Assert(bool ok, string message) { if(!ok) throw new Exception(message); }
 static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
 static void Page(Guard win, string name) { var kind = typeof(Guard).GetNestedType("PageKind", BindingFlags.NonPublic); typeof(Guard).GetMethod("BuildPage", Private).Invoke(win, new object[] { Enum.Parse(kind, name) }); win.UpdateLayout(); Pump(); }
 static void CheckCombo(Guard win, string field) {
  var combo = (ComboBox)typeof(Guard).GetField(field, Private).GetValue(win);
  Assert(combo != null && combo.Items.Count > 1, field + " options missing");
  combo.BringIntoView(); win.UpdateLayout(); Pump(); combo.ApplyTemplate();
  var toggle = (ToggleButton)combo.Template.FindName("DropDownToggle", combo);
  Assert(toggle != null, field + " toggle missing");
  typeof(ToggleButton).GetMethod("OnClick", Private).Invoke(toggle, null); Pump();
  Assert(combo.IsDropDownOpen, field + " does not open");
  var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
  Assert(popup.IsOpen && popup.Child.IsVisible, field + " popup not visible");
  var source = PresentationSource.FromVisual(win);
  var keyPreview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
  combo.RaiseEvent(keyPreview); Assert(!keyPreview.Handled, field + " swallows navigation key");
  var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
  combo.RaiseEvent(escape); Pump(); Assert(!combo.IsDropDownOpen, field + " Escape failed");
  typeof(ToggleButton).GetMethod("OnClick", Private).Invoke(toggle, null); Pump(); Assert(combo.IsDropDownOpen, field + " reopen failed");
  int target = combo.SelectedIndex == 0 ? 1 : 0;
  var item = (ComboBoxItem)combo.Items[target];
  var preview = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
  item.RaiseEvent(preview); Assert(!preview.Handled, field + " swallows item preview click");
  var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
  item.RaiseEvent(down);
  var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseUpEvent };
  item.RaiseEvent(up); Pump();
  Assert(combo.SelectedIndex == target, field + " mouse selection failed");
  Assert(!combo.IsDropDownOpen, field + " does not close after selection");
  Console.WriteLine("PASS mouse open/select/close: " + field);
 }
 static void CheckAssessments(Guard win) {
  var ht = typeof(Guard).GetNestedType("Health", BindingFlags.NonPublic);
  object h = Activator.CreateInstance(ht, true);
  Func<string,string> get = key => (string)ht.GetProperty(key).GetValue(h, null);
  Assert(get("LifetimeSeverity") == "未知", "missing health must not be healthy");
  ht.GetField("Temperature").SetValue(h, 95.0);
  ht.GetField("TemperatureMax").SetValue(h, 100.0);
  Assert(get("ThermalRisk") == "未见告警", "arbitrary 90% Celsius warning");
  Assert(get("Severity") == "未知", "temperature alone must not establish health");
  ht.GetField("Temperature").SetValue(h, 100.0);
  Assert(get("ThermalRisk") == "关注", "temperature boundary missed");
  ht.GetField("TemperatureMax").SetValue(h, null);
  Assert(get("ThermalRisk") == "未知", "missing threshold guessed");
  ht.GetField("Wear").SetValue(h, 95.0);
  Assert(get("LifetimeSeverity") == "未知", "wear alone must not establish health");
  ht.GetField("Wear").SetValue(h, 100.0);
  Assert(get("LifetimeSeverity") == "关注", "endurance consumed is not confirmed failure");
  ht.GetField("Wear").SetValue(h, null);
  ht.GetField("SmartKnown").SetValue(h, true);
  Assert(get("LifetimeSeverity") == "未见告警", "known negative SMART lost");
  ht.GetField("ReadErrorsUncorrected").SetValue(h, (long)1);
  Assert(get("LifetimeSeverity") == "关注", "historical error labeled current failure");
  ht.GetField("CriticalWarning").SetValue(h, (byte)4);
  Assert(get("LifetimeSeverity") == "严重", "device reliability warning missed");
  var dt = typeof(Guard).GetNestedType("Drive", BindingFlags.NonPublic);
  object d = Activator.CreateInstance(dt, new object[] {"99 Test", false});
  var workload = typeof(Guard).GetMethod("WorkloadSummary", Private);
  Assert(((string)workload.Invoke(win,new [] {d})).Contains("等待"), "unsampled data accepted");
  dt.GetField("SampledAt").SetValue(d,DateTime.UtcNow);
  Assert(((string)workload.Invoke(win,new [] {d})).Contains("无法评价"), "missing treated as zero");
  dt.GetField("ReadValid").SetValue(d,true); dt.GetField("WriteValid").SetValue(d,true);
  Assert(((string)workload.Invoke(win,new [] {d})).Contains("无读写"), "valid idle not distinguished");
  dt.GetField("Read").SetValue(d,1000000.0); dt.GetField("Write").SetValue(d,1000000.0);
  Assert(((string)workload.Invoke(win,new [] {d})).Contains("50.0%"), "write proportion wrong");
  dt.GetField("SampledAt").SetValue(d,DateTime.UtcNow.AddHours(-1));
  Assert(((string)workload.Invoke(win,new [] {d})).Contains("过期"), "stale accepted");
  var observed = typeof(Guard).GetMethod("Observed",BindingFlags.Static|BindingFlags.NonPublic);
  Assert((string)observed.Invoke(null,new object[]{0.0,false,"0",""}) == "无法获取", "missing rendered zero");
  Assert((string)observed.Invoke(null,new object[]{Double.NaN,true,"0",""}) == "无法获取", "NaN accepted");
  Console.WriteLine("PASS evidence model: missing/idle/stale, ratio, thermal boundaries, SMART, historical errors and endurance");
 }
 static void CheckCatalog(Guard win) {
  var catalog=((IEnumerable)typeof(Guard).GetMethod("Metrics",Private).Invoke(win,null)).Cast<object>().ToArray();
  Func<object,string,string> field=(m,key)=>(string)m.GetType().GetField(key).GetValue(m);
  Assert(catalog.Length>=70,"catalog missing existing fields");
  Assert(catalog.Select(m=>field(m,"Id")).Distinct().Count()==catalog.Length,"duplicate metric identity");
  Func<string,string> layer=id=>field(catalog.Single(m=>field(m,"Id")==id),"Layer");
  Assert(layer("raw_Wear")=="原始数据" && layer("raw_Status")=="原始数据","upstream report not raw");
  Assert(layer("active")=="计算指标" && layer("temperature_margin")=="计算指标","numeric calculation misplaced");
  Assert(layer("health")=="结论" && layer("thermal")=="结论" && layer("workload")=="结论","judgments in derived layer");
  Assert(catalog.Where(m=>field(m,"Id").StartsWith("node_")).All(m=>field(m,"Layer")=="附加功能"),"node mixed with disk");
  var set=typeof(Guard).GetMethod("SetOption",Private);var get=typeof(Guard).GetMethod("GetOption",Private);
  var defaults=catalog.Where(m=>(bool)get.Invoke(win,new object[]{"metric_"+field(m,"Id")})).Select(m=>field(m,"Id")).OrderBy(x=>x).ToArray();
  var expected=new [] {"read","write","raw_Temperature","raw_Wear","raw_UnsafeShutdowns","health","workload","node_ip","node_location"}.OrderBy(x=>x).ToArray();
  Assert(defaults.SequenceEqual(expected),"public defaults differ from approved floating selection");
  set.Invoke(win,new object[]{"metric_raw_DataUnitsReadBytes",true,false});
  set.Invoke(win,new object[]{"metric_raw_DataUnitsWrittenBytes",false,false});
  Assert((bool)get.Invoke(win,new object[]{"metric_raw_DataUnitsReadBytes"}) && !(bool)get.Invoke(win,new object[]{"metric_raw_DataUnitsWrittenBytes"}),"read/write selections coupled");
  string[] saved=((IEnumerable<string>)typeof(Guard).GetMethod("MetricChoiceLines",Private).Invoke(win,null)).ToArray();
  var choices=(IDictionary)typeof(Guard).GetField("metricChoices",Private).GetValue(win);choices.Clear();
  foreach(string line in saved){var pair=line.Split('=');set.Invoke(win,new object[]{pair[0],pair[1]=="1",false});}
  Assert(!(bool)get.Invoke(win,new object[]{"metric_raw_DataUnitsWrittenBytes"}),"unchecked choice not preserved on reload");
  // Exercise every getter, including completely missing health data.
  var dt=typeof(Guard).GetNestedType("Drive",BindingFlags.NonPublic);object d=Activator.CreateInstance(dt,new object[]{"99 Catalog",false});
  var value=typeof(Guard).GetMethod("MetricValue",Private);
  foreach(var m in catalog) Assert(value.Invoke(win,new object[]{m,d,null}) is string,"missing-field rendering failed");
  var map=(IDictionary)typeof(Guard).GetField("drives",Private).GetValue(win);map["99 Catalog"]=d;
  foreach(var m in catalog) set.Invoke(win,new object[]{"metric_"+field(m,"Id"),false,false});
  foreach(string id in new [] {"read","active","health","node_risk"}) set.Invoke(win,new object[]{"metric_"+id,true,false});
  var body=new StackPanel(); var fill=typeof(Guard).GetMethod("FillFloating",Private);fill.Invoke(win,new object[]{body});
  Assert(body.Children.Count==2,"extras not separate from disk");
  var disk=(StackPanel)((Border)body.Children[0]).Child;
  var groups=disk.Children.OfType<Expander>().ToArray();Assert(groups.Length==3,"floating three layers missing");
  groups[0].IsExpanded=false;fill.Invoke(win,new object[]{body});
  disk=(StackPanel)((Border)body.Children[0]).Child;
  Assert(!disk.Children.OfType<Expander>().First().IsExpanded,"refresh reset group collapse");
  foreach(var m in catalog) set.Invoke(win,new object[]{"metric_"+field(m,"Id"),false,false});
  foreach(string id in new [] {"read","write","raw_Temperature","active","health"}) set.Invoke(win,new object[]{"metric_"+id,true,false});
  dt.GetField("Read").SetValue(d,24000000.0);dt.GetField("Write").SetValue(d,3000000.0);
  dt.GetField("ReadValid").SetValue(d,true);dt.GetField("WriteValid").SetValue(d,true);dt.GetField("ActiveValid").SetValue(d,true);
  dt.GetField("Active").SetValue(d,12.0);dt.GetField("SampledAt").SetValue(d,DateTime.UtcNow);
  var ht=typeof(Guard).GetNestedType("Health",BindingFlags.NonPublic);object h=Activator.CreateInstance(ht,true);
  ht.GetField("Temperature").SetValue(h,38.0);ht.GetField("Status").SetValue(h,"正常");
  var health=(IDictionary)typeof(Guard).GetField("health",Private).GetValue(win);health[99]=h;
  ((IDictionary)typeof(Guard).GetField("floatingGroups",Private).GetValue(win)).Clear();
  var ft=typeof(Guard).GetNestedType("FloatingPanel",BindingFlags.NonPublic);var floating=(Window)Activator.CreateInstance(ft,new object[]{win});
  try {
   floating.Show();Pump();floating.UpdateLayout();
   var bmp=new System.Windows.Media.Imaging.RenderTargetBitmap((int)floating.ActualWidth,(int)floating.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(floating);
   var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
   using(var file=System.IO.File.Create("bin/floating-categories.png"))png.Save(file);
  } finally {floating.Close();}
  health.Remove(99);map.Remove("99 Catalog");choices.Clear();
  Console.WriteLine("PASS unified catalog, strict layers, independent toggles, persistence round-trip, missing data and floating group retention");
 }
 [STAThread] static int Main() {
  System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
  Guard win = null;
  try {
   var app = new Application(); win = new Guard(false); win.Show(); Pump();
   CheckAssessments(win);
   CheckCatalog(win);
   foreach(string theme in new [] { "Light", "Dark" }) {
    var themeType = typeof(Guard).GetNestedType("ThemeMode", BindingFlags.NonPublic);
    typeof(Guard).GetField("themeMode", Private).SetValue(win, Enum.Parse(themeType, theme));
    typeof(Guard).GetMethod("ApplyThemePalette", Private).Invoke(win, null);
    foreach(string field in new [] { "fontSelector", "themeSelector", "performanceIntervalSelector", "healthIntervalSelector", "probeSizeSelector", "probeDurationSelector", "probeScheduleSelector", "ping0IntervalSelector" }) { Page(win,"Settings"); CheckCombo(win,field); }
    Page(win,"Details"); CheckCombo(win,"detailsLayerSelector");
    Page(win,"Details"); CheckCombo(win,"chartDomainSelector");
    var driveType = typeof(Guard).GetNestedType("Drive", BindingFlags.NonPublic);
    var driveMap = (System.Collections.IDictionary)typeof(Guard).GetField("drives",Private).GetValue(win);
    driveMap["99 Test"] = Activator.CreateInstance(driveType, new object[] { "99 Test", false });
    Page(win,"Details"); CheckCombo(win,"detailsDriveSelector"); CheckCombo(win,"detailsAvailabilitySelector");
    Console.WriteLine("PASS theme: " + theme);
   }
   var tileMethod = typeof(Guard).GetMethod("InfoTile",Private);
   var ink = (Brush)typeof(Guard).GetField("Ink",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
   var green = (Brush)typeof(Guard).GetField("Teal",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
   var muted = (Brush)typeof(Guard).GetField("Muted",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
   foreach(string label in new [] { "介质", "总线", "容量", "型号", "通电小时" }) {
    var tile = (Border)tileMethod.Invoke(win,new object[]{label,"Example",green});
    Assert(((TextBlock)((StackPanel)tile.Child).Children[1]).Foreground == ink, label + " not neutral");
   }
   var missing = (Border)tileMethod.Invoke(win,new object[]{"设备状态","未知",green});
   Assert(((TextBlock)((StackPanel)missing.Child).Children[1]).Foreground == muted,"unknown shown green");
   Console.WriteLine("PASS neutral/missing semantic colors");
   var healthType = typeof(Guard).GetNestedType("Health",BindingFlags.NonPublic);
   var fixture = Activator.CreateInstance(healthType);
   healthType.GetField("Model").SetValue(fixture,"Example NVMe 2TB");
   healthType.GetField("Bus").SetValue(fixture,"NVMe");
   healthType.GetField("Media").SetValue(fixture,"SSD");
   healthType.GetField("SizeBytes").SetValue(fixture,(long?)2000000000000L);
   healthType.GetField("UnsafeShutdowns").SetValue(fixture,(long?)12L);
   healthType.GetField("MediaErrors").SetValue(fixture,(long?)0L);
   var map = (System.Collections.IDictionary)typeof(Guard).GetField("health",Private).GetValue(win); map[99] = fixture;
   var rawType = typeof(Guard).GetNestedType("DataViewKind",BindingFlags.NonPublic);
   typeof(Guard).GetField("selectedDataView",Private).SetValue(win,Enum.Parse(rawType,"Raw"));
   win.Width=1220; win.Height=900; Page(win,"Details");
   ((Border)typeof(Guard).GetField("detailsChartCard",Private).GetValue(win)).Visibility=Visibility.Collapsed;
   System.Threading.Thread.Sleep(250); Pump(); win.UpdateLayout();
   var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(win);
   var png=new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
   using(var file=System.IO.File.Create("ui-regression.png")) png.Save(file);
   var targetType = typeof(Guard).GetNestedType("ProbeTarget",BindingFlags.NonPublic);
   var targets = (System.Collections.IList)typeof(Guard).GetField("probeTargets",Private).GetValue(win);
   string folder = System.IO.Path.GetFullPath("bin");
   for(int i=0;i<3;i++) {
    var target = Activator.CreateInstance(targetType);
    targetType.GetField("Key").SetValue(target,"fixture"+i);
    targetType.GetField("Index").SetValue(target,i);
    targetType.GetField("Label").SetValue(target,"Fixture disk "+i);
    targetType.GetField("Roots").SetValue(target,new [] { i==0 ? System.IO.Path.GetPathRoot(folder) : (i==1 ? "Y:\\" : "Z:\\") });
    targetType.GetField("Folder").SetValue(target,folder);
    targets.Add(target);
   }
   Page(win,"Settings");
   var rows=(StackPanel)typeof(Guard).GetField("probeRows",Private).GetValue(win);
   Assert(rows.Children.Count==6,"expected one path row and result per physical disk");
   var first=(Grid)rows.Children[0]; var second=(Grid)rows.Children[2];
   var check=(CheckBox)((StackPanel)first.Children[2]).Children[1]; check.IsChecked=true;
   Assert((bool)targetType.GetField("Enabled").GetValue(targets[0]),"disk selection not updated");
   Assert(!(bool)targetType.GetField("Enabled").GetValue(targets[1]),"disk selection leaked");
   var input=(TextBox)first.Children[1]; input.ApplyTemplate();
   Assert(input.Template.FindName("PART_ContentHost",input)!=null,"rounded textbox content host missing");
   var match=typeof(Guard).GetMethod("TargetMatches",BindingFlags.Static|BindingFlags.NonPublic);
   Assert((bool)match.Invoke(null,new object[]{targets[0],folder,targets}),"valid physical target rejected");
   Assert(!(bool)match.Invoke(null,new object[]{targets[1],folder,targets}),"wrong physical disk accepted");
   check.IsChecked=false;
   typeof(Guard).GetField("probeEnabled",Private).SetValue(win,true);
   typeof(Guard).GetMethod("BeginProbeIfDue",Private).Invoke(win,new object[]{true});
   Assert(typeof(Guard).GetField("probeTask",Private).GetValue(win)==null,"unchecked disks launched probe");
   var foreign=System.IO.Path.GetFullPath("test-fixtures/unowned-probe/.diskguard-probe/fixed-sequential.bin");
   string before=System.IO.File.ReadAllText(foreign);
   typeof(Guard).GetMethod("RunProbe",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{System.IO.Path.GetFullPath("test-fixtures/unowned-probe"),32,10});
   Assert(System.IO.File.ReadAllText(foreign)==before,"foreign probe file modified");
   rows.BringIntoView(); win.UpdateLayout(); System.Threading.Thread.Sleep(300); Pump();
   bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(win);
   png=new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
   using(var file=System.IO.File.Create("bin/settings-regression.png")) png.Save(file);
   Page(win,"Details"); var chart=(Canvas)typeof(Guard).GetField("detailsChart",Private).GetValue(win);
   var sentinel=new Border(); chart.Children.Add(sentinel); win.Hide();
   typeof(Guard).GetMethod("RenderPerformanceChart",Private).Invoke(win,null);
   typeof(Guard).GetMethod("RenderCurrent",Private).Invoke(win,null);
   Assert(chart.Children.Contains(sentinel),"hidden chart still rebuilt");
   typeof(Guard).GetMethod("CreateTray",Private).Invoke(win,null);
   typeof(Guard).GetField("closeToTray",Private).SetValue(win,true);
   win.Show(); win.Close(); Pump(); Assert(!win.IsVisible,"close did not hide to tray");
   typeof(Guard).GetMethod("RestoreMain",Private).Invoke(win,null); Pump(); Assert(win.IsVisible,"tray restore failed");
   Console.WriteLine("PASS tray close and restore");
   Console.WriteLine("PASS three-disk rows, independent selection, path validation, foreign-file preservation and hidden rendering");
   Page(win,"Settings"); CheckCombo(win,"textSizeSelector");
   Assert(win.UseLayoutRounding && win.SnapsToDevicePixels,"pixel alignment disabled");
   Assert(TextOptions.GetTextFormattingMode(win)==TextFormattingMode.Display,"display text formatting disabled");
   foreach(int step in new [] {0,2,4}) {
    Page(win,"Settings"); var sizes=(ComboBox)typeof(Guard).GetField("textSizeSelector",Private).GetValue(win); sizes.SelectedIndex=step/2; Pump();
    var metric=(Border)tileMethod.Invoke(win,new object[]{"容量","2.0 TB",ink});
    var labels=(StackPanel)metric.Child;
    Assert(((TextBlock)labels.Children[0]).FontSize==13+step,"metric label size mismatch");
    Assert(((TextBlock)labels.Children[1]).FontSize==14+step,"metric value size mismatch");
    Assert(metric.Effect==null,"metric retains rasterizing effect");
   }
   var tt=typeof(Guard).GetNestedType("ThemeMode",BindingFlags.NonPublic);
   foreach(string theme in new [] {"Light","Dark"}) {
    typeof(Guard).GetField("themeMode",Private).SetValue(win,Enum.Parse(tt,theme));
    typeof(Guard).GetMethod("ApplyThemePalette",Private).Invoke(win,null);
    typeof(Guard).GetMethod("RebuildShellForTheme",Private).Invoke(win,null);
    win.Width=920; win.Height=740; Page(win,"Settings");
    var sizeControl=(ComboBox)typeof(Guard).GetField("textSizeSelector",Private).GetValue(win); sizeControl.BringIntoView();
    System.Threading.Thread.Sleep(300); Pump(); win.UpdateLayout();
    bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(win);
    png=new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using(var file=System.IO.File.Create("bin/typography-"+theme+".png")) png.Save(file);
   }
   Console.WriteLine("PASS text size modes, pixel alignment and light/dark typography snapshots");
   var riskMethod=typeof(Guard).GetMethod("RiskNumber",BindingFlags.Static|BindingFlags.NonPublic);
   foreach(var invalid in new [] {"", "null", "NaN", "Infinity", "-1", "101", "18%"}) Assert(riskMethod.Invoke(null,new object[]{invalid})==null,"invalid risk accepted: "+invalid);
   Assert((double)riskMethod.Invoke(null,new object[]{"18"})==18,"valid risk rejected");
   var bandMethod=typeof(Guard).GetMethod("RiskBand",BindingFlags.Static|BindingFlags.NonPublic);
   string[] bands={"极度纯净","纯净","中性","轻微风险","稍高风险","极度风险"};
   double[] values={15,25,40,50,70,100};
   for(int i=0;i<values.Length;i++) Assert((string)bandMethod.Invoke(null,new object[]{values[i]})==bands[i],"risk boundary mismatch");
   Page(win,"Details");
   var seriesMethod=typeof(Guard).GetMethod("SeriesBrush",BindingFlags.Static|BindingFlags.NonPublic);
   var readColor=(SolidColorBrush)seriesMethod.Invoke(null,new object[]{"读取"});
   var readCheck=(CheckBox)typeof(Guard).GetField("chartReadCheck",Private).GetValue(win);
   Assert(((SolidColorBrush)readCheck.Foreground).Color==readColor.Color,"chart selector color mismatch");
   ink=(Brush)typeof(Guard).GetField("Ink",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
   var readTile=(Border)tileMethod.Invoke(win,new object[]{"读取速度","1.0 MB/s",readColor});
   var readStack=(StackPanel)readTile.Child;
   Assert(((TextBlock)readStack.Children[1]).Foreground==ink,"performance value implies health by color");
   var dot=(System.Windows.Documents.Run)((TextBlock)readStack.Children[0]).Inlines.FirstInline;
   Assert(((SolidColorBrush)dot.Foreground).Color==readColor.Color,"metric swatch/curve mismatch");
   var driveMap2=(System.Collections.IDictionary)typeof(Guard).GetField("drives",Private).GetValue(win);
   healthType.GetField("Model").SetValue(fixture,"Very long NVMe model / 超长磁盘型号测试 / 2TB PRO CONTROLLER SERIES");
   var driveHeader=(DockPanel)typeof(Guard).GetMethod("DriveHeading",Private).Invoke(win,new object[]{driveMap2["99 Test"],fixture});
   driveHeader.Measure(new Size(260,double.PositiveInfinity)); Assert(driveHeader.DesiredSize.Width<=261,"long model overflows available width");
   var pingType=typeof(Guard).GetNestedType("Ping0Snapshot",BindingFlags.NonPublic); var ping=Activator.CreateInstance(pingType);
   pingType.GetField("IpRisk").SetValue(ping,"18"); pingType.GetField("Status").SetValue(ping,"测试示例 · 非实测");
   typeof(Guard).GetField("ping0",Private).SetValue(win,ping); typeof(Guard).GetField("ping0Enabled",Private).SetValue(win,true);
   foreach(string page in new [] {"Overview","Details","Settings","Evidence"}) {
    win.Width=720;win.Height=740;Page(win,page); System.Threading.Thread.Sleep(300);Pump();win.UpdateLayout();
    if(page=="Details") {
     ((Border)typeof(Guard).GetField("detailsChartCard",Private).GetValue(win)).Visibility=Visibility.Collapsed;
     var data=(StackPanel)typeof(Guard).GetField("detailsDataStack",Private).GetValue(win);
     var card=(Border)data.Children[1]; Assert(card.Padding.Left>=12 && card.Padding.Top>=12,"raw/derived disk card missing inset");
     ((FrameworkElement)((StackPanel)card.Child).Children[0]).BringIntoView(); Pump();
    }
    if(page=="Overview") { ((FrameworkElement)typeof(Guard).GetField("overviewRiskPanel",Private).GetValue(win)).BringIntoView(); Pump(); }
    bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(win);
    png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using(var file=System.IO.File.Create("bin/layout-"+page+".png"))png.Save(file);
   }
   Console.WriteLine("PASS risk validation/boundaries, semantic vs series colors, long model bounds and four narrow-page snapshots");
   Page(win,"Details");
   var layers = (ComboBox)typeof(Guard).GetField("detailsLayerSelector",Private).GetValue(win);
   foreach(int layer in new [] {1,2}) {
    layers.SelectedIndex=layer; Pump(); win.UpdateLayout();
    bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth,(int)win.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(win);
    png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    using(var file=System.IO.File.Create("bin/assessment-"+layer+".png"))png.Save(file);
   }
   Console.WriteLine("PASS all UI regression checks; monitoring/config writes disabled"); return 0;
  } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
  finally { if(win != null) { typeof(Guard).GetField("exitRequested",Private).SetValue(win,true); win.Close(); } }
 }
}
