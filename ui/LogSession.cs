using System;
using System.IO;

namespace CMP90HX.Control
{
    // A lease protects a running GUI/task's logs when another launch cleans old records.
    internal sealed class LogSession : IDisposable
    {
        internal readonly string DirectoryPath;
        readonly FileStream lease;
        internal LogSession(string root)
        {
            DateTime started=DateTime.UtcNow;
            Directory.CreateDirectory(root);
            DirectoryPath=Path.Combine(root,Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            lease=new FileStream(Path.Combine(DirectoryPath,".active"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
            CleanPrevious(root,DirectoryPath,started);
        }
        internal static void CleanPrevious(string root,string current,DateTime before)
        {
            root=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            foreach(string entry in Directory.GetDirectories(root)) {
                string full=Path.GetFullPath(entry);
                if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase) || full==current || Directory.GetCreationTimeUtc(full)>=before) continue;
                try {
                    if(HasLink(full)) continue;
                    string marker=Path.Combine(full,".active");
                    if(File.Exists(marker)) {
                        // A live session does not share write access. Do not remove its files.
                        using(var probe=new FileStream(marker,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { }
                    }
                    Directory.Delete(full,true);
                } catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
            foreach(string file in Directory.GetFiles(root)) {
                try { if((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0 && File.GetCreationTimeUtc(file)<before) File.Delete(file); }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
        }
        static bool HasLink(string path)
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) return true;
            foreach(string entry in Directory.GetFileSystemEntries(path)) {
                if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0) return true;
                if(Directory.Exists(entry) && HasLink(entry)) return true;
            }
            return false;
        }
        public void Dispose() { lease.Dispose(); }
    }
}
