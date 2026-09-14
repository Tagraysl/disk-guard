using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Native, read-only storage protocol queries.  This file deliberately exposes
// only IOCTL_STORAGE_QUERY_PROPERTY; it never sends a write, reset, format or
// device self-test command.
internal sealed class NativeStorageHealth {
 public bool IdentifyOk;
 public bool HealthOk;
 public string Source = "未尝试";
 public string Note = "";
 public string Model = "";
 public string Serial = "";
 public string Firmware = "";
 public byte? CriticalWarning;
 public double? Temperature;
 public double? AvailableSpare;
 public double? AvailableSpareThreshold;
 public double? PercentageUsed;
 public double? DataUnitsReadBytes;
 public double? DataUnitsWrittenBytes;
 public double? HostReadCommands;
 public double? HostWriteCommands;
 public double? ControllerBusyMinutes;
 public long? PowerCycleCount;
 public long? PowerOnHours;
 public long? UnsafeShutdowns;
 public long? MediaErrors;
 public double? ErrorLogEntries;
}

internal static class StorageProtocolReader {
 const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
 const uint STORAGE_ADAPTER_PROTOCOL_SPECIFIC_PROPERTY = 49;
 const uint STORAGE_DEVICE_PROTOCOL_SPECIFIC_PROPERTY = 50;
 const uint PROTOCOL_TYPE_NVME = 3;
 const uint NVME_DATA_TYPE_IDENTIFY = 1;
 const uint NVME_DATA_TYPE_LOG_PAGE = 2;
 const uint NVME_IDENTIFY_CNS_CONTROLLER = 1;
 const uint NVME_LOG_PAGE_HEALTH_INFO = 2;
 const uint FILE_SHARE_READ = 0x00000001;
 const uint FILE_SHARE_WRITE = 0x00000002;
 const uint FILE_SHARE_DELETE = 0x00000004;
 const uint OPEN_EXISTING = 3;

