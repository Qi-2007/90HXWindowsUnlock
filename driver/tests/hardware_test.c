/* Host test of the actual register IOCTL handler with fake PCI/MMIO/ACPI.
 * No driver is installed and no real hardware is accessed. */
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define CMP_HOST_TEST
#define UNALIGNED
#define C_ASSERT(x) _Static_assert(x, #x)
#define CTL_CODE(d,f,m,a) (((uint32_t)(d)<<16)|((uint32_t)(a)<<14)|((uint32_t)(f)<<2)|(m))
#define METHOD_BUFFERED 0
#define FILE_READ_DATA 1
#define FILE_WRITE_DATA 2
#define FALSE 0
#define TRUE 1
#define MAXULONG UINT32_MAX
#define PCIConfiguration 4
#define MmNonCached 0
#define NT_SUCCESS(s) ((s) == 0)
#define STATUS_SUCCESS 0
#define STATUS_INVALID_PARAMETER (-1)
#define STATUS_DEVICE_BUSY (-2)
#define STATUS_NO_SUCH_DEVICE (-3)
#define STATUS_INVALID_DEVICE_STATE (-4)
#define STATUS_NOT_SUPPORTED (-5)
#define STATUS_INSUFFICIENT_RESOURCES (-6)
#define STATUS_DEVICE_CONFIGURATION_ERROR (-7)
#define STATUS_ACCESS_DENIED (-8)
#define STATUS_INFO_LENGTH_MISMATCH (-9)
#define STATUS_UNSUCCESSFUL (-10)
#define STATUS_INVALID_DEVICE_REQUEST (-11)
typedef uint8_t UCHAR, BOOLEAN;
typedef uint16_t USHORT, *PUSHORT;
typedef uint32_t ULONG, *PULONG;
typedef uint64_t ULONGLONG;
typedef int64_t LONGLONG;
typedef uintptr_t ULONG_PTR;
typedef size_t SIZE_T;
typedef int NTSTATUS;
typedef void VOID, *PVOID;
typedef UCHAR *PUCHAR;
typedef struct { LONGLONG QuadPart; } PHYSICAL_ADDRESS;
typedef union { ULONG AsULONG; struct { ULONG DeviceNumber:5, FunctionNumber:3, Reserved:24; } bits; } SLOT_UNION;
typedef struct { SLOT_UNION u; } PCI_SLOT_NUMBER;
#include "../include/protocol.h"
static UCHAR GpuConfig[256], BridgeConfig[256], BridgeMemory[4096], Arena[4096];
static PUCHAR GpuMemory;
static PVOID Buffer, UserAddress;
static PHYSICAL_ADDRESS Physical;
static unsigned Maps, Unmaps, Writes, HalWrites;
static const ULONG TestGpu = 0x100, TestBridge = 8;
static const ULONGLONG TestBar = 0x40000000, TestEcam = 0xe0008000;
static ULONG HalGetBusDataByOffset(int kind, ULONG bus, ULONG slot, PVOID buffer, ULONG offset, ULONG width)
{
    ULONG bdf = (bus << 8) | ((slot & 31) << 3) | ((slot >> 5) & 7);
    PUCHAR config = bdf == TestGpu ? GpuConfig : bdf == TestBridge ? BridgeConfig : NULL;
    assert(kind == PCIConfiguration && offset + width <= 256);
    if (!config) { memset(buffer, 255, width); return width; }
    memcpy(buffer, config + offset, width); return width;
}
static ULONG HalSetBusDataByOffset(int kind, ULONG bus, ULONG slot, PVOID buffer, ULONG offset, ULONG width)
{
    assert(kind == PCIConfiguration && bus == 1 && slot == 0);
    assert(offset + width <= 256);
    memcpy(GpuConfig + offset, buffer, width); ++HalWrites; return width;
}
static NTSTATUS AuxKlibInitialize(void) { return STATUS_SUCCESS; }
static NTSTATUS AuxKlibGetSystemFirmwareTable(ULONG provider, ULONG id, PVOID buffer, ULONG size, PULONG length)
{
    UCHAR table[60] = {0}; unsigned i, sum = 0;
    ULONGLONG base = 0xe0000000;
    assert(provider == 0x41435049 && id == 0x4746434d && size >= sizeof(table));
    memcpy(table, &id, 4); table[4] = 60; table[55] = 255;
    memcpy(table + 44, &base, 8);
    for (i = 0; i < 60; ++i) sum += table[i];
    table[9] = (UCHAR)(0u - sum);
    memcpy(buffer, table, 60); *length = 60; return STATUS_SUCCESS;
}
static PVOID MmMapIoSpace(PHYSICAL_ADDRESS address, SIZE_T size, int cache)
{
    assert(cache == MmNonCached); ++Maps;
    if ((ULONGLONG)address.QuadPart == TestEcam && size == 4096) return BridgeMemory;
    if ((ULONGLONG)address.QuadPart == TestBar && size == 16 * 1024 * 1024) return GpuMemory;
    assert(0); return NULL;
}
static VOID MmUnmapIoSpace(PVOID address, SIZE_T size) { assert(address && size); ++Unmaps; }
static ULONG READ_REGISTER_ULONG(PULONG reg) { ULONG value; memcpy(&value, reg, 4); return value; }
static USHORT READ_REGISTER_USHORT(PUSHORT reg) { USHORT value; memcpy(&value, reg, 2); return value; }
static VOID WRITE_REGISTER_ULONG(PULONG reg, ULONG value) { memcpy(reg, &value, 4); ++Writes; }
static VOID WRITE_REGISTER_USHORT(PUSHORT reg, USHORT value) { memcpy(reg, &value, 2); ++Writes; }
static VOID KeMemoryBarrier(void) { }
#include "../src/hardware.h"
typedef union { CMP_TARGET target; CMP_PCI pci; CMP_MMIO mmio; ULONG value; } PACKET;
static NTSTATUS Call(ULONG code, PACKET *packet, ULONG input, ULONG output)
{
    ULONG_PTR bytes = 0;
    NTSTATUS status = HardwareIoctl(code, packet, input, output, &bytes);
    assert(bytes == (status == STATUS_SUCCESS ? output : 0));
    return status;
}
static VOID Config(PUCHAR config, ULONG offset, ULONG value)
{ memcpy(config + offset, &value, 4); }
int main(void)
{
    PACKET p;
    unsigned before;
    GpuMemory = calloc(1, CMP_BAR0_BYTES); assert(GpuMemory);
    Config(GpuConfig, 0, 0x220d10de); Config(GpuConfig, 4, 6);
    Config(GpuConfig, 0x10, (ULONG)TestBar); Config(GpuConfig, 0x34, 0x50);
    Config(GpuConfig, 0x50, 0x6001); Config(GpuConfig, 0x60, 0x00020010);
    Config(GpuMemory, 0, 0x172000a1);
    Config(BridgeConfig, 0, 0x12348086); Config(BridgeConfig, 8, 0x06040000);
    Config(BridgeConfig, 0x0c, 0x00010000); Config(BridgeConfig, 0x18, 0x00010100);
    Config(BridgeConfig, 0x34, 0x70); Config(BridgeConfig, 0x70, 0x00020010);
    memcpy(BridgeMemory, BridgeConfig, 256); Config(BridgeMemory, 0x80, 0xdead0000);
    HardwareInitialize(); assert(McfgLength == 60);
    p.target = (CMP_TARGET){TestBridge, TestGpu};
    assert(Call(CMP_BIND, &p, 8, 0) == STATUS_NO_SUCH_DEVICE && Maps == 0);
    p.target = (CMP_TARGET){TestGpu, TestBridge};
    Config(GpuConfig, 4, 0);
    assert(Call(CMP_BIND, &p, 8, 0) == STATUS_INVALID_DEVICE_STATE && Maps == 0);
    Config(GpuConfig, 4, 6);
    p.pci = (CMP_PCI){TestGpu, 4, 2, 6};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_SUCCESS);
    p.pci = (CMP_PCI){TestBridge, 4, 2, 6};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    p.pci = (CMP_PCI){TestGpu, 4, 2, 0x1006};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    p.pci = (CMP_PCI){TestGpu, 0x0e, 1, 0};
    assert(Call(CMP_PCI_READ, &p, 16, 4) == STATUS_SUCCESS && p.value == 0);
    p.pci = (CMP_PCI){TestGpu, 0x10, 4, (ULONG)TestBar};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);

    p.pci = (CMP_PCI){0xffff, 0, 4, 0};
    assert(Call(CMP_PCI_READ, &p, 16, 4) == STATUS_SUCCESS && p.value == MAXULONG);
    p.pci = (CMP_PCI){TestGpu, 0x72, 4, 0};
    assert(Call(CMP_PCI_READ, &p, 16, 4) == STATUS_INVALID_PARAMETER);
    p.target = (CMP_TARGET){TestGpu, TestBridge};
    assert(Call(CMP_BIND, &p, 7, 0) == STATUS_INFO_LENGTH_MISMATCH && Maps == 0);
    Config(GpuConfig, 4, 0x406); /* Real machine: Interrupt Disable + MEM/BME. */
    BridgeMemory[0] ^= 1;
    assert(Call(CMP_BIND, &p, 8, 0) == STATUS_DEVICE_CONFIGURATION_ERROR && !Bound);
    BridgeMemory[0] ^= 1;
    assert(Call(CMP_BIND, &p, 8, 0) == STATUS_SUCCESS && Bound);
    Config(GpuConfig, 4, 0); /* SBR clears the whole Command register. */
    before = HalWrites;
    p.pci = (CMP_PCI){TestGpu, 4, 2, 0x406};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_SUCCESS && HalWrites == before + 1);
    assert(PciValue(TestGpu, 4, 2) == 0x406);
    p.pci = (CMP_PCI){TestGpu, 4, 2, 0x1406};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED && HalWrites == before + 1);
    p.pci = (CMP_PCI){TestGpu, 4, 2, 6};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED && HalWrites == before + 1);
    Config(GpuConfig, 4, 0); /* The second SBR must use the same baseline. */
    p.pci = (CMP_PCI){TestGpu, 4, 2, 0x406};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_SUCCESS && HalWrites == before + 2);
    puts("Saved Command 0x406 restored after both SBRs; unrelated bits remain denied.");
    p.target = (CMP_TARGET){TestGpu, 16};
    assert(Call(CMP_BIND, &p, 8, 0) == STATUS_DEVICE_BUSY);
    before = Writes;
    p.mmio = (CMP_MMIO){TestBar + CMP_BAR0_BYTES, 1, 0};
    assert(Call(CMP_MMIO_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED && Writes == before);
    p.mmio = (CMP_MMIO){TestBar + 1, 1, 0};
    assert(Call(CMP_MMIO_WRITE, &p, 16, 0) == STATUS_INVALID_PARAMETER);
    p.mmio = (CMP_MMIO){TestBar, 0, 1};
    assert(Call(CMP_MMIO_READ, &p, 16, 4) == STATUS_INVALID_PARAMETER);
    p.mmio = (CMP_MMIO){TestBar + CMP_BAR0_BYTES - 4, 0x12345678, 0};
    assert(Call(CMP_MMIO_WRITE, &p, 16, 0) == STATUS_SUCCESS);
    assert(Call(CMP_MMIO_READ, &p, 16, 4) == STATUS_SUCCESS && p.value == 0x12345678);
    p.pci = (CMP_PCI){TestGpu, 0x10, 4, (ULONG)TestBar + 0x1000000};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    p.pci = (CMP_PCI){TestGpu, 0x10, 4, (ULONG)TestBar};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_SUCCESS);
    p.pci = (CMP_PCI){TestBridge, 0x80, 2, 0x20};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_SUCCESS);
    assert(READ_REGISTER_ULONG((PULONG)(BridgeMemory + 0x80)) == 0xdead0020);
    p.mmio = (CMP_MMIO){TestEcam + 0x80, 0, 0};
    assert(Call(CMP_MMIO_WRITE, &p, 16, 0) == STATUS_SUCCESS);
    assert(READ_REGISTER_ULONG((PULONG)(BridgeMemory + 0x80)) == 0xdead0000);
    p.mmio = (CMP_MMIO){TestEcam + 0x18, 0, 0};
    assert(Call(CMP_MMIO_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    p.pci = (CMP_PCI){TestBridge, 0x80, 2, 0x100};
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    p.pci = (CMP_PCI){TestBridge, 0xa0, 2, 2};
    Config(BridgeConfig, 0x70, 0x00010010);
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    Config(BridgeConfig, 0x70, 0x00020010);
    assert(Call(CMP_PCI_WRITE, &p, 16, 0) == STATUS_SUCCESS);
    Buffer = Arena; UserAddress = Arena; Physical.QuadPart = 0x20000000;
    Config(Arena, 0, 0x90580001);
    p.mmio = (CMP_MMIO){0x20000000, 0, 0};
    assert(Call(CMP_MMIO_READ, &p, 16, 4) == STATUS_SUCCESS && p.value == 0x90580001);
    p.mmio = (CMP_MMIO){0x20000000, 0, 0};
    assert(Call(CMP_MMIO_WRITE, &p, 16, 0) == STATUS_ACCESS_DENIED);
    assert(Call(CMP_MMIO_READ, &p, 15, 4) == STATUS_INFO_LENGTH_MISMATCH);
    HardwareCleanup(); assert(!Bound && Unmaps == Maps);
    free(GpuMemory);
    puts("Unified driver IOCTL tests passed: bind, packet sizes, scoped PCI/MMIO, BAR restore, W1C and arena readback.");
    return 0;
}
