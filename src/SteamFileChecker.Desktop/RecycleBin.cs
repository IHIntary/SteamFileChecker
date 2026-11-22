using System.Runtime.InteropServices;

namespace SteamFileChecker.Desktop;

/// <summary>Moves files to the Recycle Bin through the Windows shell, never deleting permanently without the shell asking first.</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    /// <summary>Warn before a file is permanently destroyed because it cannot be recycled (overrides FOF_NOCONFIRMATION for that case).</summary>
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    /// <summary>
    /// Runs the shell operation on its own STA thread so the window keeps responding while Windows shows its progress dialog.
    /// Callers check which files are gone afterwards; per-file failures (e.g. files in use) are reported by the shell itself.
    /// </summary>
    public static Task SendAsync(IntPtr owner, IReadOnlyList<string> paths)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var chunk in paths.Chunk(1000))
                {
                    var operation = new SHFILEOPSTRUCT
                    {
                        hwnd = owner,
                        wFunc = FO_DELETE,
                        pFrom = string.Join('\0', chunk) + "\0\0", // double-null-terminated list
                        fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
                    };
                    SHFileOperation(ref operation);
                    if (operation.fAnyOperationsAborted) break;
                }
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }
}
