using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace OstConverter.Tests;

/// <summary>
/// Reads a compound file through Windows' own OLE structured-storage implementation (the same one Outlook uses),
/// so tests validate our writer against the real thing rather than against our own idea of the format.
/// </summary>
static class OleReader
{
    [ComImport, Guid("0000000b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint r1, uint r2, out IStream stream);
        void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr r1, uint mode, uint r2, out IStream stream);
        void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint r1, uint r2, out IStorage storage);
        void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage? prio, uint mode, IntPtr snb, uint r, out IStorage storage);
        void CopyTo(uint ciidExclude, IntPtr rgiidExclude, IntPtr snbExclude, IStorage dest);
        void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage dest, [MarshalAs(UnmanagedType.LPWStr)] string newName, uint flags);
        void Commit(uint flags);
        void Revert();
        void EnumElements(uint r1, IntPtr r2, uint r3, out IEnumSTATSTG enumerator);
        void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string name);
        void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string oldName, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string name, FILETIME? c, FILETIME? a, FILETIME? m);
        void SetClass(ref Guid clsid);
        void SetStateBits(uint bits, uint mask);
        void Stat(out STATSTG stat, uint flag);
    }

    [ComImport, Guid("0000000d-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSTATSTG
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.LPArray), Out] STATSTG[] items, out uint fetched);
        void Skip(uint count);
        void Reset();
        void Clone(out IEnumSTATSTG clone);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int StgOpenStorage(string name, IStorage? prio, uint mode, IntPtr snb, uint reserved, out IStorage storage);

    const uint StgmRead = 0, StgmShareExclusive = 0x10;
    const int TypeStorage = 1, TypeStream = 2;

    public sealed record Result(Dictionary<string, byte[]> Streams, List<string> Storages, Guid RootClsid);

    public static Result Read(string path)
    {
        int hr = StgOpenStorage(path, null, StgmRead | StgmShareExclusive, IntPtr.Zero, 0, out var root);
        if (hr != 0) throw new InvalidDataException($"Windows could not open the compound file (HRESULT 0x{hr:X8}).");
        try
        {
            var streams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var storages = new List<string>();
            root.Stat(out var rootStat, 1);
            Walk(root, "", streams, storages);
            return new Result(streams, storages, rootStat.clsid);
        }
        finally { Marshal.ReleaseComObject(root); }
    }

    static void Walk(IStorage storage, string prefix, Dictionary<string, byte[]> streams, List<string> storages)
    {
        storage.EnumElements(0, IntPtr.Zero, 0, out var en);
        var item = new STATSTG[1];
        var names = new List<(string Name, int Type, long Size)>();
        while (en.Next(1, item, out uint fetched) == 0 && fetched == 1)
            names.Add((item[0].pwcsName, item[0].type, item[0].cbSize));
        Marshal.ReleaseComObject(en);

        foreach (var (name, type, size) in names)
        {
            var full = prefix + name;
            if (type == TypeStream)
            {
                storage.OpenStream(name, IntPtr.Zero, StgmRead | StgmShareExclusive, 0, out var s);
                var buf = new byte[size];
                var pRead = Marshal.AllocCoTaskMem(4);
                try
                {
                    int got = 0;
                    while (got < size)
                    {
                        var chunk = new byte[Math.Min(1 << 20, size - got)];
                        s.Read(chunk, chunk.Length, pRead);
                        int n = Marshal.ReadInt32(pRead);
                        if (n == 0) throw new InvalidDataException($"Stream {full} ended early at {got} of {size} bytes.");
                        Array.Copy(chunk, 0, buf, got, n);
                        got += n;
                    }
                }
                finally { Marshal.FreeCoTaskMem(pRead); Marshal.ReleaseComObject(s); }
                streams[full] = buf;
            }
            else if (type == TypeStorage)
            {
                storages.Add(full);
                storage.OpenStorage(name, null, StgmRead | StgmShareExclusive, IntPtr.Zero, 0, out var child);
                try { Walk(child, full + "/", streams, storages); }
                finally { Marshal.ReleaseComObject(child); }
            }
        }
    }
}
