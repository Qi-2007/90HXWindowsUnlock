using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;

namespace CMP90HX.Control
{
    // Only interactive GUI windows are marked. SYSTEM background processes are excluded.
    internal sealed class DesktopPresence : IDisposable
    {
        delegate bool EnumWindow(IntPtr window,IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback,IntPtr parameter);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetProp(IntPtr window,string name,IntPtr value);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr window,string name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr window,string name);
        [DllImport("user32.dll",SetLastError=true)] static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,System.Text.StringBuilder text,int maximum);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr window,uint message,uint action,IntPtr change);
        static string Marker {get {using(var user=WindowsIdentity.GetCurrent()) return "CMP90HX.Desktop."+user.User.Value;}}
        readonly Window window;
        readonly string marker;
        readonly uint message;
        HwndSource source;
        IntPtr handle;
        internal static bool TryActivate(string channel=null)
        {
            string marker=Marker+(channel??"");uint message=RegisterWindowMessage(marker+".Activate");bool found=false;
            EnumWindows((handle,parameter)=> {
                if(GetProp(handle,marker)!=new IntPtr(1)) return true;
                found=PostMessage(handle,message,IntPtr.Zero,IntPtr.Zero);
                if(found) SetForegroundWindow(handle);
                return !found;
            },IntPtr.Zero);
            // Older desktop builds have no activation hook. Restore their titled
            // main window so an upgrade does not open a second interactive GUI.
            if(!found && channel==null) EnumWindows((handle,parameter)=> {
                var title=new System.Text.StringBuilder(128);GetWindowText(handle,title,title.Capacity);
                if(title.ToString()!="90HX Windows Unlock 控制台") return true;
                uint id;GetWindowThreadProcessId(handle,out id);
                try {using(var process=System.Diagnostics.Process.GetProcessById((int)id)) {
                    if(!String.Equals(process.ProcessName,"CMP90HXControl",StringComparison.OrdinalIgnoreCase)) return true;
                    ShowWindowAsync(handle,9);SetForegroundWindow(handle);found=true;return false;
                }} catch(ArgumentException) {return true;}
                catch(InvalidOperationException) {return true;}
                catch(Win32Exception) {return true;}
            },IntPtr.Zero);
            return found;
        }
        internal DesktopPresence(Window window,string channel=null)
        {
            this.window=window;marker=Marker+(channel??"");message=RegisterWindowMessage(marker+".Activate");
            window.SourceInitialized+=Attach;
        }
        void Attach(object sender,EventArgs args)
        {
            handle=new WindowInteropHelper(window).Handle;source=HwndSource.FromHwnd(handle);
            // Permit the harmless activate-only message from a non-elevated second launch.
            if(!ChangeWindowMessageFilterEx(handle,message,1,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            source.AddHook(Receive);
            if(!SetProp(handle,marker,new IntPtr(1))) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        IntPtr Receive(IntPtr hwnd,int msg,IntPtr wparam,IntPtr lparam,ref bool handled)
        {
            if(unchecked((uint)msg)==message) {
                handled=true;
                Window target=window;
                foreach(Window other in Application.Current.Windows)
                    if(other!=window && other.IsVisible && other.Owner==window) {target=other;break;}
                if(!target.IsVisible) target.Show();
                if(target.WindowState==WindowState.Minimized) target.WindowState=WindowState.Normal;
                target.Activate();SetForegroundWindow(new WindowInteropHelper(target).Handle);
            }
            return IntPtr.Zero;
        }
        public void Dispose()
        {
            window.SourceInitialized-=Attach;
            if(handle!=IntPtr.Zero) RemoveProp(handle,marker);
            if(source!=null && !source.IsDisposed) source.RemoveHook(Receive);
        }
    }
}
