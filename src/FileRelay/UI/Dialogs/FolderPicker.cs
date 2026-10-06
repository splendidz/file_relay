using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileRelay.UI
{
    /// <summary>탐색기 스타일 폴더 선택 창 (IFileOpenDialog + FOS_PICKFOLDERS). 실패 시 FolderBrowserDialog.</summary>
    public static class FolderPicker
    {
        public static string Pick(IWin32Window owner, string title, string initial = null)
        {
            try
            {
                var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                try
                {
                    dlg.GetOptions(out uint opts);
                    dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                    if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                    if (!string.IsNullOrEmpty(initial) && System.IO.Directory.Exists(initial))
                    {
                        var iid = typeof(IShellItem).GUID;
                        if (SHCreateItemFromParsingName(initial, IntPtr.Zero, ref iid, out IShellItem item) == 0)
                            dlg.SetFolder(item);
                    }
                    int hr = dlg.Show(owner?.Handle ?? IntPtr.Zero);
                    if (hr != 0) return null;
                    dlg.GetResult(out IShellItem result);
                    result.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr p);
                    string path = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    return path;
                }
                finally { Marshal.ReleaseComObject(dlg); }
            }
            catch
            {
                using (var fb = new FolderBrowserDialog { Description = title, SelectedPath = initial ?? "", ShowNewFolderButton = true })
                    return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
            }
        }

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_NOCHANGEDIR = 0x8;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
