using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace CMP90HX.Control {
    // Only the historical built-in power task and --power-monitor processes qualify.
    internal sealed class LegacyPowerMigration {
        const string TaskName="CMP90HX Idle Power Saver";
        bool checkedOnce;
        internal sealed class Journal {
            public Journal() {}
            public PowerSettings Settings {get;set;}
            public List<string> Directories {get;set;}
        }
        internal static bool IsLegacy(string executable,string arguments) {
            return !String.IsNullOrWhiteSpace(executable) &&
                String.Equals(Path.GetFileName(executable.Trim('"')),"CMP90HXControl.exe",StringComparison.OrdinalIgnoreCase) &&
                String.Equals((arguments??"").Trim(),"--power-monitor",StringComparison.OrdinalIgnoreCase);
        }
        static void Release(object value) {if(value!=null && Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
        static object Find(dynamic folder) {
            try {return folder.GetTask(TaskName);}
            catch(FileNotFoundException) {return null;}
            catch(COMException error) {if(error.ErrorCode==unchecked((int)0x80070002))return null;throw;}
        }
        static List<int> Processes(List<string> directories) {
            var ids=new List<int>();
            using(var search=new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='CMP90HXControl.exe'")) {
            search.Scope.Options.EnablePrivileges=true;
            using(var results=search.Get()) foreach(ManagementObject item in results) using(item) {
                string image=(string)item["ExecutablePath"],command=(string)item["CommandLine"];
                if(String.IsNullOrWhiteSpace(image) || String.IsNullOrWhiteSpace(command))
                    throw new IOException("无法确认旧控制台进程的参数，省电迁移暂缓。");
                string prefix=command.TrimStart(),arguments;
                if(prefix.StartsWith("\"",StringComparison.Ordinal)) {
                    int end=prefix.IndexOf('"',1);if(end<0)continue;arguments=prefix.Substring(end+1);
                } else {
                    if(!prefix.StartsWith(image,StringComparison.OrdinalIgnoreCase))continue;
                    arguments=prefix.Substring(image.Length);
                }
                if(!IsLegacy(image,arguments))continue;
                ids.Add(Convert.ToInt32(item["ProcessId"]));directories.Add(Path.GetDirectoryName(image));
            }
            }
            return ids;
        }
        static bool Exited(int id) {
            try {using(var process=Process.GetProcessById(id))return process.HasExited;}
            catch(ArgumentException) {return true;}
        }
        static void CollectReferences(dynamic folder,List<string> references) {
            object tasks=null,folders=null;
            try {
                tasks=folder.GetTasks(1);
                for(int i=1;i<=(int)((dynamic)tasks).Count;i++) {
                    object task=null,definition=null,actions=null;
                    try {
                        task=((dynamic)tasks)[i];definition=((dynamic)task).Definition;actions=((dynamic)definition).Actions;
                        for(int j=1;j<=(int)((dynamic)actions).Count;j++) {
                            object action=null;try {action=((dynamic)actions)[j];if((int)((dynamic)action).Type==0) {references.Add((string)((dynamic)action).Path);references.Add((string)((dynamic)action).WorkingDirectory);}}finally{Release(action);}
                        }
                    } finally {Release(actions);Release(definition);Release(task);}
                }
                folders=folder.GetFolders(0);
                for(int i=1;i<=(int)((dynamic)folders).Count;i++) {object child=null;try{child=((dynamic)folders)[i];CollectReferences((dynamic)child,references);}finally{Release(child);}}
            } finally {Release(folders);Release(tasks);}
        }
        internal bool Run(Action<string> log) {
            using(var coordination=new PowerCoordination()) {
                string journalPath=Path.Combine(PowerStorage.Root,"State","legacy-power-migration.json");
                if(checkedOnce && !coordination.Running.WaitOne(0) && !File.Exists(journalPath))return false;
                {
                    object service=null,folder=null,task=null,definition=null,info=null,actions=null,action=null;
                    try {
                        Journal journal;List<int> ids;
                        using(var deployment=new DeploymentLock()) {
                            var directories=new List<string>();ids=Processes(directories);
                            service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();
                            folder=((dynamic)service).GetFolder(@"\");task=Find((dynamic)folder);
                            if(task!=null) {
                                definition=((dynamic)task).Definition;info=((dynamic)definition).RegistrationInfo;actions=((dynamic)definition).Actions;
                                if((string)((dynamic)info).Source!="CMP90HX.Control" || (int)((dynamic)actions).Count!=1)
                                    throw new IOException("同名省电任务不属于旧版内置省电，拒绝迁移。");
                                action=((dynamic)actions)[1];
                                if((int)((dynamic)action).Type!=0 || !IsLegacy((string)((dynamic)action).Path,(string)((dynamic)action).Arguments))
                                    throw new IOException("旧省电任务命令无法确认，拒绝迁移。");
                                directories.Add(Path.GetDirectoryName((string)((dynamic)action).Path));
                            }
                            if(task==null && ids.Count==0 && !File.Exists(journalPath)) {
                                if(coordination.Running.WaitOne(0))throw new IOException("旧省电信号仍在，未确认对应进程，暂缓接管。");
                                checkedOnce=true;return false;
                            }
                            if(File.Exists(journalPath) && (File.GetAttributes(journalPath)&FileAttributes.ReparsePoint)!=0)throw new IOException("迁移记录不能是符号链接。");
                            journal=File.Exists(journalPath)?ControlMemory.Json().Deserialize<Journal>(File.ReadAllText(journalPath)):
                                new Journal {Settings=PowerStorage.ReadSettings(),Directories=directories};
                            if(journal==null || journal.Settings==null || journal.Directories==null)throw new IOException("旧省电迁移记录无效。");
                            journal.Settings.Validate();journal.Directories.AddRange(directories);
                            DriverService.ProtectDirectory(Path.GetDirectoryName(journalPath));
                            PowerStorage.Write(Path.GetDirectoryName(journalPath),Path.GetFileName(journalPath),journal);
                            // Prevent another launch before requesting a graceful release and exit.
                            if(task!=null) {((dynamic)task).Enabled=false;((dynamic)folder).DeleteTask(TaskName,0);log("LEGACY_POWER_TASK_REMOVED");}
                            var disabled=ControlMemory.Json().Deserialize<PowerSettings>(ControlMemory.Json().Serialize(journal.Settings));disabled.Enabled=false;
                            PowerStorage.WriteSettings(disabled);
                        }
                        // The old loop takes DeploymentLock while reading settings. Never
                        // hold it while waiting for that loop to release its limit and exit.
                        var wait=Stopwatch.StartNew();
                        while(wait.ElapsedMilliseconds<10000 && (ids.Any(id=>!Exited(id)) || coordination.Running.WaitOne(0)))Thread.Sleep(100);
                        if(ids.Any(id=>!Exited(id)) || coordination.Running.WaitOne(0))throw new IOException("旧省电进程尚未释放限制并退出，迁移设置已保存，将自动重试。");
                        using(var deployment=new DeploymentLock()) {
                            // Preserve any recovery intent left by a crashed old controller.
                            var status=PowerStorage.ReadStatus();if(status!=null)PowerStorage.WriteStatus(status);
                            PowerStorage.WriteSettings(journal.Settings);
                            var references=new List<string>();CollectReferences((dynamic)folder,references);references.AddRange(RuntimeDeployment.ActiveImages());
                            RuntimeDeployment.Clean(RuntimeDeployment.Root,references,log,journal.Directories);
                            File.Delete(journalPath);checkedOnce=true;log("LEGACY_POWER_MIGRATED: 已继承省电设置，由统一后台接管。");return true;
                        }
                    } finally {Release(action);Release(actions);Release(info);Release(definition);Release(task);Release(folder);Release(service);}
                }
            }
        }
    }
}
