using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed class AtaAttribute {
 public byte Id, Current, Worst;
 public byte? Threshold;
 public ushort Flags;
 public ulong Raw;
 public bool ThresholdExceeded { get { return Threshold.HasValue && Threshold.Value>0 && Current>0 && Current<=Threshold.Value; } }
 public string Display { get { return "当前 "+Current+" / 最差 "+Worst+" / 阈值 "+(Threshold.HasValue?Threshold.Value.ToString():"未返回")+" · RAW 0x"+Raw.ToString("X12")+"（"+Raw+"）"; } }
}
internal sealed class AtaSmartData {
 public Dictionary<byte,AtaAttribute> Attributes=new Dictionary<byte,AtaAttribute>();
 public string Source="未返回", Note="";
 public bool Valid { get { return Attributes.Count>0; } }
}
internal static class AtaSmartReader {
 // Only SMART READ DATA (D0) and READ THRESHOLDS (D1) are exposed.
 // No SMART ENABLE, autosave, write, self-test, reset or format commands.
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]
 static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputLength,byte[] output,uint outputLength,out uint returned,IntPtr overlapped);
 public static Dictionary<string,byte[]> ReadWmi(bool thresholds) {
  var result=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
  try {
   using(var search=new ManagementObjectSearcher(@"root\wmi","SELECT InstanceName, VendorSpecific FROM "+(thresholds?"MSStorageDriver_FailurePredictThresholds":"MSStorageDriver_FailurePredictData")))
   using(var rows=search.Get()) foreach(ManagementObject row in rows) using(row) {
    string key=Convert.ToString(row["InstanceName"]);var data=row["VendorSpecific"] as byte[];
    if(!String.IsNullOrWhiteSpace(key)&&data!=null&&data.Length>=512) result[key]=data;
   }
  } catch { }
  return result;
 }
 public static bool Matches(string instance,string pnp) {
  if(String.IsNullOrWhiteSpace(pnp)) return false;
  return String.Equals(instance,pnp,StringComparison.OrdinalIgnoreCase) || String.Equals(instance,pnp+"_0",StringComparison.OrdinalIgnoreCase);
 }
 public static AtaSmartData Read(int index,string pnp,string bus,Dictionary<string,byte[]> blocks,Dictionary<string,byte[]> thresholds) {
  var result=new AtaSmartData();
  var matches=blocks.Where(x=>Matches(x.Key,pnp)).ToArray();
  if(matches.Length==1) {
   byte[] limits;thresholds.TryGetValue(matches[0].Key,out limits);
   string error;
   if(Parse(matches[0].Value,limits,result,out error)) {result.Source="WMI ATA SMART Data / Thresholds";return result;}
   result.Note=error;
  }
  if(!new [] {"SATA","ATA","IDE","3","11"}.Contains((bus??"").ToUpperInvariant())) {
   result.Source="ATA 接口未返回";
   result.Note="未匹配到有效 WMI SMART。USB/SAS/RAID 或未知总线暂未实现桥接透传；不代表硬盘没有 SMART。";return result;
  }
  if(index<0||IntPtr.Size!=8) {result.Source="接口不适用";return result;}
  using(var handle=CreateFile(@"\\.\PhysicalDrive"+index,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
   if(handle.IsInvalid) {int code=Marshal.GetLastWin32Error();result.Source=code==5?"ATA 查询权限不足":"ATA 打开失败";result.Note="错误码 "+code+"；可尝试以管理员运行。未发出磁盘写入命令。";return result;}
   byte[] data,limits;string error,thresholdError;
   if(!Query(handle,false,out data,out error)) {result.Source="ATA 读取失败 / 驱动未支持";result.Note=error;return result;}
   Query(handle,true,out limits,out thresholdError);
   if(Parse(data,limits,result,out error)) {result.Source="ATA SMART 只读透传";result.Note=String.IsNullOrEmpty(thresholdError)?"":("阈值未返回："+thresholdError);}
   else {result.Source="ATA 数据无效";result.Note=error;}
  }
  return result;
 }
 static byte[] Request(bool thresholds) {
  var b=new byte[48+512];
  b[0]=48;b[2]=2; // ATA_FLAGS_DATA_IN, 512 byte single-sector response
  Buffer.BlockCopy(BitConverter.GetBytes((uint)512),0,b,8,4);
  Buffer.BlockCopy(BitConverter.GetBytes((uint)3),0,b,12,4);
  Buffer.BlockCopy(BitConverter.GetBytes((ulong)48),0,b,24,8);
  b[40]=thresholds?(byte)0xD1:(byte)0xD0; b[41]=1;
  b[43]=0x4F;b[44]=0xC2;b[46]=0xB0;
  return b;
 }
 static bool Query(SafeFileHandle handle,bool thresholds,out byte[] data,out string error) {
  data=null;error="";var b=Request(thresholds);uint returned;
  if(!DeviceIoControl(handle,0x4D02C,b,(uint)b.Length,b,(uint)b.Length,out returned,IntPtr.Zero)) {error="错误码 "+Marshal.GetLastWin32Error();return false;}
  if(returned<b.Length || (b[46]&0x21)!=0 || BitConverter.ToUInt32(b,8)<512 || BitConverter.ToUInt64(b,24)!=48) {error="返回长度/状态/偏移无效";return false;}
  data=new byte[512];Buffer.BlockCopy(b,48,data,0,512);return true;
 }
 static bool BlockValid(byte[] data) {
  if(data==null||data.Length<512 || (data[0]==0&&data[1]==0) || (data[0]==255&&data[1]==255)) return false;
  int checksum=0;for(int i=0;i<512;i++)checksum+=data[i];return (checksum&255)==0;
 }
 public static bool Parse(byte[] data,byte[] limits,AtaSmartData result,out string error) {
  result.Attributes.Clear();error="";
  if(!BlockValid(data)){error="SMART 数据长度、版本或校验和无效";return false;}
  bool validLimits=BlockValid(limits);
  var thresholds=new Dictionary<byte,byte>();
  if(validLimits) for(int p=2;p<362;p+=12) if(limits[p]!=0) thresholds[limits[p]]=limits[p+1];
  for(int p=2;p<362;p+=12) {
   byte id=data[p];if(id==0)continue;
   if(result.Attributes.ContainsKey(id)){result.Attributes.Clear();error="SMART 属性 ID 重复";return false;}
   ulong raw=0;for(int i=0;i<6;i++)raw|=(ulong)data[p+5+i]<<(8*i);
   byte limit;result.Attributes[id]=new AtaAttribute {Id=id,Flags=(ushort)(data[p+1]|data[p+2]<<8),Current=data[p+3],Worst=data[p+4],Raw=raw,Threshold=thresholds.TryGetValue(id,out limit)?(byte?)limit:null};
  }
  if(result.Attributes.Count==0){error="没有有效 SMART 属性";return false;}
  return true;
 }
}
