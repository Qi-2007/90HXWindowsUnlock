#include <ntifs.h>
#include <wdmsec.h>
#include "caller_auth.h"
#define READER_TYPE 0x8338
#define READER_SNAPSHOT CTL_CODE(READER_TYPE,0x800,METHOD_BUFFERED,FILE_READ_DATA|FILE_WRITE_DATA)
typedef struct _SNAPSHOT {
    ULONG Version,Gpu,Bridge,Flags;
    ULONGLONG Bar0;
    ULONG Boot0,GpuLink,BridgeLink,Compute0,Compute1,Graphics;
    ULONG BeforeCommand,AfterCommand,Reserved[2];
} SNAPSHOT;
C_ASSERT(sizeof(SNAPSHOT)==64);
static KMUTEX Lock;
static const GUID ReaderClass={0x70a06851,0xf331,0x437a,{0xb1,0xa5,0x8b,0xdd,0x35,0x43,0xd9,0x80}};
static ULONG Pci(ULONG bdf,ULONG offset,ULONG width)
{
    PCI_SLOT_NUMBER slot; ULONG value=MAXULONG;
    slot.u.AsULONG=0;slot.u.bits.DeviceNumber=(bdf>>3)&31;slot.u.bits.FunctionNumber=bdf&7;
    if(HalGetBusDataByOffset(PCIConfiguration,bdf>>8,slot.u.AsULONG,&value,offset,width)!=width)return MAXULONG;
    return width==4?value:value&((1u<<(width*8))-1);
}
static ULONG Cap(ULONG bdf,ULONG id)
{
    ULONG p=Pci(bdf,0x34,1)&~3u,i;
    for(i=0;i<48 && p>=0x40 && p<=0xfc;i++) {
        ULONG value=Pci(bdf,p,2);if((value&255)==id)return p;p=(value>>8)&~3u;
    }
    return 0;
}
static ULONGLONG Bar(ULONG bdf)
{
    ULONG low=Pci(bdf,0x10,4),type=low&6;
    if(!low || low==MAXULONG || (low&1) || (type!=0 && type!=4))return 0;
    return (ULONGLONG)(low&~15u)|(type==4?(ULONGLONG)Pci(bdf,0x14,4)<<32:0);
}
static BOOLEAN Ready(ULONG bdf,ULONGLONG bar)
{
    ULONG command=Pci(bdf,4,2),pm=Cap(bdf,1);
    return Pci(bdf,0,4)==0x220d10de && Bar(bdf)==bar && command!=MAXULONG &&
        (command&2)!=0 && pm!=0 && (Pci(bdf,pm+4,2)&3)==0;
}
static NTSTATUS Capture(ULONG gpu,SNAPSHOT *result)
{
    ULONG bus=gpu>>8,bdf,bridge=MAXULONG,matches=0,gcap,bcap,boot,command,bridgeId,bridgeBuses;
    ULONGLONG bar;PVOID identity=NULL,registers=NULL;PHYSICAL_ADDRESS address;
    NTSTATUS status=STATUS_DEVICE_NOT_READY;
    if(gpu>0xffff || Pci(gpu,0,4)!=0x220d10de)return STATUS_NO_SUCH_DEVICE;
    bar=Bar(gpu);if(!bar || (bar&(PAGE_SIZE-1)) || bar>0x000fffffffffffffULL-0x824000 || !Ready(gpu,bar))return status;
    for(bdf=0;bdf<=0xffff;bdf+=8) {
        ULONG fn,limit=(Pci(bdf,0x0e,1)&0x80)?8:1;
        if(Pci(bdf,0,4)==MAXULONG)continue;
        for(fn=0;fn<limit;fn++) {
            ULONG target=bdf|fn,buses;
            if(Pci(target,8,4)>>16!=0x0604 || (Pci(target,0x0e,1)&0x7f)!=1)continue;
            buses=Pci(target,0x18,4);
            if(((buses>>8)&255)==bus && ((buses>>16)&255)>=bus){bridge=target;matches++;}
        }
    }
    if(matches!=1)return STATUS_DEVICE_CONFIGURATION_ERROR;
    bridgeId=Pci(bridge,0,4);bridgeBuses=Pci(bridge,0x18,4);
    gcap=Cap(gpu,0x10);bcap=Cap(bridge,0x10);if(!gcap || !bcap || gcap>0xe8 || bcap>0xe8)return STATUS_NOT_SUPPORTED;
    command=Pci(gpu,4,2);if(!Ready(gpu,bar))return status;
    address.QuadPart=(LONGLONG)bar;
    identity=MmMapIoSpaceEx(address,PAGE_SIZE,PAGE_READONLY|PAGE_NOCACHE);
    if(!identity)return STATUS_INSUFFICIENT_RESOURCES;
    boot=READ_REGISTER_ULONG((PULONG)identity);
    if((boot&0x1f000000)!=0x17000000 || (boot&0xf00000)!=0x200000)goto done;
    address.QuadPart=(LONGLONG)(bar+0x823000);
    registers=MmMapIoSpaceEx(address,PAGE_SIZE,PAGE_READONLY|PAGE_NOCACHE);
    if(!registers){status=STATUS_INSUFFICIENT_RESOURCES;goto done;}
    if(!Ready(gpu,bar))goto done;
    RtlZeroMemory(result,sizeof(*result));result->Version=1;result->Gpu=gpu;result->Bridge=bridge;
    result->Bar0=bar;result->Boot0=boot;result->BeforeCommand=command;
    result->Compute0=READ_REGISTER_ULONG((PULONG)((PUCHAR)registers+0x81c));
    result->Compute1=READ_REGISTER_ULONG((PULONG)((PUCHAR)registers+0x820));
    result->Graphics=READ_REGISTER_ULONG((PULONG)((PUCHAR)registers+0x830));
    result->GpuLink=Pci(gpu,gcap+0x12,2);result->BridgeLink=Pci(bridge,bcap+0x12,2);
    result->AfterCommand=Pci(gpu,4,2);
    if(!Ready(gpu,bar) || Pci(bridge,0,4)!=bridgeId || Pci(bridge,0x18,4)!=bridgeBuses ||
        Cap(gpu,0x10)!=gcap || Cap(bridge,0x10)!=bcap || result->BeforeCommand!=result->AfterCommand ||
        READ_REGISTER_ULONG((PULONG)identity)!=boot || result->GpuLink>=0xffff || result->BridgeLink>=0xffff)goto done;
    result->Flags=1;status=STATUS_SUCCESS;
done:
    if(registers)MmUnmapIoSpace(registers,PAGE_SIZE);
    MmUnmapIoSpace(identity,PAGE_SIZE);return status;
}
static NTSTATUS Dispatch(PDEVICE_OBJECT device,PIRP irp)
{
    PIO_STACK_LOCATION stack=IoGetCurrentIrpStackLocation(irp);NTSTATUS status=STATUS_INVALID_DEVICE_REQUEST;ULONG_PTR bytes=0;
    UNREFERENCED_PARAMETER(device);
    if(KeGetCurrentIrql()!=PASSIVE_LEVEL){status=STATUS_INVALID_DEVICE_STATE;goto done;}
    if(stack->MajorFunction==IRP_MJ_CREATE) {
        if(irp->RequestorMode!=UserMode || IoGetRequestorProcess(irp)!=PsGetCurrentProcess() || IoIs32bitProcess(irp))status=STATUS_ACCESS_DENIED;
        else status=CallerAuthVerify(PsGetCurrentProcess());
        if(NT_SUCCESS(status)) {
            stack->FileObject->FsContext=PsGetCurrentProcess();ObReferenceObject(stack->FileObject->FsContext);
        }
    } else if(stack->MajorFunction==IRP_MJ_CLEANUP) {
        if(stack->FileObject->FsContext){ObDereferenceObject(stack->FileObject->FsContext);stack->FileObject->FsContext=NULL;}status=STATUS_SUCCESS;
    } else if(stack->MajorFunction==IRP_MJ_CLOSE)status=STATUS_SUCCESS;
    else if(stack->MajorFunction==IRP_MJ_DEVICE_CONTROL) {
        if(irp->RequestorMode!=UserMode || stack->FileObject->FsContext!=PsGetCurrentProcess() || IoGetRequestorProcess(irp)!=PsGetCurrentProcess()){status=STATUS_ACCESS_DENIED;goto done;}
        if(stack->Parameters.DeviceIoControl.IoControlCode!=READER_SNAPSHOT)goto done;
        if(stack->Parameters.DeviceIoControl.InputBufferLength!=4 || stack->Parameters.DeviceIoControl.OutputBufferLength!=sizeof(SNAPSHOT)){status=STATUS_INFO_LENGTH_MISMATCH;goto done;}
        KeEnterCriticalRegion();KeWaitForSingleObject(&Lock,Executive,KernelMode,FALSE,NULL);
        status=Capture(*(ULONG *)irp->AssociatedIrp.SystemBuffer,(SNAPSHOT *)irp->AssociatedIrp.SystemBuffer);
        KeReleaseMutex(&Lock,FALSE);KeLeaveCriticalRegion();
        if(NT_SUCCESS(status))bytes=sizeof(SNAPSHOT);
    }
done:
    irp->IoStatus.Status=status;irp->IoStatus.Information=bytes;IoCompleteRequest(irp,IO_NO_INCREMENT);return status;
}
DRIVER_INITIALIZE DriverEntry;
NTSTATUS DriverEntry(PDRIVER_OBJECT driver,PUNICODE_STRING registry)
{
    UNICODE_STRING name=RTL_CONSTANT_STRING(L"\\Device\\CMP90HXReader"),link=RTL_CONSTANT_STRING(L"\\DosDevices\\CMP90HXReader");
    UNICODE_STRING acl=RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");PDEVICE_OBJECT device;ULONG i;NTSTATUS status;
    UNREFERENCED_PARAMETER(registry);KeInitializeMutex(&Lock,0);
    status=CallerAuthInitialize();if(!NT_SUCCESS(status))return status;
    status=IoCreateDeviceSecure(driver,0,&name,READER_TYPE,FILE_DEVICE_SECURE_OPEN,FALSE,&acl,&ReaderClass,&device);
    if(!NT_SUCCESS(status)){CallerAuthShutdown();return status;}
    status=IoCreateSymbolicLink(&link,&name);if(!NT_SUCCESS(status)){IoDeleteDevice(device);CallerAuthShutdown();return status;}
    for(i=0;i<=IRP_MJ_MAXIMUM_FUNCTION;i++)driver->MajorFunction[i]=Dispatch;
    driver->DriverUnload=NULL;device->Flags&=~DO_DEVICE_INITIALIZING;return STATUS_SUCCESS;
}