 [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
 static extern SafeFileHandle CreateFile(
  string fileName,
  uint desiredAccess,
  uint shareMode,
  IntPtr securityAttributes,
  uint creationDisposition,
  uint flagsAndAttributes,
  IntPtr templateFile);

 [DllImport("kernel32.dll", SetLastError = true)]
 static extern bool DeviceIoControl(
  SafeFileHandle device,
  uint controlCode,
  byte[] inBuffer,
  uint inBufferSize,
  byte[] outBuffer,
  uint outBufferSize,
  out uint bytesReturned,
  IntPtr overlapped);

 public static bool TryReadNvme(int physicalIndex, out NativeStorageHealth data) {
  data = new NativeStorageHealth();
  if(physicalIndex < 0) {
   data.Source = "NVMe 原生查询不可用";
   data.Note = "没有有效的 PhysicalDrive 编号";
   return false;
  }
  string path = @"\\.\PhysicalDrive" + physicalIndex;
  try {
   // A zero desired-access handle is sufficient for query IOCTLs and avoids
   // requiring write-capable raw-disk access or elevation on locked-down PCs.
   using(var handle = CreateFile(path, 0,
     FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
     IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero)) {
    if(handle == null || handle.IsInvalid) {
     data.Source = "NVMe 原生查询不可用";
     data.Note = "打开物理盘失败，错误码 " + Marshal.GetLastWin32Error();
     return false;
    }

    byte[] identify;
    int identifyOffset;
    string identifyError;
    data.IdentifyOk = QueryProtocol(handle, true, out identify, out identifyOffset, out identifyError);
    if(data.IdentifyOk) ParseIdentify(identify, identifyOffset, data);

    byte[] health;
    int healthOffset;
    string healthError;
    data.HealthOk = QueryProtocol(handle, false, out health, out healthOffset, out healthError);
    if(data.HealthOk) ParseHealth(health, healthOffset, data);

    if(data.IdentifyOk && data.HealthOk) data.Source = "NVMe Identify + SMART/Health";
    else if(data.HealthOk) data.Source = "NVMe SMART/Health";
    else if(data.IdentifyOk) data.Source = "NVMe Identify（健康日志未返回）";
    else data.Source = "NVMe 原生查询未支持";

    var notes = new System.Collections.Generic.List<string>();
    if(!data.IdentifyOk && !String.IsNullOrEmpty(identifyError)) notes.Add("Identify: " + identifyError);
    if(!data.HealthOk && !String.IsNullOrEmpty(healthError)) notes.Add("SMART/Health: " + healthError);
    data.Note = String.Join("；", notes.ToArray());
    return data.IdentifyOk || data.HealthOk;
   }
  } catch(Exception ex) {
   data.Source = "NVMe 原生查询失败";
   data.Note = ex.GetType().Name;
   return false;
  }
 }

 static bool QueryProtocol(SafeFileHandle handle, bool identify,
   out byte[] buffer, out int payloadOffset, out string error) {
  const int queryHeaderSize = 8;       // STORAGE_PROPERTY_QUERY up to AdditionalParameters
  const int protocolDataSize = 40;     // current STORAGE_PROTOCOL_SPECIFIC_DATA
  int payloadLength = identify ? 4096 : 512;
  int totalLength = queryHeaderSize + protocolDataSize + payloadLength;
  buffer = new byte[totalLength];
  payloadOffset = 0;
  error = "";

  WriteUInt32(buffer, 0, identify ? STORAGE_ADAPTER_PROTOCOL_SPECIFIC_PROPERTY : STORAGE_DEVICE_PROTOCOL_SPECIFIC_PROPERTY);
  WriteUInt32(buffer, 4, 0); // PropertyStandardQuery
  int p = queryHeaderSize;
  WriteUInt32(buffer, p + 0, PROTOCOL_TYPE_NVME);
  WriteUInt32(buffer, p + 4, identify ? NVME_DATA_TYPE_IDENTIFY : NVME_DATA_TYPE_LOG_PAGE);
  WriteUInt32(buffer, p + 8, identify ? NVME_IDENTIFY_CNS_CONTROLLER : NVME_LOG_PAGE_HEALTH_INFO);
  WriteUInt32(buffer, p + 12, 0);
  WriteUInt32(buffer, p + 16, protocolDataSize);
  WriteUInt32(buffer, p + 20, (uint)payloadLength);
  WriteUInt32(buffer, p + 24, 0);
  WriteUInt32(buffer, p + 28, 0);
  WriteUInt32(buffer, p + 32, 0);
  WriteUInt32(buffer, p + 36, 0);

  uint returned;
  bool ok = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
    buffer, (uint)buffer.Length, buffer, (uint)buffer.Length,
    out returned, IntPtr.Zero);
  if(!ok || returned == 0) {
   error = "错误码 " + Marshal.GetLastWin32Error();
   return false;
  }
  if(returned < (uint)(queryHeaderSize + protocolDataSize)) {
   error = "返回长度不足";
   return false;
  }

  uint offset = ReadUInt32(buffer, p + 16);
  uint length = ReadUInt32(buffer, p + 20);
  if(offset < protocolDataSize || offset > (uint)(buffer.Length - queryHeaderSize) || length == 0) {
   error = "驱动返回的协议数据范围无效";
   return false;
  }
  int start = queryHeaderSize + checked((int)offset);
  if(start < 0 || start > buffer.Length || length > (uint)(buffer.Length - start)) {
   error = "驱动返回的数据长度无效";
   return false;
  }
  payloadOffset = start;
  return true;
 }

 static void ParseIdentify(byte[] buffer, int offset, NativeStorageHealth data) {
  if(offset < 0 || offset + 80 > buffer.Length) return;
  data.Serial = AsciiField(buffer, offset + 4, 20);
  data.Model = AsciiField(buffer, offset + 24, 40);
  data.Firmware = AsciiField(buffer, offset + 64, 8);
 }

