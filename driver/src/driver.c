#include <ntifs.h>
#include <wdmsec.h>
#include "../include/protocol.h"
#include "../include/arena_state.h"

/* This experimental legacy driver supplies PHYSICAL addresses, not IOVAs.
 * Register access is scoped to a validated GA102 BAR0/upstream bridge.
 * No unload routine: backing pages and quarantine survive until reboot. */
static FAST_MUTEX Lock;
static PFILE_OBJECT Owner;
static PEPROCESS Process;
static PVOID Buffer, UserAddress;
static PMDL Mdl;
static PHYSICAL_ADDRESS Physical;
static unsigned State;
static ULONGLONG Cookie;
static const GUID DeviceClass = {0x21be72b4,0x4f41,0x4daf,{0xa2,0xcb,0x25,0xa0,0x8a,0x2e,0xb7,0xd1}};
#include "hardware.h"
DRIVER_INITIALIZE DriverEntry;
DRIVER_DISPATCH Dispatch;

static NTSTATUS Finish(PIRP irp, NTSTATUS status, ULONG_PTR bytes)
{
    irp->IoStatus.Status = status;
    irp->IoStatus.Information = bytes;
    IoCompleteRequest(irp, IO_NO_INCREMENT);
    return status;
}

static NTSTATUS MapBuffer(void)
{
    PHYSICAL_ADDRESS low, high, boundary;
    low.QuadPart = 0x100000; high.QuadPart = 0xffffffff; boundary.QuadPart = 0;
    if (!Buffer) {
        Buffer = MmAllocateContiguousMemorySpecifyCache(CMP_ARENA_BYTES, low, high, boundary, MmCached);
        if (!Buffer) return STATUS_INSUFFICIENT_RESOURCES;
        RtlZeroMemory(Buffer, CMP_ARENA_BYTES);
        Physical = MmGetPhysicalAddress(Buffer);
        Mdl = IoAllocateMdl(Buffer, CMP_ARENA_BYTES, FALSE, FALSE, NULL);
        if (!Mdl) {
            MmFreeContiguousMemory(Buffer); Buffer = NULL;
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        MmBuildMdlForNonPagedPool(Mdl);
        Cookie = (ULONGLONG)KeQueryPerformanceCounter(NULL).QuadPart;
        if (!Cookie) Cookie = 1;
    }
    if (!UserAddress) {
        __try {
            UserAddress = MmMapLockedPagesSpecifyCache(Mdl, UserMode, MmCached, NULL,
                FALSE, NormalPagePriority | MdlMappingNoExecute);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return GetExceptionCode();
        }
        if (!UserAddress) return STATUS_INSUFFICIENT_RESOURCES;
    }
    return STATUS_SUCCESS;
}

NTSTATUS Dispatch(PDEVICE_OBJECT device, PIRP irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(irp);
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR bytes = 0;
    UNREFERENCED_PARAMETER(device);
    if (KeGetCurrentIrql() != PASSIVE_LEVEL) return Finish(irp, STATUS_INVALID_DEVICE_STATE, 0);
    ExAcquireFastMutex(&Lock);
    /* ExAcquireFastMutex raises to APC_LEVEL; allocations/mapping require <= APC_LEVEL. */
    switch (stack->MajorFunction) {
    case IRP_MJ_CREATE:
        if (IoIs32bitProcess(irp)) { status = STATUS_NOT_SUPPORTED; break; }
        if (Owner) { status = STATUS_SHARING_VIOLATION; break; }
        Owner = stack->FileObject;
        Process = PsGetCurrentProcess(); ObReferenceObject(Process);
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_CLEANUP:
        if (Owner == stack->FileObject) {
            KAPC_STATE attach;
            HardwareCleanup();
            State = arena_disconnect(State);
            if (UserAddress) {
                KeStackAttachProcess(Process, &attach);
                MmUnmapLockedPages(UserAddress, Mdl);
                KeUnstackDetachProcess(&attach);
                UserAddress = NULL;
            }
            ObDereferenceObject(Process); Process = NULL; Owner = NULL;
        }
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_CLOSE:
        status = STATUS_SUCCESS;
        break;
    case IRP_MJ_DEVICE_CONTROL:
        if (Owner != stack->FileObject || PsGetCurrentProcess() != Process ||
            irp->RequestorMode != UserMode) { status = STATUS_ACCESS_DENIED; break; }
        if (stack->Parameters.DeviceIoControl.IoControlCode >= CMP_BIND &&
            stack->Parameters.DeviceIoControl.IoControlCode <= CMP_MMIO_WRITE) {
            status = HardwareIoctl(stack->Parameters.DeviceIoControl.IoControlCode,
                irp->AssociatedIrp.SystemBuffer, stack->Parameters.DeviceIoControl.InputBufferLength,
                stack->Parameters.DeviceIoControl.OutputBufferLength, &bytes);
            break;
        }
        if (stack->Parameters.DeviceIoControl.InputBufferLength != 0) {
            status = STATUS_INVALID_PARAMETER; break;
        }
        switch (stack->Parameters.DeviceIoControl.IoControlCode) {
        case CMP_INFO:
        case CMP_MAP:
            if (stack->Parameters.DeviceIoControl.OutputBufferLength != sizeof(CMP_ARENA_INFO)) {
                status = STATUS_INFO_LENGTH_MISMATCH; break;
            }
            if (stack->Parameters.DeviceIoControl.IoControlCode == CMP_MAP) {
                if (State != ARENA_CLEAN) { status = STATUS_DEVICE_BUSY; break; }
                status = MapBuffer();
                if (!NT_SUCCESS(status)) break;
            }
            {
                CMP_ARENA_INFO *info = (CMP_ARENA_INFO *)irp->AssociatedIrp.SystemBuffer;
                RtlZeroMemory(info, sizeof(*info));
                info->Version = CMP_PROTOCOL_VERSION; info->State = State;
                info->Physical = (ULONGLONG)Physical.QuadPart;
                info->Length = Buffer ? CMP_ARENA_BYTES : 0;
                info->UserAddress = (ULONGLONG)(ULONG_PTR)UserAddress;
                info->Cookie = Cookie;
                bytes = sizeof(*info); status = STATUS_SUCCESS;
            }
            break;
        case CMP_ARM:
        case CMP_COMPLETE:
            if (stack->Parameters.DeviceIoControl.OutputBufferLength != 0 || !UserAddress) {
                status = STATUS_INVALID_PARAMETER; break;
            }
            if (stack->Parameters.DeviceIoControl.IoControlCode == CMP_ARM)
                status = arena_arm(&State) ? STATUS_SUCCESS : STATUS_DEVICE_BUSY;
            else
                status = arena_complete(&State) ? STATUS_SUCCESS : STATUS_INVALID_DEVICE_STATE;
            break;
        }
        break;
    }
    ExReleaseFastMutex(&Lock);
    return Finish(irp, status, bytes);
}

NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    UNICODE_STRING name = RTL_CONSTANT_STRING(L"\\Device\\CMP90HXDma");
    UNICODE_STRING link = RTL_CONSTANT_STRING(L"\\DosDevices\\CMP90HXDma");
    UNICODE_STRING acl = RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    PDEVICE_OBJECT device;
    NTSTATUS status;
    ULONG i;
    UNREFERENCED_PARAMETER(registry);
    HardwareInitialize(); /* Firmware APIs require PASSIVE_LEVEL, before the mutex. */
    ExInitializeFastMutex(&Lock);
    status = IoCreateDeviceSecure(driver, 0, &name, CMP_DEVICE_TYPE,
        FILE_DEVICE_SECURE_OPEN, TRUE, &acl, &DeviceClass, &device);
    if (!NT_SUCCESS(status)) return status;
    status = IoCreateSymbolicLink(&link, &name);
    if (!NT_SUCCESS(status)) { IoDeleteDevice(device); return status; }
    for (i = 0; i <= IRP_MJ_MAXIMUM_FUNCTION; ++i) driver->MajorFunction[i] = Dispatch;
    driver->DriverUnload = NULL;
    device->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}
