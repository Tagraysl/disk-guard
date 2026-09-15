using System;
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
 [STAThread] static int Main() {
  Guard win = null;
  try {
   var app = new Application(); win = new Guard(false); win.Show(); Pump();
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
    Page(win,"Details"); CheckCombo(win,"detailsDriveSelector");
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
   Console.WriteLine("PASS all UI regression checks; monitoring/config writes disabled"); return 0;
  } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
  finally { if(win != null) { typeof(Guard).GetField("exitRequested",Private).SetValue(win,true); win.Close(); } }
 }
}
