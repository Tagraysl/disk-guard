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
   Console.WriteLine("PASS all UI regression checks; monitoring/config writes disabled"); return 0;
  } catch(Exception e) { Console.Error.WriteLine(e); return 1; }
  finally { if(win != null) win.Close(); }
 }
}
