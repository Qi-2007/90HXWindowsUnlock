using System;
using System.IO;
using System.Threading;

namespace CMP90HX.Control
{
    internal sealed class PowerHardwareGate : IDisposable
    {
        readonly Mutex mutex=SharedSynchronization.OpenMutex(SharedSynchronization.HardwareName);
        bool owned;
        internal PowerHardwareGate()
        {
            try {
                try {owned=mutex.WaitOne(10000);} catch(AbandonedMutexException) {owned=true;}
                if(!owned) throw new IOException("解锁或硬件操作仍在运行，请完成后再修改省电设置。");
            } catch {mutex.Dispose();throw;}
        }
        public void Dispose() {if(owned) {owned=false;mutex.ReleaseMutex();}mutex.Dispose();}
    }
}