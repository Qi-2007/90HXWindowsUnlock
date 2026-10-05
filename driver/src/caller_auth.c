#ifdef CMP_CALLER_AUTH_TEST
#include "../tests/caller_auth_mock.h"
#else
#include <ntifs.h>
#include <bcrypt.h>
#endif
#include "caller_auth.h"
/* Generated only from the self-tested release worker. Missing policy fails build. */
#ifndef CMP_CALLER_AUTH_TEST
#include "../../build/caller-policy.h"
#endif

#define AUTH_TAG 'aPmC'
#define AUTH_CHUNK 65536u
#define AUTH_MAX_IMAGE (64u * 1024u * 1024u)
typedef struct _CALLER_IMAGE {
    LIST_ENTRY Link;
    PEPROCESS Process;
    PFILE_OBJECT File;
    HANDLE Handle;
} CALLER_IMAGE;
static LIST_ENTRY Images;
static FAST_MUTEX ImageLock;
/* Keep a machine-readable policy inside the signed driver for package checks. */
static const volatile char Policy[] = "CMP90HX_CALLER_SHA256=" CMP_CALLER_SHA256_HEX;

static void FreeImage(CALLER_IMAGE *image)
{
    ZwClose(image->Handle);
    ObDereferenceObject(image->File);
    ObDereferenceObject(image->Process);
    ExFreePoolWithTag(image, AUTH_TAG);
}

static void ProcessNotify(PEPROCESS process, HANDLE id, PPS_CREATE_NOTIFY_INFO info)
{
    CALLER_IMAGE *image = NULL;
    PLIST_ENTRY link;
    UNREFERENCED_PARAMETER(id);
    if (info) {
        if (!info->FileObject || !NT_SUCCESS(info->CreationStatus)) return;
        image = ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(*image), AUTH_TAG);
        if (!image) return; /* No entry means access is denied, never fail open. */
        /* Keep an open handle so the loader cannot clean up this file object
         * before the process first opens our device. Never reopen by path. */
        if (!(info->FileObject->Flags & FO_SYNCHRONOUS_IO) ||
            !NT_SUCCESS(ObOpenObjectByPointer(info->FileObject, OBJ_KERNEL_HANDLE, NULL,
                FILE_READ_DATA | SYNCHRONIZE, *IoFileObjectType, KernelMode, &image->Handle))) {
            ExFreePoolWithTag(image, AUTH_TAG);
            return;
        }
        image->Process = process;
        image->File = info->FileObject;
        ObReferenceObject(process);
        ObReferenceObject(image->File);
        ExAcquireFastMutex(&ImageLock);
        InsertTailList(&Images, &image->Link);
        ExReleaseFastMutex(&ImageLock);
    } else {
        ExAcquireFastMutex(&ImageLock);
        for (link = Images.Flink; link != &Images; link = link->Flink) {
            CALLER_IMAGE *candidate = CONTAINING_RECORD(link, CALLER_IMAGE, Link);
            if (candidate->Process == process) {
                image = candidate;
                RemoveEntryList(link);
                break;
            }
        }
        ExReleaseFastMutex(&ImageLock);
        if (image) FreeImage(image);
    }
}

NTSTATUS CallerAuthInitialize(void)
{
    InitializeListHead(&Images);
    ExInitializeFastMutex(&ImageLock);
    return PsSetCreateProcessNotifyRoutineEx(ProcessNotify, FALSE);
}

void CallerAuthShutdown(void)
{
    PsSetCreateProcessNotifyRoutineEx(ProcessNotify, TRUE);
    while (!IsListEmpty(&Images)) {
        CALLER_IMAGE *image = CONTAINING_RECORD(RemoveHeadList(&Images), CALLER_IMAGE, Link);
        FreeImage(image);
    }
}

