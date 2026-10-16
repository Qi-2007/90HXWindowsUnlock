using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CMP90HX.Control {
    // A local, per-user test backend. It never accesses a GPU, tasks or settings.
    internal static class LegacyBackendFixture {
        static int Main(string[] args) {
            string marker=args[0];
            using(var singleton=ControlMemory.OpenLock(ControlMemory.DiagnosticPrefix+"Background.v1",true)) {
                bool owned=false;
                try {
                    try{owned=singleton.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
                    if(!owned)return 2;
                    using(var memory=new ControlMemory(true,true)) {
                        memory.ResetRequest();
                        var state=new ControlState {BackendRevision=3,Pid=Process.GetCurrentProcess().Id,Instance=Guid.NewGuid().ToString("N"),Busy=true};
                        var lifetime=Stopwatch.StartNew();
                        while(lifetime.ElapsedMilliseconds<30000) {
                            state.Heartbeat=DateTime.UtcNow;memory.Publish(state);
                            var request=memory.Request();
                            if(request!=null && request.Command=="Shutdown") {
                                state.Acknowledged=request.Id;memory.Publish(state);
                                File.WriteAllText(marker,"Shutdown");
                                Thread.Sleep(300); // Simulate a queued operation completing.
                                state.Results.Add(new IpcResult {Id=request.Id,Code=0});state.Busy=false;memory.Publish(state);
                                Thread.Sleep(500); // Acknowledgement is not process exit.
                                File.WriteAllText(marker+".exited","released");return 0;
                            }
                            Thread.Sleep(25);
                        }
                        return 3;
                    }
                } finally {if(owned)singleton.ReleaseMutex();}
            }
        }
    }
}
