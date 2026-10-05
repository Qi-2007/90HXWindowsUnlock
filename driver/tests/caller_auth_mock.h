#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <stdio.h>
typedef int32_t NTSTATUS;
typedef uint32_t ULONG;
typedef int64_t LONGLONG;
typedef unsigned char UCHAR, *PUCHAR;
typedef void *HANDLE, *PEPROCESS, *BCRYPT_ALG_HANDLE, *BCRYPT_HASH_HANDLE;
typedef struct { LONGLONG QuadPart; } LARGE_INTEGER;
typedef struct { NTSTATUS Status; size_t Information; } IO_STATUS_BLOCK;
typedef struct { LARGE_INTEGER EndOfFile; } FILE_STANDARD_INFORMATION;
typedef struct { ULONG Flags; } FILE_OBJECT, *PFILE_OBJECT;
typedef struct { PFILE_OBJECT FileObject; NTSTATUS CreationStatus; } PS_CREATE_NOTIFY_INFO, *PPS_CREATE_NOTIFY_INFO;
typedef struct _LIST_ENTRY { struct _LIST_ENTRY *Flink, *Blink; } LIST_ENTRY, *PLIST_ENTRY;
typedef int FAST_MUTEX;
#define STATUS_SUCCESS ((NTSTATUS)0)
#define STATUS_PENDING ((NTSTATUS)0x103)
#define STATUS_ACCESS_DENIED ((NTSTATUS)0xc0000022)
#define STATUS_INSUFFICIENT_RESOURCES ((NTSTATUS)0xc000009a)
#define NT_SUCCESS(s) ((s) >= 0)
#define PASSIVE_LEVEL 0
#define TRUE 1
#define FALSE 0
#define FO_SYNCHRONOUS_IO 2
#define OBJ_KERNEL_HANDLE 0
#define FILE_READ_DATA 1
#define SYNCHRONIZE 2
#define KernelMode 0
#define POOL_FLAG_NON_PAGED 0
#define FileStandardInformation 0
#define BCRYPT_SHA256_ALGORITHM 0
#define UNREFERENCED_PARAMETER(x) ((void)(x))
#define CONTAINING_RECORD(p,t,f) ((t *)((char *)(p)-offsetof(t,f)))
#include <stddef.h>
#define CMP_CALLER_SHA256_HEX "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
#define CMP_CALLER_SHA256_BYTES {0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa,0xaa}
static void *FileType;
static void **IoFileObjectType = &FileType;
static int Irql, FailAllocation, FailOpen, FailRead, ShortRead, Tamper, LivePools, LiveHandles, References;
static LONGLONG ImageSize = 100000;
static NTSTATUS RegistrationStatus;
static void (*Notify)(PEPROCESS, HANDLE, PPS_CREATE_NOTIFY_INFO);
static void InitializeListHead(PLIST_ENTRY h) { h->Flink=h->Blink=h; }
static int IsListEmpty(PLIST_ENTRY h) { return h->Flink==h; }
static void InsertTailList(PLIST_ENTRY h, PLIST_ENTRY e) { e->Flink=h; e->Blink=h->Blink; h->Blink->Flink=e; h->Blink=e; }
static void RemoveEntryList(PLIST_ENTRY e) { e->Blink->Flink=e->Flink; e->Flink->Blink=e->Blink; }
static PLIST_ENTRY RemoveHeadList(PLIST_ENTRY h) { PLIST_ENTRY e=h->Flink; RemoveEntryList(e); return e; }
static void ExInitializeFastMutex(FAST_MUTEX *m) { *m=0; }
static void ExAcquireFastMutex(FAST_MUTEX *m) { assert(!*m); *m=1; Irql=1; }
static void ExReleaseFastMutex(FAST_MUTEX *m) { assert(*m); *m=0; Irql=0; }
static int KeGetCurrentIrql(void) { return Irql; }
static void *ExAllocatePool2(int flags, size_t size, ULONG tag) {
    void *p; (void)flags; (void)tag;
    if (FailAllocation) return NULL;
    p=calloc(1,size); if(p) ++LivePools; return p;
}
static void ExFreePoolWithTag(void *p, ULONG tag) { (void)tag; --LivePools; free(p); }
static void ObReferenceObject(void *p) { assert(p); ++References; }
static void ObDereferenceObject(void *p) { assert(p); --References; }
static NTSTATUS PsSetCreateProcessNotifyRoutineEx(void (*routine)(PEPROCESS,HANDLE,PPS_CREATE_NOTIFY_INFO), int remove) {
    if(remove) Notify=NULL; else if(NT_SUCCESS(RegistrationStatus)) Notify=routine;
    return RegistrationStatus;
}
static NTSTATUS ObOpenObjectByPointer(void *file, ULONG flags, void *access, ULONG rights, void *type, int mode, HANDLE *out) {
    (void)flags; (void)access; (void)rights; (void)type; (void)mode;
    assert(Irql==PASSIVE_LEVEL);
    if(FailOpen) return STATUS_ACCESS_DENIED;
    *out=file; ++LiveHandles; return STATUS_SUCCESS;
}
static NTSTATUS ZwClose(HANDLE h) { assert(h); --LiveHandles; return STATUS_SUCCESS; }
static NTSTATUS ZwQueryInformationFile(HANDLE h, IO_STATUS_BLOCK *io, FILE_STANDARD_INFORMATION *size, size_t bytes, int kind) {
    (void)h; (void)bytes; (void)kind; assert(Irql==0); io->Status=0; size->EndOfFile.QuadPart=ImageSize; return 0;
}
static NTSTATUS ZwReadFile(HANDLE h, void *event, void *apc, void *context, IO_STATUS_BLOCK *io, PUCHAR buffer, ULONG length, LARGE_INTEGER *offset, void *key) {
    (void)h; (void)event; (void)apc; (void)context; (void)key;
    assert(Irql==0); assert(offset->QuadPart>=0 && offset->QuadPart+length<=ImageSize);
    if(FailRead) return STATUS_ACCESS_DENIED;
    memset(buffer,0xab,length); io->Information=ShortRead?length-1:length; io->Status=0; return 0;
}
static NTSTATUS ZwWaitForSingleObject(HANDLE h, int alert, void *timeout) { (void)h; (void)alert; (void)timeout; return 0; }
/* Crypto is mocked: these tests exercise policy/lifetime/I/O failures, not SHA-256. */
static NTSTATUS BCryptOpenAlgorithmProvider(BCRYPT_ALG_HANDLE *h, int name, void *provider, ULONG flags) {
    (void)name; (void)provider; (void)flags; assert(Irql==0); *h=(void *)1; return 0;
}
static NTSTATUS BCryptCreateHash(BCRYPT_ALG_HANDLE a, BCRYPT_HASH_HANDLE *h, void *object, ULONG length, void *secret, ULONG secretLength, ULONG flags) {
    (void)a; (void)object; (void)length; (void)secret; (void)secretLength; (void)flags; *h=(void *)1; return 0;
}
static NTSTATUS BCryptHashData(BCRYPT_HASH_HANDLE h, PUCHAR p, ULONG length, ULONG flags) { (void)h; (void)p; (void)length; (void)flags; assert(Irql==0); return 0; }
static NTSTATUS BCryptFinishHash(BCRYPT_HASH_HANDLE h, PUCHAR digest, ULONG length, ULONG flags) { (void)h; (void)flags; memset(digest,Tamper?0xbb:0xaa,length); return 0; }
static void BCryptDestroyHash(BCRYPT_HASH_HANDLE h) { (void)h; }
static void BCryptCloseAlgorithmProvider(BCRYPT_ALG_HANDLE h, ULONG flags) { (void)h; (void)flags; }
