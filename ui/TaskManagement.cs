using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Web.Script.Serialization;

namespace CMP90HX.Control
{
    internal sealed class TaskStatus
    {
        internal bool Exists;
        internal string Text;
    }
    internal static class EnvironmentSetup
    {
        internal static void Prepare(Action validate,Func<bool> certificates,Action installCertificates,
            Func<bool> driver,Action installDriver,Func<int> environmentCheck,Action registerTask,Action<string> log)
        {
            validate();
            if(!certificates()) installCertificates();
            if(!certificates()) throw new IOException("证书安装后检查未通过，未注册计划任务。");
            if(!driver()) installDriver();
            if(!driver()) throw new IOException("驱动安装后检查未通过，未注册计划任务。");
            if(environmentCheck()!=0) throw new IOException("环境检查未通过，未注册计划任务。请查看具体失败项。");
            log("ENVIRONMENT_READY_FOR_TASK");
            registerTask();
        }
    }
    internal static class TaskManagement
    {
        internal const string Name="CMP90HX Auto Unlock";
        internal const string Source="CMP90HX.Control";
        internal const string WakeQuery="<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107]]</Select></Query></QueryList>";
        static string Escape(string value) { return SecurityElement.Escape(value); }
        internal static string Xml(string executable)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"+
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"+
                "<RegistrationInfo><Description>CMP 90HX 开机及睡眠唤醒后自动检查并解锁。</Description><Source>"+Source+"</Source></RegistrationInfo>"+
                "<Triggers><BootTrigger><Enabled>true</Enabled><Delay>PT20S</Delay></BootTrigger>"+
                "<EventTrigger><Enabled>true</Enabled><Subscription>"+Escape(WakeQuery)+"</Subscription><Delay>PT10S</Delay></EventTrigger></Triggers>"+
                "<Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"+
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"+
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate>"+
                "<StartWhenAvailable>true</StartWhenAvailable><Enabled>true</Enabled><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>"+
                "<Actions Context=\"System\"><Exec><Command>"+Escape(executable)+"</Command><Arguments>--auto-unlock</Arguments>"+
                "<WorkingDirectory>"+Escape(Path.GetDirectoryName(executable))+"</WorkingDirectory></Exec></Actions></Task>";
        }
        internal static void ValidateDefinition(string executable)
        {
            object service=null,definition=null;
            try {
                service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
                ((dynamic)service).Connect();definition=((dynamic)service).NewTask(0);
                ((dynamic)definition).XmlText=Xml(executable);
            } finally { Release(definition);Release(service); }
        }
        static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        static T Scheduler<T>(Func<dynamic,T> action)
        {
            object service=null,root=null;
            try {
                service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
                ((dynamic)service).Connect(); root=((dynamic)service).GetFolder(@"\");
                return action((dynamic)root);
            } finally { Release(root); Release(service); }
        }
        static object Find(dynamic root)
        {
            try { return root.GetTask(Name); }
            catch(FileNotFoundException) { return null; }
            catch(COMException error) { if(error.ErrorCode==unchecked((int)0x80070002)) return null; throw; }
        }
        static void RequireOwned(dynamic task)
        {
            object definition=null,info=null;
            try {
                definition=task.Definition; info=((dynamic)definition).RegistrationInfo;
                if((string)((dynamic)info).Source!=Source) throw new IOException("同名计划任务属于其他程序，拒绝修改。");
            } finally { Release(info); Release(definition); }
        }
        internal static TaskStatus Query()
        {
            return Scheduler<TaskStatus>(root=> {
                object task=Find(root);
                if(task==null) return new TaskStatus {Text="未安装",Exists=false};
                try {
                    RequireOwned((dynamic)task);
                    dynamic value=task; int state=value.State;
                    DateTime last=value.LastRunTime; int result=value.LastTaskResult;
                    string summary=state==4?"运行中":value.Enabled?"已安装 · 已启用":"已安装 · 已禁用";
                    string time=last.Year>2000?last.ToString("yyyy-MM-dd HH:mm:ss"):"尚未运行";
                    return new TaskStatus {Exists=true,Text=summary+"\n上次运行："+time+"\n上次结果：0x"+unchecked((uint)result).ToString("X8")};
                } finally { Release(task); }
            });
        }
        internal static void Register(RuntimePaths paths,Action<string> log)
        {
            string executable=Deploy(paths);
            Scheduler<int>(root=> {
                object previous=Find(root),registered=null;
                try {
                    if(previous!=null) RequireOwned((dynamic)previous);
                    registered=root.RegisterTask(Name,Xml(executable),6,"SYSTEM",null,5,"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
                    log("TASK_INSTALLED name="+Name+" account=SYSTEM boot=20s wake=Kernel-Power/107+10s");
                    log("TASK_EXECUTABLE "+executable); return 0;
                } finally { Release(registered); Release(previous); }
            });
        }
        internal static void Uninstall(Action<string> log)
        {
            Scheduler<int>(root=> {
                object task=Find(root);
                try {
                    if(task==null) { log("TASK_UNINSTALLED: 计划任务已不存在。"); return 0; }
                    RequireOwned((dynamic)task);
                    // Remove registration without stopping an in-flight hardware process.
                    root.DeleteTask(Name,0); log("TASK_UNINSTALLED: 已移除后续触发，正在运行的解锁会继续完成。"); return 0;
                } finally { Release(task); }
            });
        }
        internal static void Run(Action<string> log)
        {
            Scheduler<int>(root=> {
                object task=Find(root),running=null;
                try {
                    if(task==null) throw new IOException("尚未安装计划任务。");
                    RequireOwned((dynamic)task);
                    running=((dynamic)task).Run(null);
                    log("TASK_MANUAL_TRIGGERED: 已请求后台运行，结果请查看计划任务状态及自动解锁日志。"); return 0;
                } finally { Release(running); Release(task); }
            });
        }
        static string Deploy(RuntimePaths paths)
        {
            paths.Validate(true,true); CertificateManager.RequireInstalled();
            string appRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");
            DriverService.ProtectDirectory(appRoot);
            string root=Path.Combine(appRoot,"AutoUnlock"); DriverService.ProtectDirectory(root);
            string sourceGui=paths.Packaged?Path.Combine(paths.Root,"CMP90HXControl.exe"):Path.Combine(paths.Root,"build","CMP90HXControl.exe");
            string destination=Path.Combine(root,RuntimePaths.Version+"-"+RuntimePaths.Hash(sourceGui).Substring(0,16));
            DriverService.ProtectDirectory(destination);
            var inputs=new Dictionary<string,string> {
                {"CMP90HXControl.exe",sourceGui},{"CMP90HXControl.exe.config",sourceGui+".config"},
                {"runtime/CMP90HXGen2.exe",paths.Worker},{"core/nvpermissive-core.o",paths.Core},
                {"driver/CMP90HXDmaSigned.sys",paths.DmaDriver},
                {"drivers/WinRing0x64.sys",Path.Combine(paths.Drivers,"WinRing0x64.sys")},
                {"drivers/ThrottleStop.sys",Path.Combine(paths.Drivers,"ThrottleStop.sys")}
            };
            foreach(string name in CertificateManager.Names) inputs.Add("driver/cert/"+name,Path.Combine(paths.Certificates,name));
            var hashes=new Dictionary<string,string>();
            foreach(var file in inputs) {
                string target=Path.Combine(destination,file.Key.Replace('/',Path.DirectorySeparatorChar));
                string parent=Path.GetDirectoryName(target);
                // Protect every directory on the way to privileged executable files.
                string relative=parent.Substring(destination.Length).TrimStart(Path.DirectorySeparatorChar);
                string current=destination;
                foreach(string part in relative.Split(new[]{Path.DirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries)) {
                    current=Path.Combine(current,part); DriverService.ProtectDirectory(current);
                }
                RejectLink(target);
                string hash=RuntimePaths.Hash(file.Value);
                if(!File.Exists(target) || RuntimePaths.Hash(target)!=hash) File.Copy(file.Value,target,true);
                RuntimePaths.RequireHash(target,hash); hashes.Add(file.Key,hash);
            }
            string manifest=Path.Combine(destination,"files.sha256.json"); RejectLink(manifest);
            File.WriteAllText(manifest,new JavaScriptSerializer().Serialize(hashes));
            new RuntimePaths(destination).Validate(true,true);
            return Path.Combine(destination,"CMP90HXControl.exe");
        }
        static void RejectLink(string file)
        { if(File.Exists(file) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("拒绝写入符号链接："+file); }
    }
}
