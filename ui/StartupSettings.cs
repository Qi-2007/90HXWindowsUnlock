using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace CMP90HX.Control
{
    internal static class StartupSettings
    {
        internal static bool AutoUnlockDisabled(bool commandLineDisabled,string root)
        {
            // The recovery switch works even if the on-disk settings are invalid.
            if(commandLineDisabled) return true;
            string path=Path.Combine(root,"settings.json");
            if(!File.Exists(path)) return false;
            var sections=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            if(sections==null) throw new IOException("配置文件无效："+path);
            object value;
            if(!sections.TryGetValue("DisableAutoUnlock",out value)) return false;
            if(!(value is bool)) throw new IOException("DisableAutoUnlock 必须为 true 或 false："+path);
            return (bool)value;
        }
    }
}
