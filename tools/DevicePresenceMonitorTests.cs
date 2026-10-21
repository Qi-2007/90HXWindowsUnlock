using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CMP90HX.Control {
    internal static class DevicePresenceMonitorTests {
        static int checks;
        static void Check(bool value,string name) {
            if(!value)throw new Exception(name);
            checks++;Console.WriteLine("PASS "+name);
        }
        static bool Until(Func<bool> ready) {
            var wait=Stopwatch.StartNew();
            while(wait.ElapsedMilliseconds<5000) {if(ready())return true;Thread.Sleep(20);}
            return false;
        }
        static int Main() {
            try {
                int reads=0,problem=0,displayed=-1,errors=0,fail=0;
                using(var operation=new ManualResetEvent(false)) {
                    var worker=new Thread(()=>operation.WaitOne());worker.Start();
                    try {
                        var monitor=new DevicePresenceMonitor(()=> {
                            if(Interlocked.Exchange(ref fail,0)!=0)throw new IOException("device rebinding");
                            Interlocked.Exchange(ref displayed,Interlocked.CompareExchange(ref problem,0,0));
                            Interlocked.Increment(ref reads);
                        },error=>Interlocked.Increment(ref errors));
                        try {
                            Check(Until(()=>Interlocked.CompareExchange(ref displayed,0,0)==0),"initial device status arrives while hardware worker is blocked");
                            Interlocked.Exchange(ref problem,22);
                            Check(Until(()=>Interlocked.CompareExchange(ref displayed,0,0)==22),"Code 22 is observed before unlock completes");
                            Interlocked.Exchange(ref fail,1);
                            Check(Until(()=>Interlocked.CompareExchange(ref errors,0,0)==1),"transient metadata failure is reported independently");
                            Interlocked.Exchange(ref problem,0);
                            Check(Until(()=>Interlocked.CompareExchange(ref displayed,0,0)==0),"device status resumes after rebinding without restarting monitor");
                            Check(worker.IsAlive,"monitor never releases or completes the hardware operation");
                        } finally {monitor.Dispose();}
                        int stopped=Interlocked.CompareExchange(ref reads,0,0);Thread.Sleep(1100);
                        Check(Interlocked.CompareExchange(ref reads,0,0)==stopped,"normal disposal stops further polling");
                    } finally {operation.Set();worker.Join();}
                }
                using(var entered=new ManualResetEvent(false))
                using(var release=new ManualResetEvent(false))
                using(var returned=new ManualResetEvent(false)) {
                    var stalled=new DevicePresenceMonitor(()=> {entered.Set();release.WaitOne();returned.Set();},error=>{});
                    try {
                        Check(entered.WaitOne(5000),"stalled OS query fixture entered");
                        var elapsed=Stopwatch.StartNew();stalled.Dispose();
                        Check(elapsed.ElapsedMilliseconds<3000,"stalled OS metadata cannot block backend shutdown indefinitely");
                    } finally {release.Set();returned.WaitOne(5000);stalled.Dispose();}
                }
                Console.WriteLine("DEVICE_PRESENCE_TESTS_PASSED count="+checks+"; mock callbacks only");return 0;
            }catch(Exception error) {Console.Error.WriteLine(error);return 1;}
        }
    }
}
