using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;

namespace CMP90HX.Control {
    internal static class BackendStartup {
        internal const string TaskName="CMP90HX Background";
        const string Source="CMP90HX.Control.Background";
        internal static string Xml(string executable) {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"+
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"+
                "<RegistrationInfo><Source>"+Source+"</Source><Description>CMP 90HX 统一后台：开机启动，按设置执行解锁与省电。</Description></RegistrationInfo>"+
                "<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>"+
                "<Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"+
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"+
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>"+
                "<Enabled>true</Enabled><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>"+
                "<Actions Context=\"System\"><Exec><Command>"+SecurityElement.Escape(executable)+"</Command><Arguments>--startup</Arguments>"+
                "<WorkingDirectory>"+SecurityElement.Escape(Path.GetDirectoryName(executable))+"</WorkingDirectory></Exec></Actions></Task>";
        }
        static void Release(object value) {if(value!=null && Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
        static object Find(dynamic folder) {
            try{return folder.GetTask(TaskName);}catch(FileNotFoundException){return null;}
            catch(COMException error){if(error.ErrorCode==unchecked((int)0x80070002))return null;throw;}
        }
        static void RequireOwned(dynamic task) {
            object definition=null,info=null;
            try {definition=task.Definition;info=((dynamic)definition).RegistrationInfo;
                if((string)((dynamic)info).Source!=Source)throw new IOException("同名开机任务属于其他程序，未修改。");
            } finally {Release(info);Release(definition);}
        }
        internal static string PackagePath(string root,string relative) {
            if(String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':')>=0 ||
                relative.Split(new[]{'/','\\'}).Any(part=>part==".." || part=="." || part.Length==0))throw new IOException("后台包文件路径无效。");
            string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string file=Path.GetFullPath(Path.Combine(prefix,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("后台包文件超出运行目录。");
            return file;
        }
        static void RejectLinks(string root,string file) {
            string current=file;
            while(current!=null && current.StartsWith(root,StringComparison.OrdinalIgnoreCase)) {
                if((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("后台包不能包含链接："+current);
                if(String.Equals(current,root,StringComparison.OrdinalIgnoreCase))break;current=Path.GetDirectoryName(current);
            }
        }
        static string Deploy(RuntimePaths paths,Action<string> log) {
            string source=Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar);
            string manifest=Path.Combine(source,"files.sha256.json");RejectLinks(source,manifest);
            string text=File.ReadAllText(manifest);var files=ControlMemory.Json().Deserialize<Dictionary<string,string>>(text);
            if(files==null || !files.ContainsKey("CMP90HXBackground.exe") || !files.ContainsKey("CMP90HXBackground.exe.config"))throw new IOException("缺少完整的后台运行包清单，请重新构建 Console。");
            // Validate every input before registering privileged execution. No driver
            // installation, GPU access or application launch is needed to deploy it.
            foreach(var item in files) {
                string input=PackagePath(source,item.Key);RejectLinks(source,input);
                if(!Regex.IsMatch(item.Value??"",@"\A[0-9a-fA-F]{64}\z"))throw new IOException("后台包哈希无效。");
                RuntimePaths.RequireHash(input,item.Value.ToUpperInvariant());
            }
            DriverService.ProtectDirectory(PowerStorage.Root);DriverService.ProtectDirectory(RuntimeDeployment.Root);
            string destination=Path.Combine(RuntimeDeployment.Root,RuntimePaths.Version+"-"+RuntimePaths.Hash(manifest).Substring(0,16));
            DriverService.ProtectDirectory(destination);
            foreach(var item in files) {
                string output=PackagePath(destination,item.Key);RejectLinks(destination,output);
                string relative=Path.GetDirectoryName(output).Substring(destination.Length).TrimStart(Path.DirectorySeparatorChar),current=destination;
                foreach(string part in relative.Split(new[]{Path.DirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries)) {current=Path.Combine(current,part);DriverService.ProtectDirectory(current);}
                if(!File.Exists(output) || !String.Equals(RuntimePaths.Hash(output),item.Value,StringComparison.OrdinalIgnoreCase))File.Copy(PackagePath(source,item.Key),output,true);
                RuntimePaths.RequireHash(output,item.Value.ToUpperInvariant());
            }
            string targetManifest=Path.Combine(destination,"files.sha256.json");RejectLinks(destination,targetManifest);
            if(!File.Exists(targetManifest) || File.ReadAllText(targetManifest)!=text)File.Copy(manifest,targetManifest,true);
            log("BACKGROUND_STARTUP_DEPLOYED "+destination);return Path.Combine(destination,"CMP90HXBackground.exe");
        }
        internal static void Update(bool enabled,RuntimePaths paths,Action save,Action<string> log) {
            using(var deployment=new DeploymentLock()) {
                object service=null,folder=null,previous=null,registered=null;string priorXml=null;bool changed=false;
                try {
                    service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");previous=Find((dynamic)folder);
                    if(previous!=null) {RequireOwned((dynamic)previous);priorXml=(string)((dynamic)previous).Xml;}
                    if(enabled) {
                        string executable=Deploy(paths,log);
                        registered=((dynamic)folder).RegisterTask(TaskName,Xml(executable),6,"SYSTEM",null,5,"D:P(A;;GA;;;SY)(A;;GA;;;BA)");changed=true;
                    } else if(previous!=null) {((dynamic)folder).DeleteTask(TaskName,0);changed=true;}
                    try {save();}
                    catch {
                        if(changed) {
                            try {
                                if(priorXml!=null) {object restored=null;try {restored=((dynamic)folder).RegisterTask(TaskName,priorXml,6,"SYSTEM",null,5,"D:P(A;;GA;;;SY)(A;;GA;;;BA)");}finally{Release(restored);}}
                                else ((dynamic)folder).DeleteTask(TaskName,0);
                            } catch(Exception rollback) {log("BACKGROUND_STARTUP_ROLLBACK_FAILED: "+rollback.Message);}
                        }
                        throw;
                    }
                    log(enabled?"BACKGROUND_STARTUP_ENABLED: SYSTEM 开机启动，解锁与省电按各自设置执行。":"BACKGROUND_STARTUP_DISABLED: 已移除开机启动，当前后台继续运行。");
                } finally {Release(registered);Release(previous);Release(folder);Release(service);}
            }
        }
    }
}
