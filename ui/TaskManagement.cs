using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Web.Script.Serialization;
using System.Xml;
using System.Text;

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
        internal const string PowerName="CMP90HX Idle Power Saver";
        internal const string CleanupName="CMP90HX Uninstall Cleanup";
        internal static string CleanupScript()
        {
            string target=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"CMP90HX");
            return "$ErrorActionPreference='Stop'\r\n"+
                "$root='"+target.Replace("'","''")+"'\r\n"+
                "$expected=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CMP90HX'\r\n"+
                "if(![string]::Equals([IO.Path]::GetFullPath($root),$expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup root'}\r\n"+
                "if(@(Get-Service -ErrorAction Stop | Where-Object {$_.Name -eq 'CMP90HXDma'}).Count){throw 'Driver still installed; cleanup deferred'}\r\n"+
                "function Assert-NoLinks($path){$item=Get-Item -LiteralPath $path -Force; if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked path refused'}; if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $path -Force){Assert-NoLinks $child.FullName}}}\r\n"+
                "if(Test-Path -LiteralPath $root){Assert-NoLinks $root; Remove-Item -LiteralPath $root -Recurse -Force}\r\n"+
                "if(Test-Path -LiteralPath $root){throw 'Cleanup incomplete'}\r\n"+
                "$scheduler=New-Object -ComObject Schedule.Service; $scheduler.Connect(); $scheduler.GetFolder('\\').DeleteTask('"+CleanupName+"',0)\r\n";
        }
        internal static string CleanupXml()
        {
            string shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            var doc=new XmlDocument();doc.LoadXml(PowerXml(shell));
            var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t",doc.DocumentElement.NamespaceURI);
            doc.SelectSingleNode("//t:Description",ns).InnerText="CMP90HX 卸载后，在下次启动清空程序数据并移除此任务。";
            doc.SelectSingleNode("//t:Arguments",ns).InnerText="-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupScript()));
            var restart=doc.CreateElement("RestartOnFailure",doc.DocumentElement.NamespaceURI);
            var interval=doc.CreateElement("Interval",doc.DocumentElement.NamespaceURI);interval.InnerText="PT1M";restart.AppendChild(interval);
            var attempts=doc.CreateElement("Count",doc.DocumentElement.NamespaceURI);attempts.InnerText="5";restart.AppendChild(attempts);
            doc.SelectSingleNode("//t:Settings",ns).AppendChild(restart);
            return doc.OuterXml;
        }
        internal static void UninstallAll(Action<string> log)
        {
            // Reject foreign tasks and invalid definitions before changing the installation.
            Query();Query(PowerName);Query(CleanupName);
            ValidateCleanupDefinition();
            PowerTasks.Disable(log);
            TaskManagement.Uninstall(log);
            using(var gate=new PowerHardwareGate()) DriverService.Uninstall(log);
            CertificateManager.Uninstall(log);
            Scheduler<int>(root=> {
                object previous=Find(root,CleanupName),registered=null;
                try {
                    if(previous!=null) RequireOwned((dynamic)previous);
                    registered=root.RegisterTask(CleanupName,CleanupXml(),6,"SYSTEM",null,5,"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
                    log("UNINSTALL_CLEANUP_SCHEDULED: 下次重启后清空 ProgramData\\CMP90HX。");return 0;
                } finally {Release(registered);Release(previous);}
            });
        }
        internal static void ValidateCleanupDefinition()
        {
            object service=null,definition=null;
            try {
                service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
                ((dynamic)service).Connect();definition=((dynamic)service).NewTask(0);
                ((dynamic)definition).XmlText=CleanupXml();
            } finally {Release(definition);Release(service);}
        }
        internal const string WakeQuery="<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=107]]</Select></Query></QueryList>";
        static string Escape(string value) { return SecurityElement.Escape(value); }
        internal static string Xml(string executable)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>"+
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"+
                "<RegistrationInfo><Description>CMP 90HX 开机及睡眠唤醒后自动检查并解锁。</Description><Source>"+Source+"</Source></RegistrationInfo>"+
                "<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger>"+
                "<EventTrigger><Enabled>true</Enabled><Subscription>"+Escape(WakeQuery)+"</Subscription></EventTrigger></Triggers>"+
                "<Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"+
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"+
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate>"+
                "<StartWhenAvailable>true</StartWhenAvailable><Enabled>true</Enabled><WakeToRun>false</WakeToRun><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>"+
                "<Actions Context=\"System\"><Exec><Command>"+Escape(executable)+"</Command><Arguments>--auto-unlock</Arguments>"+
                "<WorkingDirectory>"+Escape(Path.GetDirectoryName(executable))+"</WorkingDirectory></Exec></Actions></Task>";
        }
        internal static string PowerXml(string executable)
        {
            var doc=new XmlDocument();doc.LoadXml(Xml(executable));
            var ns=new XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t",doc.DocumentElement.NamespaceURI);
            var wake=doc.SelectSingleNode("//t:EventTrigger",ns);wake.ParentNode.RemoveChild(wake);
            doc.SelectSingleNode("//t:Description",ns).InnerText="CMP 90HX 空闲省电后台控制，解锁期间暂停，设备恢复后重新检测。";
            doc.SelectSingleNode("//t:Arguments",ns).InnerText="--power-monitor";
            return doc.OuterXml;
        }
        internal static void ValidateDefinition(string executable,bool power=false)
        {
            object service=null,definition=null;
            try {
                service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));
                ((dynamic)service).Connect();definition=((dynamic)service).NewTask(0);
                ((dynamic)definition).XmlText=power?PowerXml(executable):Xml(executable);
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
        static object Find(dynamic root,string name=Name)
        {
            try { return root.GetTask(name); }
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
        internal static TaskStatus Query(string name=Name)
        {
            return Scheduler<TaskStatus>(root=> {
                object task=Find(root,name);
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
            using(var changes=new DeploymentLock()) {
            string executable=Deploy(paths);
            Scheduler<int>(root=> {
                object previous=Find(root),registered=null;
                try {
                    if(previous!=null) RequireOwned((dynamic)previous);
                    registered=root.RegisterTask(Name,Xml(executable),6,"SYSTEM",null,5,"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
                    log("TASK_INSTALLED name="+Name+" account=SYSTEM boot=ready wake=Kernel-Power/107+ready");
                    log("TASK_EXECUTABLE "+executable); return 0;
                } finally { Release(registered); Release(previous); }
            });
            CleanUnusedRuntimesLocked(log);
            }
        }
        internal static void Uninstall(Action<string> log,string name=Name)
        {
            using(var changes=new DeploymentLock()) {
            Scheduler<int>(root=> {
                object task=Find(root,name);
                try {
                    if(task==null) { log("TASK_UNINSTALLED: 计划任务已不存在。"); return 0; }
                    RequireOwned((dynamic)task);
                    // Remove registration without stopping an in-flight hardware process.
                    root.DeleteTask(name,0); log("TASK_UNINSTALLED: 已移除后续触发，正在运行的解锁会继续完成。"); return 0;
                } finally { Release(task); }
            });
            CleanUnusedRuntimesLocked(log);
            }
        }
        internal static void Run(Action<string> log,string name=Name)
        {
            Scheduler<int>(root=> {
                object task=Find(root,name),running=null;
                try {
                    if(task==null) throw new IOException("尚未安装计划任务。");
                    RequireOwned((dynamic)task);
                    running=((dynamic)task).Run(null);
                    log("TASK_MANUAL_TRIGGERED: 已请求后台运行，结果请查看计划任务状态及自动解锁日志。"); return 0;
                } finally { Release(running); Release(task); }
            });
        }
        internal static void RegisterPower(RuntimePaths paths,Action<string> log)
        {
            using(var changes=new DeploymentLock()) {
            string executable=Deploy(paths);
            Scheduler<int>(root=> {
                object previous=Find(root,PowerName),registered=null;
                try {
                    if(previous!=null) RequireOwned((dynamic)previous);
                    registered=root.RegisterTask(PowerName,PowerXml(executable),6,"SYSTEM",null,5,"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
                    log("POWER_TASK_INSTALLED: SYSTEM 开机常驻，关闭 GUI 后继续省电控制。");return 0;
                } finally {Release(registered);Release(previous);}
            });
            CleanUnusedRuntimesLocked(log);
            }
        }
        internal static void CleanUnusedRuntimes(Action<string> log)
        {
            try {using(var changes=new DeploymentLock()) CleanUnusedRuntimesLocked(log);}
            catch(Exception error) {log("TASK_RUNTIME_CLEANUP_DEFERRED: "+error.Message);}
        }
        static void CleanUnusedRuntimesLocked(Action<string> log)
        {
            try {
                var references=Scheduler<List<string>>(root=> {
                    var files=new List<string>();CollectExecutables(root,files);return files;
                });
                references.AddRange(RuntimeDeployment.ActiveImages());
                RuntimeDeployment.Clean(RuntimeDeployment.Root,references,log);
            } catch(Exception error) {log("TASK_RUNTIME_CLEANUP_DEFERRED: "+error.Message);}
        }
        static void CollectExecutables(dynamic folder,List<string> files)
        {
            object tasks=null,folders=null;
            try {
                tasks=folder.GetTasks(1);
                for(int i=1;i<=(int)((dynamic)tasks).Count;i++) {
                    object task=null,definition=null,actions=null;
                    try {
                        task=((dynamic)tasks)[i];definition=((dynamic)task).Definition;actions=((dynamic)definition).Actions;
                        for(int j=1;j<=(int)((dynamic)actions).Count;j++) {
                            object action=null;
                            try {action=((dynamic)actions)[j];if((int)((dynamic)action).Type==0) {files.Add((string)((dynamic)action).Path);files.Add((string)((dynamic)action).WorkingDirectory);}}
                            finally {Release(action);}
                        }
                    } finally {Release(actions);Release(definition);Release(task);}
                }
                folders=folder.GetFolders(0);
                for(int i=1;i<=(int)((dynamic)folders).Count;i++) {
                    object child=null;
                    try {child=((dynamic)folders)[i];CollectExecutables((dynamic)child,files);}
                    finally {Release(child);}
                }
            } finally {Release(folders);Release(tasks);}
        }
        internal static bool DisableInspectorStartup(Action<string> log)
        {
            return InspectorStartup(false,log);
        }
        internal static void RestoreInspectorStartup(Action<string> log) {InspectorStartup(true,log);}
        static bool InspectorStartup(bool enabled,Action<string> log)
        {
            return Scheduler<bool>(root=> {
                object task=Find(root,@"\nvidiaInspector\MultiDisplayPowerSaver");
                if(task==null) return false;
                object definition=null,actions=null,action=null;
                try {
                    definition=((dynamic)task).Definition;actions=((dynamic)definition).Actions;
                    if((int)((dynamic)actions).Count!=1) return false;
                    action=((dynamic)actions)[1];
                    string file=((dynamic)action).Path,args=((dynamic)action).Arguments;
                    if(String.Equals(Path.GetFileName(file),"nvidiaInspector.exe",StringComparison.OrdinalIgnoreCase) &&
                        (args??"").IndexOf("-multiDisplayPowerSaver",StringComparison.OrdinalIgnoreCase)>=0 && (bool)((dynamic)task).Enabled!=enabled) {
                        ((dynamic)task).Enabled=enabled;
                        log(enabled?"INSPECTOR_STARTUP_RESTORED: 启用失败，已恢复外部省电启动任务。":"INSPECTOR_STARTUP_DISABLED: 已停用外部省电任务，配置和程序均保留。");
                        return true;
                    }
                    return false;
                } finally {Release(action);Release(actions);Release(definition);Release(task);}
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
                {"runtime/CMP90HXGen2.exe",paths.Worker},{"runtime/CMP90HXGen2.exe.config",paths.Worker+".config"},{"core/nvpermissive-core.o",paths.Core},
                {"driver/CMP90HXDmaSigned.sys",paths.DmaDriver}
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
