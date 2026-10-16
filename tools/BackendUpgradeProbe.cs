using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class BackendUpgradeProbe {
    static int Main() {
        // Launch logic must run with the same base directory as the frontend.
        if(Environment.GetEnvironmentVariable("CMP90HX_IPC_DIAGNOSTIC")!="1")return 2;
        try {
            var assembly=Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CMP90HXControl.exe"));
            var type=assembly.GetType("CMP90HX.Control.BackendClient",true);
            var flags=BindingFlags.Static|BindingFlags.NonPublic;
            type.GetMethod("Ensure",flags).Invoke(null,new object[]{false});
            Console.WriteLine(new JavaScriptSerializer().Serialize(type.GetMethod("Read",flags).Invoke(null,new object[0])));return 0;
        } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}
