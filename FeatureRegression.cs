using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
class FeatureRegression {
 static BindingFlags I=BindingFlags.Instance|BindingFlags.NonPublic, S=BindingFlags.Static|BindingFlags.NonPublic;
 static void Assert(bool ok,string reason){if(!ok)throw new Exception(reason);}
 static void Pump(){var f=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>f.Continue=false));Dispatcher.PushFrame(f);}
 static Type Nested(string name){return typeof(Guard).GetNestedType(name,BindingFlags.NonPublic);}
 static void Checksum(byte[] data){int sum=0;for(int i=0;i<511;i++)sum+=data[i];data[511]=(byte)((256-(sum&255))&255);}
 [STAThread] static int Main(){Guard win=null;try{
  System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
  var app=new Application();win=new Guard(false);win.Width=1000;win.Height=800;win.Show();Pump();
  var asm=typeof(Guard).Assembly;var ata=asm.GetType("AtaSmartReader");var record=asm.GetType("AtaSmartData");
  byte[] data=new byte[512],limits=new byte[512];data[0]=limits[0]=1;data[2]=limits[2]=5;data[5]=100;data[6]=90;data[7]=7;limits[3]=105;Checksum(data);Checksum(limits);
  object parsed=Activator.CreateInstance(record);object[] args={data,limits,parsed,""};
  var parse=ata.GetMethod("Parse");Assert((bool)parse.Invoke(null,args),"valid SMART rejected");
  var attributes=(IDictionary)record.GetField("Attributes").GetValue(parsed);var a=attributes[(byte)5];
  Assert((ulong)a.GetType().GetField("Raw").GetValue(a)==7,"RAW decoding wrong");
  Assert((bool)a.GetType().GetProperty("ThresholdExceeded").GetValue(a,null),"device threshold comparison wrong");
  data[511]++;args=new object[]{data,limits,parsed,""};Assert(!(bool)parse.Invoke(null,args)&&attributes.Count==0,"bad checksum accepted");Checksum(data);
  args=new object[]{data,null,parsed,""};Assert((bool)parse.Invoke(null,args),"missing thresholds rejected all data");
  a=attributes[(byte)5];Assert(a.GetType().GetField("Threshold").GetValue(a)==null,"missing threshold became zero");
  data[14]=5;Checksum(data);Assert(!(bool)parse.Invoke(null,new object[]{data,null,parsed,""}),"duplicate ID accepted");
  Assert(!(bool)ata.GetMethod("Matches").Invoke(null,new object[]{"IDE\\Other_0","IDE\\Disk"}),"cross-disk WMI mapping");
  Assert((bool)ata.GetMethod("Matches").Invoke(null,new object[]{"IDE\\Disk_0","IDE\\Disk"}),"WMI suffix mapping");
  foreach(bool threshold in new[]{false,true}){var request=(byte[])ata.GetMethod("Request",S).Invoke(null,new object[]{threshold});Assert(request[2]==2&&request[46]==0xB0&&request[40]==(threshold?0xD1:0xD0),"non-read ATA command");}
  Console.WriteLine("PASS ATA checksum/bounds/duplicate IDs/missing thresholds/WMI identity/read-command whitelist");
  var trySpec=typeof(Guard).GetMethod("TrySpec",S);
  var inputs=new Dictionary<string,string>{{"max_temp","60"},{"read_peak","200"},{"source","manufacturer reference"}};
  args=new object[]{inputs,null,""};Assert((bool)trySpec.Invoke(null,args),"partial spec rejected");var spec=args[1];
  Assert((bool)trySpec.Invoke(null,new object[]{new Dictionary<string,string>(),null,""}),"empty spec rejected");
  Assert(!(bool)trySpec.Invoke(null,new object[]{new Dictionary<string,string>{{"tbw","NaN"}},null,""}),"NaN accepted");
  Assert(!(bool)trySpec.Invoke(null,new object[]{new Dictionary<string,string>{{"min_temp","61"},{"max_temp","60"}},null,""}),"inverted bounds accepted");
  Assert(!(bool)trySpec.Invoke(null,new object[]{new Dictionary<string,string>{{"start_stop","1.5"}},null,""}),"fractional cycle count accepted");
  var identity=typeof(Guard).GetMethod("DeviceIdentity",S);string key=(string)identity.Invoke(null,new object[]{"Model","SerialA","PNPA"});
  string other=(string)identity.Invoke(null,new object[]{"Model","SerialB","PNPB"});Assert(key!=other,"device identities collide");
  var specs=(IDictionary)typeof(Guard).GetField("manualSpecs",I).GetValue(win);specs[key]=spec;
  var lines=((IEnumerable<string>)typeof(Guard).GetMethod("SpecLines",I).Invoke(win,null)).ToArray();specs.Clear();typeof(Guard).GetMethod("LoadSpecs",I).Invoke(win,new object[]{lines});Assert(specs.Contains(key)&&!specs.Contains(other),"per-device persistence failed");
  var ht=Nested("Health");object h=Activator.CreateInstance(ht);ht.GetField("IdentityKey").SetValue(h,key);ht.GetField("Temperature").SetValue(h,40.0);
  var dt=Nested("Drive");object d=Activator.CreateInstance(dt,new object[]{"99 Test",false});dt.GetField("Read").SetValue(d,100000000.0);dt.GetField("ReadValid").SetValue(d,true);dt.GetField("SampledAt").SetValue(d,DateTime.UtcNow);
  var calculation=typeof(Guard).GetMethod("ManualCalculation",I);
  Assert(((string)calculation.Invoke(win,new object[]{d,h,"max_temp"})).Contains("20.0"),"temperature margin wrong");
  Assert(((string)calculation.Invoke(win,new object[]{d,h,"read_peak"})).Contains("50.0%"),"reference ratio wrong");
  Assert(((string)calculation.Invoke(win,new object[]{d,h,"write_peak"})).Contains("未填写"),"missing input computed");
  ht.GetField("IdentityKey").SetValue(h,other);Assert(((string)calculation.Invoke(win,new object[]{d,h,"max_temp"})).Contains("未填写"),"spec crossed disks");
  Console.WriteLine("PASS partial/empty spec validation, units, per-device round-trip and conditional calculations");
  var targetType=Nested("ProbeTarget");object target=Activator.CreateInstance(targetType);targetType.GetField("Key").SetValue(target,key);targetType.GetField("Label").SetValue(target,"磁盘 0 · 回归测试硬盘（非真实采样）");
  ((IList)typeof(Guard).GetField("probeTargets",I).GetValue(win)).Add(target);
  var form=new StackPanel();typeof(Guard).GetMethod("BuildManualSpecifications",I).Invoke(win,new object[]{form});
  var originalContent=win.Content;win.Content=new ScrollViewer {Content=form};win.UpdateLayout();Pump();
  var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1000,800,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(win);
  var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var output=System.IO.File.Create("bin/manual-specs-test.png"))encoder.Save(output);
  win.Content=originalContent;win.UpdateLayout();Pump();Console.WriteLine("PASS per-disk optional form render");
  var sampleType=Nested("MetricSample");var points=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sampleType));var values=new List<double>();
  var series=dt.GetField("Series").GetValue(d);var enqueue=series.GetType().GetMethod("Enqueue");
  for(int i=0;i<32;i++){object point=Activator.CreateInstance(sampleType);double value=10+Math.Sin(2*Math.PI*i/8);sampleType.GetField("Time").SetValue(point,DateTime.UtcNow.AddSeconds((i-32)*10));sampleType.GetField("Read").SetValue(point,value);sampleType.GetField("Write").SetValue(point,value);sampleType.GetField("Queue").SetValue(point,Double.NaN);points.Add(point);values.Add(value);enqueue.Invoke(series,new[]{point});}
  var spectrum=typeof(Guard).GetMethod("ComputeSpectrum",I).Invoke(win,new object[]{values,points});var st=spectrum.GetType();var mags=(List<double>)st.GetField("Magnitudes").GetValue(spectrum);var freqs=(List<double>)st.GetField("Frequencies").GetValue(spectrum);
  Assert(mags.Count>0&&Math.Abs(freqs[mags.IndexOf(mags.Max())]-.0125)<.0001,"10-second frequency calibration wrong");
  foreach(int count in new[]{47,128,129,240}) {
   var longPoints=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sampleType));var longValues=new List<double>();var epoch=DateTime.UtcNow;
   for(int i=0;i<count;i++){object point=Activator.CreateInstance(sampleType);sampleType.GetField("Time").SetValue(point,epoch.AddSeconds(i*10));longPoints.Add(point);longValues.Add(10+Math.Sin(2*Math.PI*i/8));}
   var result=typeof(Guard).GetMethod("ComputeSpectrum",I).Invoke(win,new object[]{longValues,longPoints});var amplitudes=(List<double>)st.GetField("Magnitudes").GetValue(result);var frequencies=(List<double>)st.GetField("Frequencies").GetValue(result);
   Assert(amplitudes.Count>0&&Math.Abs(frequencies[amplitudes.IndexOf(amplitudes.Max())]-.0125)<.0001,"long-window FFT failed: "+count);
  }
  var map=(IDictionary)typeof(Guard).GetField("drives",I).GetValue(win);map["99 Test"]=d;
  var page=typeof(Guard).GetMethod("BuildPage",I);page.Invoke(win,new[]{Enum.Parse(Nested("PageKind"),"Details")});
  ((ComboBox)typeof(Guard).GetField("chartDomainSelector",I).GetValue(win)).SelectedIndex=1;Pump();
  Assert(((TextBlock)typeof(Guard).GetField("detailsChartTitle",I).GetValue(win)).Text.Contains("主峰"),"missing queue blocked read spectrum");
  var panel=(StackPanel)typeof(Guard).GetField("detailsDataStack",I).GetValue(win);var heading=panel.Children[0];var help=(Border)((DockPanel)heading).Children[0];var tip=(ToolTip)help.ToolTip;tip.PlacementTarget=help;tip.IsOpen=true;Pump();
  typeof(Guard).GetMethod("RenderDetailsView",I).Invoke(win,null);Pump();Assert(ReferenceEquals(heading,panel.Children[0])&&tip.IsOpen,"refresh destroyed active help");tip.IsOpen=false;
  dt.GetMethod("Sample").Invoke(d,new object[]{1});Assert((int)series.GetType().GetProperty("Count").GetValue(series,null)==33,"failed field cleared history");
  Console.WriteLine("PASS independent missing fields, 10-second FFT, retained history and stable open help");
  var hasData=typeof(Guard).GetMethod("HasDisplayData",S);
  foreach(string v in new[]{"0","0.0%","否","未见告警","当前 100 / 阈值 未返回"})Assert((bool)hasData.Invoke(null,new object[]{v}),"valid value hidden: "+v);
  foreach(string v in new[]{"无法获取","数据已过期","未填写","样本不足：等待","本接口不适用（SSD）"})Assert(!(bool)hasData.Invoke(null,new object[]{v}),"missing value shown: "+v);
  var filter=(ComboBox)typeof(Guard).GetField("detailsAvailabilitySelector",I).GetValue(win);
  ((IDictionary)typeof(Guard).GetField("health",I).GetValue(win))[99]=h;
  typeof(Guard).GetMethod("RenderDetailsView",I).Invoke(win,null);
  var diskCard=(Border)panel.Children[1];var diskStack=(StackPanel)diskCard.Child;
  var grids=diskStack.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().ToArray();
  Func<int> visible=()=>grids.Sum(g=>g.Children.Cast<UIElement>().Count(x=>x.Visibility==Visibility.Visible));
  filter.SelectedIndex=0;Pump();int all=visible();filter.SelectedIndex=1;Pump();int available=visible();filter.SelectedIndex=2;Pump();int missing=visible();
  Assert(all==available+missing&&available>0&&missing>0,"filter partition incorrect");Assert(ReferenceEquals(heading,panel.Children[0]),"filter rebuilt help");
  var password=(PasswordBox)typeof(Guard).GetMethod("StyledPasswordBox",I).Invoke(win,new object[]{220.0,new Thickness(0)});password.ApplyTemplate();
  var passwordBorder=(Border)System.Windows.Media.VisualTreeHelper.GetChild(password,0);Assert(passwordBorder.CornerRadius.TopLeft==10,"password has no rounded template");
  password.Password="test-only";Assert(password.Password=="test-only"&&password.Template.FindName("PART_ContentHost",password)!=null,"password control broken");
  Console.WriteLine("PASS data filter partition, zero/false values, stable controls and rounded password input");
  return 0;
 }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}finally{if(win!=null){typeof(Guard).GetField("exitRequested",I).SetValue(win,true);win.Close();}}}
}
