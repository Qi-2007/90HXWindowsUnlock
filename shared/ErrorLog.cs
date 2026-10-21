using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CMP90HX.Control {
    internal static class ErrorLog {
        const int Retained=10;
        internal static string TrySave(string context,string details,string source=null) {
            try {return Save(Path.Combine(AppData.Root,"ErrorLogs"),context,details,source);}
            catch(Exception error) {Trace.WriteLine("ERROR_ARCHIVE_FAILED: "+error);return null;}
        }
        internal static string Save(string root,string context,string details,string source) {
            using(var gate=SharedSynchronization.OpenMutex(@"Global\CMP90HX.ErrorLogs.v1")) {
                bool owned=false;
                try {
                    try {owned=gate.WaitOne(5000);}catch(System.Threading.AbandonedMutexException){owned=true;}
                    if(!owned)throw new IOException("Error archive is busy.");
                    root=Path.GetFullPath(root);
                    if(Directory.Exists(root) && IsLink(root))throw new IOException("Error archive cannot be a link.");
                    Directory.CreateDirectory(root);
                    string id=DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")+"-"+Guid.NewGuid().ToString("N");
                    string pending=Path.Combine(root,".pending-"+Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(pending);
                    try {
                        var report=new StringBuilder();
                        report.AppendLine("UTC: "+DateTime.UtcNow.ToString("o"));
                        report.AppendLine("Version: "+typeof(ErrorLog).Assembly.GetName().Version);
                        report.AppendLine("Context: "+context);report.AppendLine(details);
                        if(!String.IsNullOrEmpty(source) && Directory.Exists(source)) {
                            try {CopyEvidence(source,Path.Combine(pending,"operation"),report);}
                            catch(IOException error){report.AppendLine("EVIDENCE_COPY_FAILED: "+error.Message);}
                            catch(UnauthorizedAccessException error){report.AppendLine("EVIDENCE_COPY_FAILED: "+error.Message);}
                        }
                        byte[] bytes=new UTF8Encoding(false).GetBytes(report.ToString());
                        using(var file=new FileStream(Path.Combine(pending,"error.log"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {
                            file.Write(bytes,0,bytes.Length);file.Flush(true);
                        }
                        string final=Path.Combine(root,id);Directory.Move(pending,final);
                        Prune(root,final);return final;
                    } finally {
                        if(Directory.Exists(pending) && !HasLink(pending))Directory.Delete(pending,true);
                    }
                } finally {if(owned)gate.ReleaseMutex();}
            }
        }
        static void CopyEvidence(string source,string destination,StringBuilder report) {
            if(IsLink(source)) {report.AppendLine("EVIDENCE_SKIPPED_LINK: "+source);return;}
            Directory.CreateDirectory(destination);
            foreach(string entry in Directory.GetFileSystemEntries(source)) {
                if(IsLink(entry)) {report.AppendLine("EVIDENCE_SKIPPED_LINK: "+entry);continue;}
                if(Path.GetFileName(entry)==".active")continue;
                string target=Path.Combine(destination,Path.GetFileName(entry));
                try {
                    if(Directory.Exists(entry))CopyEvidence(entry,target,report);
                    else File.Copy(entry,target,false);
                }catch(IOException error){report.AppendLine("EVIDENCE_COPY_FAILED: "+entry+": "+error.Message);}
                 catch(UnauthorizedAccessException error){report.AppendLine("EVIDENCE_COPY_FAILED: "+entry+": "+error.Message);}
            }
        }
        static void Prune(string root,string latest) {
            var records=Directory.GetDirectories(root).Where(path=>
                Regex.IsMatch(Path.GetFileName(path),@"^\d{8}T\d{9}Z-[0-9a-f]{32}$") && !IsLink(path) && File.Exists(Path.Combine(path,"error.log")))
                .OrderByDescending(path=>path==latest).ThenByDescending(path=>Directory.GetCreationTimeUtc(path))
                .ThenByDescending(path=>Path.GetFileName(path),StringComparer.Ordinal).ToArray();
            foreach(string old in records.Skip(Retained)) {
                try {if(!HasLink(old))Directory.Delete(old,true);}
                catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
        static bool HasLink(string path) {
            if(IsLink(path))return true;
            return Directory.GetFileSystemEntries(path).Any(entry=>IsLink(entry) || Directory.Exists(entry) && HasLink(entry));
        }
        static bool IsLink(string path) {return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;}
    }
}
