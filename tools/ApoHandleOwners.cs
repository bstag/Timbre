#if NET9_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace EposResearch {
    public sealed class ApoHandleOwner {
        public string ObjectName { get; set; }
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public int HandleCount { get; set; }
    }
#if NET9_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public static class ApoHandleOwners {
        [StructLayout(LayoutKind.Sequential)] struct HandleEntry {
            public IntPtr Object, ProcessId, Handle;
            public uint Access;
            public ushort BackTrace, TypeIndex;
            public uint Attributes, Reserved;
        }
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int kind, IntPtr buffer, int length, out int required);
        public static ApoHandleOwner[] Read(string product) {
            if (IntPtr.Size != 8 || (product != "0098" && product != "009f")) throw new ArgumentException("Windows x64 and a mapped EPOS product are required.");
            string prefix="Global\\CF4B411F-BE2B-4D84-8106-EC27CA0F8F05_1395_"+product;
            // Open existing objects only. Never create, acquire a mutex, notify or write.
            using (var mutex=Mutex.OpenExisting(prefix+"_mutex"))
            using (var map=MemoryMappedFile.OpenExisting(prefix+"_memory",MemoryMappedFileRights.Read))
            using (var changed=EventWaitHandle.OpenExisting(prefix+"_event")) {
                var handles=new Dictionary<long,string> {
                    {mutex.SafeWaitHandle.DangerousGetHandle().ToInt64(),prefix+"_mutex"},
                    {map.SafeMemoryMappedFileHandle.DangerousGetHandle().ToInt64(),prefix+"_memory"},
                    {changed.SafeWaitHandle.DangerousGetHandle().ToInt64(),prefix+"_event"}
                };
                IntPtr buffer=IntPtr.Zero;
                try {
                    int size=1024*1024, required=0, status;
                    while (true) {
                        buffer=Marshal.AllocHGlobal(size);
                        status=NtQuerySystemInformation(64,buffer,size,out required);
                        if (status==0) break;
                        Marshal.FreeHGlobal(buffer); buffer=IntPtr.Zero;
                        if (status!=unchecked((int)0xC0000004)) throw new InvalidOperationException("System handle query refused: 0x"+status.ToString("X8"));
                        size=Math.Max(size*2,required+65536);
                        if (size>128*1024*1024) throw new InvalidOperationException("System handle table exceeded the bounded diagnostic size.");
                    }
                    long count=Marshal.ReadIntPtr(buffer).ToInt64(); int stride=Marshal.SizeOf(typeof(HandleEntry));
                    if (count<0 || count>(size-16)/stride) throw new InvalidOperationException("Unknown system handle-table layout.");
                    int ownPid=Process.GetCurrentProcess().Id;
                    var objects=new Dictionary<long,string>();
                    for (long i=0;i<count;i++) {
                        var row=(HandleEntry)Marshal.PtrToStructure(IntPtr.Add(buffer,checked(16+(int)i*stride)),typeof(HandleEntry));
                        string name;
                        if (row.ProcessId.ToInt64()==ownPid && handles.TryGetValue(row.Handle.ToInt64(),out name)) {
                            if (row.Object==IntPtr.Zero) throw new UnauthorizedAccessException("Windows concealed object identities; administrator inspection is required.");
                            objects[row.Object.ToInt64()]=name;
                        }
                    }
                    if (objects.Count!=3) throw new InvalidOperationException("Cannot identify all opened EPOS objects in the system handle table.");
                    var results=new Dictionary<string,ApoHandleOwner>();
                    for (long i=0;i<count;i++) {
                        var row=(HandleEntry)Marshal.PtrToStructure(IntPtr.Add(buffer,checked(16+(int)i*stride)),typeof(HandleEntry));
                        string name;
                        if (!objects.TryGetValue(row.Object.ToInt64(),out name) || row.ProcessId.ToInt64()==ownPid) continue;
                        int pid=checked((int)row.ProcessId.ToInt64()); string key=pid+"|"+name;
                        ApoHandleOwner owner;
                        if (!results.TryGetValue(key,out owner)) {
                            string processName="Exited or inaccessible";
                            try { using (var process=Process.GetProcessById(pid)) processName=process.ProcessName; } catch (ArgumentException) {} catch (System.ComponentModel.Win32Exception) {}
                            owner=new ApoHandleOwner {ObjectName=name,ProcessId=pid,ProcessName=processName}; results.Add(key,owner);
                        }
                        owner.HandleCount++;
                    }
                    return new List<ApoHandleOwner>(results.Values).ToArray();
                } finally { if(buffer!=IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
            }
        }
    }
}
