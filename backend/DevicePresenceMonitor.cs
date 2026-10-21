using System;
using System.Threading;

namespace CMP90HX.Control {
    // Only OS device metadata belongs here. Never run NVAPI, IOCTLs or PnP changes.
    internal sealed class DevicePresenceMonitor : IDisposable {
        readonly Action refresh;
        readonly Action<Exception> failed;
        readonly Thread thread;
        volatile bool stopping;
        internal DevicePresenceMonitor(Action refresh,Action<Exception> failed) {
            this.refresh=refresh;this.failed=failed;
            thread=new Thread(Run) {IsBackground=true,Name="CMP90HX device presence"};
            thread.Start();
        }
        void Run() {
            while(!stopping) {
                try {refresh();}catch(Exception error) {if(!stopping)failed(error);}
                for(int i=0;i<10 && !stopping;i++)Thread.Sleep(100);
            }
        }
        public void Dispose() {
            stopping=true;
            // OS metadata queries can stall during device rebinding. They must
            // never prevent the hardware owner from finishing or shutting down.
            thread.Join(1500);
        }
    }
}
