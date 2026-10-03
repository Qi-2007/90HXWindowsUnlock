using System;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace CMP90HX.Control
{
    internal static class SharedSynchronization
    {
        internal const string HardwareName=@"Global\CMP90HX_FullTestScript";
        internal static Mutex OpenMutex(string name)
        {
            // Existing SYSTEM objects need only wait/release access, not FullControl.
            try {return Mutex.OpenExisting(name,MutexRights.Synchronize|MutexRights.Modify);}
            catch(WaitHandleCannotBeOpenedException) { }
            bool created;
            var security=new MutexSecurity();
            foreach(var type in new[]{WellKnownSidType.LocalSystemSid,WellKnownSidType.BuiltinAdministratorsSid})
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(type,null),MutexRights.FullControl,AccessControlType.Allow));
            try {return new Mutex(false,name,out created,security);}
            catch(UnauthorizedAccessException) {
                // Another process may have won creation with a more restrictive DACL.
                return Mutex.OpenExisting(name,MutexRights.Synchronize|MutexRights.Modify);
            }
        }
    }
}