 static void ParseHealth(byte[] buffer, int offset, NativeStorageHealth data) {
  if(offset < 0 || offset + 192 > buffer.Length) return;
  data.CriticalWarning = buffer[offset + 0];
  ushort temperatureKelvin = ReadUInt16(buffer, offset + 1);
  // NVMe stores composite temperature in Kelvin. Ignore 0/FFFF and implausible values.
  if(temperatureKelvin >= 200 && temperatureKelvin <= 500)
   data.Temperature = temperatureKelvin - 273.15;
  data.AvailableSpare = (double)buffer[offset + 3];
  data.AvailableSpareThreshold = (double)buffer[offset + 4];
  data.PercentageUsed = (double)buffer[offset + 5];

  data.DataUnitsReadBytes = UInt128AsDouble(buffer, offset + 32) * 512000.0;
  data.DataUnitsWrittenBytes = UInt128AsDouble(buffer, offset + 48) * 512000.0;
  data.HostReadCommands = UInt128AsDouble(buffer, offset + 64);
  data.HostWriteCommands = UInt128AsDouble(buffer, offset + 80);
  data.ControllerBusyMinutes = UInt128AsDouble(buffer, offset + 96);
  data.PowerCycleCount = UInt128AsLong(buffer, offset + 112);
  data.PowerOnHours = UInt128AsLong(buffer, offset + 128);
  data.UnsafeShutdowns = UInt128AsLong(buffer, offset + 144);
  data.MediaErrors = UInt128AsLong(buffer, offset + 160);
  data.ErrorLogEntries = UInt128AsDouble(buffer, offset + 176);
 }

 static string AsciiField(byte[] buffer, int offset, int length) {
  if(offset < 0 || offset + length > buffer.Length) return "";
  string s = Encoding.ASCII.GetString(buffer, offset, length);
  return s.Trim('\0', ' ', '\t', '\r', '\n');
 }

 static long? UInt128AsLong(byte[] buffer, int offset) {
  ulong low = ReadUInt64(buffer, offset);
  ulong high = ReadUInt64(buffer, offset + 8);
  if(high != 0 || low > 9223372036854775807UL) return null;
  return (long)low;
 }

 static double UInt128AsDouble(byte[] buffer, int offset) {
  ulong low = ReadUInt64(buffer, offset);
  ulong high = ReadUInt64(buffer, offset + 8);
  return high * 18446744073709551616.0 + low;
 }

 static ushort ReadUInt16(byte[] buffer, int offset) {
  if(offset < 0 || offset + 2 > buffer.Length) return 0;
  return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
 }
 static uint ReadUInt32(byte[] buffer, int offset) {
  if(offset < 0 || offset + 4 > buffer.Length) return 0;
  return (uint)buffer[offset]
   | ((uint)buffer[offset + 1] << 8)
   | ((uint)buffer[offset + 2] << 16)
   | ((uint)buffer[offset + 3] << 24);
 }
 static ulong ReadUInt64(byte[] buffer, int offset) {
  if(offset < 0 || offset + 8 > buffer.Length) return 0;
  return (ulong)buffer[offset]
   | ((ulong)buffer[offset + 1] << 8)
   | ((ulong)buffer[offset + 2] << 16)
   | ((ulong)buffer[offset + 3] << 24)
   | ((ulong)buffer[offset + 4] << 32)
   | ((ulong)buffer[offset + 5] << 40)
   | ((ulong)buffer[offset + 6] << 48)
   | ((ulong)buffer[offset + 7] << 56);
 }
 static void WriteUInt32(byte[] buffer, int offset, uint value) {
  buffer[offset] = (byte)(value & 0xff);
  buffer[offset + 1] = (byte)((value >> 8) & 0xff);
  buffer[offset + 2] = (byte)((value >> 16) & 0xff);
  buffer[offset + 3] = (byte)((value >> 24) & 0xff);
 }
}
