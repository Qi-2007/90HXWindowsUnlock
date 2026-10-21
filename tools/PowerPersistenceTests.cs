using System;
using System.Collections.Generic;
using System.IO;

namespace CMP90HX.Control {
    internal interface IPowerGpu : IDisposable {
        PowerSample Read();
        void Limit(int state);
    }
    internal sealed class PersistenceGpu : IPowerGpu {
        internal PowerSample Sample=new PowerSample();
        internal readonly List<int> Writes=new List<int>();
        internal Action<int> BeforeWrite;
        public PowerSample Read() {return Sample;}
        public void Limit(int state) {if(BeforeWrite!=null)BeforeWrite(state);Writes.Add(state);}
        public void Dispose(){}
    }
    internal static class PowerPersistenceTests {
        static int checks;
        static void Check(bool condition,string name) {
            if(!condition)throw new Exception(name);
            Console.WriteLine("PASS "+name);checks++;
        }
        static int Main() {
            string journal=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"intent-"+Guid.NewGuid().ToString("N")+".json");
            try {
                int persisted=0;bool owned=false;
                Action<bool> persist=limited=> {File.WriteAllText(journal,limited?"true":"false");persisted++;owned=limited;};
                var gpu=new PersistenceGpu();bool recordedBeforeP8=false,releasedBeforeClear=false;
                gpu.BeforeWrite=state=> {if(state==8)recordedBeforeP8=owned && File.ReadAllText(journal)=="true";if(state==0)releasedBeforeClear=owned;};
                var engine=new PowerEngine(gpu,persist);
                var settings=new PowerSettings {Enabled=true,IdleSeconds=1};
                var start=new DateTime(2026,10,21,0,0,0,DateTimeKind.Utc);
                engine.Tick(settings,false,false,false,start);
                Check(persisted==0 && !File.Exists(journal),"initial live telemetry does not create a status file");
                engine.Tick(settings,false,false,false,start.AddSeconds(1));
                Check(recordedBeforeP8 && persisted==1,"P8 ownership is persisted before applying limit");
                for(int i=2;i<32;i++){gpu.Sample.Watts=i;engine.Tick(settings,false,false,false,start.AddSeconds(i));}
                Check(persisted==1 && gpu.Writes.Count==1,"thirty changing telemetry samples do not rewrite the recovery journal");
                gpu.Sample.Gpu=100;engine.Tick(settings,false,false,false,start.AddSeconds(32));
                Check(releasedBeforeClear && persisted==2 && !owned && File.ReadAllText(journal)=="false","ownership is cleared only after restoring automatic performance");
                for(int i=33;i<63;i++)engine.Tick(settings,false,false,false,start.AddSeconds(i));
                Check(persisted==2,"automatic-performance telemetry does not rewrite the journal");
                gpu.BeforeWrite=null;
                var recovered=new PowerEngine(gpu,persist,true);settings.Enabled=false;
                recovered.Tick(settings,false,false,true,start.AddSeconds(63));
                Check(persisted==3 && !recovered.Limited && gpu.Writes[gpu.Writes.Count-1]==0,"restart recovery still releases a persisted limit even when power saving is disabled");
                Console.WriteLine("POWER_PERSISTENCE_TESTS_PASSED count="+checks+"; mock GPU only");return 0;
            }catch(Exception error){Console.Error.WriteLine(error);return 1;}
            finally {if(File.Exists(journal))File.Delete(journal);}
        }
    }
}
