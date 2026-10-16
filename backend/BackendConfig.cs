using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CMP90HX.Control {
    internal static class BackendConfig {
        internal static ControlOptions Read() {
            string file=Path.Combine(PowerStorage.Root,"settings.json");
            if(!File.Exists(file)) return new ControlOptions();
            var json=ControlMemory.Json();var sections=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));object value;
            if(sections==null) throw new IOException("settings.json 无效。");
            var result=sections.TryGetValue("Control",out value)?json.ConvertToType<ControlOptions>(value):new ControlOptions();
            if(result==null) throw new IOException("Control 配置无效。");result.ReadUnlockOnConnect=false;result.Validate();return result;
        }
        internal static void Save(ControlOptions options) {
            options.ReadUnlockOnConnect=false;options.Validate();
            using(var gate=new DeploymentLock()) {
                DriverService.ProtectDirectory(PowerStorage.Root);
                string file=Path.Combine(PowerStorage.Root,"settings.json");
                if(File.Exists(file) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("配置不能是符号链接。");
                var json=ControlMemory.Json();var sections=File.Exists(file)?json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file)):new Dictionary<string,object>();
                if(sections==null) throw new IOException("配置无效。");sections["Control"]=options;
                string temporary=Path.Combine(PowerStorage.Root,Guid.NewGuid().ToString("N")+".tmp");
                try {File.WriteAllText(temporary,json.Serialize(sections),Encoding.UTF8);if(File.Exists(file))File.Replace(temporary,file,null);else File.Move(temporary,file);}
                finally {if(File.Exists(temporary)) File.Delete(temporary);}
            }
        }
    }
}