NTSTATUS CallerAuthVerify(PEPROCESS process)
{
    PFILE_OBJECT file = NULL;
    PLIST_ENTRY link;
    HANDLE handle = NULL;
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    PUCHAR buffer = NULL;
    UCHAR digest[32];
    static const UCHAR expected[32] = CMP_CALLER_SHA256_BYTES;
    FILE_STANDARD_INFORMATION size;
    IO_STATUS_BLOCK iosb;
    LARGE_INTEGER offset;
    NTSTATUS status = STATUS_ACCESS_DENIED;
    ULONG i;
    if (KeGetCurrentIrql() != PASSIVE_LEVEL || Policy[0] != 'C') return STATUS_ACCESS_DENIED;
    ExAcquireFastMutex(&ImageLock);
    for (link = Images.Flink; link != &Images; link = link->Flink) {
        CALLER_IMAGE *image = CONTAINING_RECORD(link, CALLER_IMAGE, Link);
        if (image->Process == process) { file = image->File; ObReferenceObject(file); break; }
    }
    ExReleaseFastMutex(&ImageLock);
    if (!file) return STATUS_ACCESS_DENIED;
    /* The loader's synchronous image handle permits bounded, explicit-offset reads.
     * Do not reopen an image by path: that could refer to a replacement file. */
    if (!(file->Flags & FO_SYNCHRONOUS_IO)) goto done;
    status = ObOpenObjectByPointer(file, OBJ_KERNEL_HANDLE, NULL, FILE_READ_DATA | SYNCHRONIZE,
        *IoFileObjectType, KernelMode, &handle);
    if (!NT_SUCCESS(status)) goto done;
    status = ZwQueryInformationFile(handle, &iosb, &size, sizeof(size), FileStandardInformation);
    if (!NT_SUCCESS(status)) goto done;
    if (size.EndOfFile.QuadPart <= 0 || size.EndOfFile.QuadPart > AUTH_MAX_IMAGE) {
        status = STATUS_ACCESS_DENIED; goto done;
    }
    status = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0);
    if (!NT_SUCCESS(status)) goto done;
    status = BCryptCreateHash(algorithm, &hash, NULL, 0, NULL, 0, 0);
    if (!NT_SUCCESS(status)) goto done;
    buffer = ExAllocatePool2(POOL_FLAG_NON_PAGED, AUTH_CHUNK, AUTH_TAG);
    if (!buffer) { status = STATUS_INSUFFICIENT_RESOURCES; goto done; }
    offset.QuadPart = 0;
    while (offset.QuadPart < size.EndOfFile.QuadPart) {
        LONGLONG remaining = size.EndOfFile.QuadPart - offset.QuadPart;
        ULONG length = remaining > AUTH_CHUNK ? AUTH_CHUNK : (ULONG)remaining;
        status = ZwReadFile(handle, NULL, NULL, NULL, &iosb, buffer, length, &offset, NULL);
        if (status == STATUS_PENDING) {
            ZwWaitForSingleObject(handle, FALSE, NULL);
            status = iosb.Status;
        }
        if (!NT_SUCCESS(status)) goto done;
        if (iosb.Information != length) { status = STATUS_ACCESS_DENIED; goto done; }
        status = BCryptHashData(hash, buffer, length, 0);
        if (!NT_SUCCESS(status)) goto done;
        offset.QuadPart += length;
    }
    status = BCryptFinishHash(hash, digest, sizeof(digest), 0);
    if (!NT_SUCCESS(status)) goto done;
    status = STATUS_SUCCESS;
    for (i = 0; i < sizeof(digest); ++i) {
        /* Reference the marker at runtime so link optimization retains it. */
        const char hex[] = "0123456789ABCDEF";
        if (digest[i] != expected[i] || Policy[22 + i * 2] != hex[digest[i] >> 4] ||
            Policy[23 + i * 2] != hex[digest[i] & 15]) { status = STATUS_ACCESS_DENIED; break; }
    }
done:
    if (buffer) ExFreePoolWithTag(buffer, AUTH_TAG);
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (handle) ZwClose(handle);
    ObDereferenceObject(file);
    return NT_SUCCESS(status) ? status : STATUS_ACCESS_DENIED;
}
