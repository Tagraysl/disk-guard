using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

public partial class Guard {
 class ManualSpec { public Dictionary<string,string> Values=new Dictionary<string,string>(); }
 class SpecField {public string Id,Title,Unit;public double Min,Max;}
 static readonly SpecField[] SpecFields={
  new SpecField {Id="min_temp",Title="标称最低工作温度",Unit="°C",Min=-100,Max=200},
  new SpecField {Id="max_temp",Title="标称最高工作温度",Unit="°C",Min=-100,Max=200},
  new SpecField {Id="read_peak",Title="标称顺序读取峰值",Unit="MB/s（十进制）",Min=.001,Max=1000000},
  new SpecField {Id="write_peak",Title="标称顺序写入峰值",Unit="MB/s（十进制）",Min=.001,Max=1000000},
  new SpecField {Id="tbw",Title="标称写入耐久度（SSD）",Unit="TBW / TB（十进制）",Min=.001,Max=1000000000},
  new SpecField {Id="start_stop",Title="标称启停次数",Unit="次",Min=1,Max=1000000000000},
  new SpecField {Id="load_unload",Title="标称载入卸载次数",Unit="次",Min=1,Max=1000000000000}
 };
 readonly Dictionary<string,ManualSpec> manualSpecs=new Dictionary<string,ManualSpec>();
 static string DeviceIdentity(string model,string serial,string pnp) {
  string id=String.IsNullOrWhiteSpace(serial)||serial=="未知"?pnp:serial.Trim();
  if(String.IsNullOrWhiteSpace(id)||id=="未知")return "";
  return Convert.ToBase64String(Encoding.UTF8.GetBytes((model??"")+"|"+id)).TrimEnd('=');
 }
 static bool TrySpec(IDictionary<string,string> inputs,out ManualSpec spec,out string error) {
  spec=new ManualSpec();error="";
  foreach(var field in SpecFields) {
   string text; if(!inputs.TryGetValue(field.Id,out text)||String.IsNullOrWhiteSpace(text))continue;
   double number;
   if(!Double.TryParse(text.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||Double.IsNaN(number)||Double.IsInfinity(number)||number<field.Min||number>field.Max) {error=field.Title+"请输入 "+field.Min+" 至 "+field.Max+" 之间的数值（"+field.Unit+"），也可留空。";return false;}
   if((field.Id=="start_stop"||field.Id=="load_unload") && number!=Math.Truncate(number)){error=field.Title+"必须是整数。";return false;}
   spec.Values[field.Id]=number.ToString("R",CultureInfo.InvariantCulture);
  }
  double? min=SpecNumber(spec,"min_temp"),max=SpecNumber(spec,"max_temp");
  if(min.HasValue&&max.HasValue&&min.Value>max.Value){error="最低工作温度不能高于最高工作温度。";return false;}
  string source;if(inputs.TryGetValue("source",out source)&&!String.IsNullOrWhiteSpace(source))spec.Values["source"]=source.Trim().Substring(0,Math.Min(source.Trim().Length,1000));
  return true;
 }
 static double? SpecNumber(ManualSpec spec,string key) {string text;double n;return spec!=null&&spec.Values.TryGetValue(key,out text)&&Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!Double.IsNaN(n)&&!Double.IsInfinity(n)?(double?)n:null;}
 ManualSpec Specification(Health h) {ManualSpec spec;return h!=null&&!String.IsNullOrEmpty(h.IdentityKey)&&health.Values.Count(x=>x.IdentityKey==h.IdentityKey)<=1&&manualSpecs.TryGetValue(h.IdentityKey,out spec)?spec:null;}
 static string SpecsPath() {var config=ConfigPath();return String.IsNullOrEmpty(config)?null:Path.Combine(Path.GetDirectoryName(config),"device-specs.cfg");}
 IEnumerable<string> SpecLines() {return manualSpecs.OrderBy(x=>x.Key).SelectMany(x=>x.Value.Values.OrderBy(v=>v.Key).Select(v=>x.Key+"|"+v.Key+"|"+Convert.ToBase64String(Encoding.UTF8.GetBytes(v.Value))));}
 void LoadSpecs(IEnumerable<string> lines) {
  var parsed=new Dictionary<string,Dictionary<string,string>>();
  foreach(string line in lines) {
   try {var parts=line.Split('|');if(parts.Length!=3)continue;
    Dictionary<string,string> fields;if(!parsed.TryGetValue(parts[0],out fields))parsed[parts[0]]=fields=new Dictionary<string,string>();
    fields[parts[1]]=Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
   }catch{}
  }
  foreach(var pair in parsed){ManualSpec spec;string error;if(TrySpec(pair.Value,out spec,out error))manualSpecs[pair.Key]=spec;}
 }
 void LoadSpecifications() {try {var path=SpecsPath();if(path!=null&&File.Exists(path))LoadSpecs(File.ReadAllLines(path));}catch{}}
 bool SaveSpecifications(out string error) {
  error="";if(!settingsPersistence)return true;
  try {var path=SpecsPath();if(path==null)throw new IOException("配置目录不可用");
   string temp=path+".tmp";File.WriteAllLines(temp,SpecLines(),Encoding.UTF8);
   if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);return true;
  } catch(Exception ex){error="保存失败："+ex.Message;return false;}
 }
 void BuildManualSpecifications(StackPanel panel) {
  var stack=new StackPanel();stack.Children.Add(HeadingWithHelp("逐盘标称信息 · 可选填写", "只保存你知道的字段，空白不会当成零。所有数值都标为手填，未经软件核验；不会覆盖设备实测值。按型号和序列号/设备标识绑定，不按容易变化的磁盘编号绑定。",17));
  stack.Children.Add(Text("填写数值即可，单位见字段标题；小数使用点号。",12,Muted));
  var refresh=ActionButton("刷新硬盘列表",async()=>{await RefreshSpecInventory();});refresh.ToolTip="刷新会重建表单，请先保存已填写内容；不会运行探针。";stack.Children.Add(refresh);
  if(probeTargets.Count==0)stack.Children.Add(Text("尚未取得硬盘列表，请点刷新；不会运行探针。",13,Muted));
  foreach(var target in probeTargets) {
   var form=new StackPanel();ManualSpec existing;manualSpecs.TryGetValue(target.Key,out existing);
   if(String.IsNullOrEmpty(target.Key)||probeTargets.Count(x=>x.Key==target.Key)>1){form.Children.Add(Text("设备身份缺失或不唯一，暂不绑定标称信息，避免串盘。",13,Muted));}
   else {
    var edits=new Dictionary<string,TextBox>();var grid=new UniformGridShim {Columns=2};
    foreach(var field in SpecFields) {
     var cell=new StackPanel {Margin=new Thickness(0,4,12,8)};cell.Children.Add(Text(field.Title+"（"+field.Unit+"）",13,Ink));
     string value;var box=StyledTextBox(Double.NaN,existing!=null&&existing.Values.TryGetValue(field.Id,out value)?value:"",new Thickness(0));box.ToolTip="可留空；只输入数值，单位："+field.Unit;
     box.HorizontalAlignment=HorizontalAlignment.Stretch;box.MinWidth=100;edits[field.Id]=box;cell.Children.Add(box);grid.Children.Add(cell);
    }
    form.Children.Add(grid);form.Children.Add(Text("资料来源 / 型号版本 / 备注（可选，不会自动访问链接）",13,Ink));
    string note;var source=StyledTextBox(Double.NaN,existing!=null&&existing.Values.TryGetValue("source",out note)?note:"",new Thickness(0));source.MaxLength=1000;
    source.HorizontalAlignment=HorizontalAlignment.Stretch;form.Children.Add(source);var status=Text("未填写的字段不影响其他字段保存。",12,Muted);
    form.Children.Add(ActionButton("保存这块硬盘的信息",()=>{
     var inputs=edits.ToDictionary(x=>x.Key,x=>x.Value.Text);inputs["source"]=source.Text;ManualSpec next;string error;
     if(!TrySpec(inputs,out next,out error)){status.Text=error;status.Foreground=Amber;return;}
     ManualSpec before;bool had=manualSpecs.TryGetValue(target.Key,out before);manualSpecs[target.Key]=next;
     if(!SaveSpecifications(out error)){if(had)manualSpecs[target.Key]=before;else manualSpecs.Remove(target.Key);status.Text=error;status.Foreground=Amber;return;}
     status.Text="已保存 "+next.Values.Count+" 个字段；空白项保持未填写。";status.Foreground=Teal;
     detailsLayoutKey=null;if(floating!=null&&floating.IsVisible)floating.Refresh();
    }));form.Children.Add(status);
   }
   stack.Children.Add(new Expander {Header=target.Label,Content=form,IsExpanded=true,Foreground=Ink,FontSize=UiFont(14),Margin=new Thickness(0,10,0,8)});
  }
  panel.Children.Add(Card(stack));
 }
 async System.Threading.Tasks.Task RefreshSpecInventory() {
  try {var found=await System.Threading.Tasks.Task.Run(()=>DiscoverProbeTargets());
   // Keep probe paths/selections; this refresh is for metadata forms only.
   foreach(var item in found){var old=probeTargets.FirstOrDefault(x=>x.Key==item.Key);if(old!=null){item.Folder=old.Folder;item.Enabled=old.Enabled;item.Result=old.Result;}}
   probeTargets=found;if(currentPage==PageKind.Settings)BuildPage(PageKind.Settings);
  }catch(Exception ex){MessageBox.Show("无法刷新硬盘列表："+ex.Message);}
 }
 string ManualValue(Health h,string key) {
  var spec=Specification(h);string value;
  if(spec==null||!spec.Values.TryGetValue(key,out value))return "未填写";
  var field=SpecFields.FirstOrDefault(x=>x.Id==key);return value+(field==null?"":" "+field.Unit)+" · 手填";
 }
 string ManualCalculation(Drive d,Health h,string key) {
  var spec=Specification(h);double? n=SpecNumber(spec,key);
  if(!n.HasValue)return "未填写相应标称值";
  if(key=="read_peak"||key=="write_peak") {
   bool read=key=="read_peak";if(!PerformanceFresh(d)||!(read?d.ReadValid:d.WriteValid))return "无法获取有效速率";
   return ((read?d.Read:d.Write)/1000000/n.Value*100).ToString("0.0")+"%（仅对标参考）";
  }
  if(!HealthFresh(h))return "无法获取 / 数据已过期";
  if(key=="max_temp"||key=="min_temp")return h.Temperature.HasValue ? (key=="max_temp"?n.Value-h.Temperature.Value:h.Temperature.Value-n.Value).ToString("0.0")+" °C（手填参照）" : "无法获取温度";
  if(key=="tbw") {if(h.MediaLabel=="HDD")return "设备类型不适用";if(!h.MediaLabel.Contains("SSD"))return "设备类型未知，无法对标 TBW";return h.DataUnitsWrittenBytes.HasValue?(h.DataUnitsWrittenBytes.Value/1e12/n.Value*100).ToString("0.00")+"%（主机写入/TBW，非磨损）":"缺少累计主机写入量";}
  long? count=key=="start_stop"?h.StartStopCycleCount:h.LoadUnloadCycleCount;
  return count.HasValue?(100.0*count.Value/n.Value).ToString("0.00")+"%（手填参照）":"缺少设备累计次数";
 }
 string ManualTemperatureState(Health h) {
  if(!HealthFresh(h)||!h.Temperature.HasValue)return "无法获取有效温度";
  var spec=Specification(h);double? min=SpecNumber(spec,"min_temp"),max=SpecNumber(spec,"max_temp");
  if(!min.HasValue&&!max.HasValue)return "未填写温度范围";
  if(min.HasValue&&h.Temperature.Value<min.Value || max.HasValue&&h.Temperature.Value>max.Value)return "超出手填范围（资料未经核验）";
  return "未超出已填写边界（不替代设备告警）";
 }
}
