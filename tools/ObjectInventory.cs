using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EposResearch {
    public sealed class NamedObject { public string Name { get; set; } public string Type { get; set; } }
    public static class ObjectInventory {
        [StructLayout(LayoutKind.Sequential)] struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Length; public IntPtr Root, Name; public uint Flags; public IntPtr Security, Quality; }
        [StructLayout(LayoutKind.Sequential)] struct Entry { public UnicodeString Name, Type; }
        [DllImport("ntdll.dll")] static extern int NtOpenDirectoryObject(out IntPtr handle, uint access, ref Attributes attrs);
        [DllImport("ntdll.dll")] static extern int NtQueryDirectoryObject(IntPtr handle, IntPtr buffer, uint length, [MarshalAs(UnmanagedType.U1)] bool single, [MarshalAs(UnmanagedType.U1)] bool restart, ref uint context, out uint returned);
        [DllImport("ntdll.dll")] static extern int NtClose(IntPtr handle);
        public static NamedObject[] List(string directory) {
            var name = new UnicodeString { Length=(ushort)(directory.Length*2), MaximumLength=(ushort)((directory.Length+1)*2), Buffer=Marshal.StringToHGlobalUni(directory) };
            var namePtr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
            IntPtr handle=IntPtr.Zero;
            try {
                Marshal.StructureToPtr(name,namePtr,false);
                var attrs=new Attributes {Length=Marshal.SizeOf(typeof(Attributes)),Name=namePtr,Flags=64};
                int status=NtOpenDirectoryObject(out handle,1,ref attrs);
                if(status<0) throw new InvalidOperationException("NtOpenDirectoryObject: "+status.ToString("X8"));
                var entries=new List<NamedObject>(); IntPtr buffer=Marshal.AllocHGlobal(8192);
                try {
                    uint context=0; bool restart=true;
                    for(int i=0;i<20000;i++) {
                        uint returned; status=NtQueryDirectoryObject(handle,buffer,8192,true,restart,ref context,out returned); restart=false;
                        if(status==unchecked((int)0x8000001A)) break;
                        if(status<0) throw new InvalidOperationException("NtQueryDirectoryObject: "+status.ToString("X8"));
                        var entry=(Entry)Marshal.PtrToStructure(buffer,typeof(Entry));
                        entries.Add(new NamedObject {Name=Marshal.PtrToStringUni(entry.Name.Buffer,entry.Name.Length/2),Type=Marshal.PtrToStringUni(entry.Type.Buffer,entry.Type.Length/2)});
                    }
                } finally {Marshal.FreeHGlobal(buffer);}
                return entries.ToArray();
            } finally {if(handle!=IntPtr.Zero)NtClose(handle);Marshal.FreeHGlobal(namePtr);Marshal.FreeHGlobal(name.Buffer);}
        }
    }
}
