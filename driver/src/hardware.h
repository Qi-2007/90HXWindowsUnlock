#ifndef CMP_HOST_TEST
#include <aux_klib.h>
#endif
#include "../include/hardware_scope.h"

/* Legacy segment-0 transport, matching the previous HAL/ECAM implementation.
 * No caller-supplied physical mappings: derive BAR0 and ECAM from PCI/ACPI. */
static UCHAR Mcfg[44 + 16 * 256];
static ULONG McfgLength;
static BOOLEAN Bound;
static ULONG GpuBdf, BridgeBdf, GpuCap, BridgeCap, PmCap, SavedBars[6], SavedCommand;
static ULONGLONG GpuPhysical, BridgePhysical;
static PVOID GpuMapping, BridgeMapping;

static ULONG Slot(ULONG bdf)
{
    PCI_SLOT_NUMBER slot;
    slot.u.AsULONG = 0;
    slot.u.bits.DeviceNumber = (bdf >> 3) & 31;
    slot.u.bits.FunctionNumber = bdf & 7;
    return slot.u.AsULONG;
}
static ULONG PciValue(ULONG bdf, ULONG offset, ULONG width)
{
    ULONG value = MAXULONG;
    if (HalGetBusDataByOffset(PCIConfiguration, bdf >> 8, Slot(bdf), &value, offset, width) != width)
        return MAXULONG;
    return width == 4 ? value : value & ((1u << (width * 8)) - 1);
}
static ULONG Capability(ULONG bdf, ULONG id)
{
    ULONG p = PciValue(bdf, 0x34, 1) & ~3u, i;
    for (i = 0; i < 48 && p >= 0x40 && p <= 0xfc; ++i) {
        ULONG v = PciValue(bdf, p, 2);
        if ((v & 255) == id) return p;
        p = (v >> 8) & ~3u;
    }
    return 0;
}
static VOID HardwareCleanup(void)
{
    Bound = FALSE;
    if (GpuMapping) MmUnmapIoSpace(GpuMapping, CMP_BAR0_BYTES);
    if (BridgeMapping) MmUnmapIoSpace(BridgeMapping, 4096);
    GpuMapping = BridgeMapping = NULL;
}
static VOID HardwareInitialize(void)
{
    ULONG length = 0, i, sum = 0;
    if (!NT_SUCCESS(AuxKlibInitialize())) return;
    if (!NT_SUCCESS(AuxKlibGetSystemFirmwareTable(0x41435049, 0x4746434d,
        Mcfg, sizeof(Mcfg), &length))) return;
    if (length < 60 || length > sizeof(Mcfg) || (length - 44) % 16 ||
        *(UNALIGNED ULONG *)(Mcfg + 0) != 0x4746434d ||
        *(UNALIGNED ULONG *)(Mcfg + 4) != length) return;
    for (i = 0; i < length; ++i) sum += Mcfg[i];
    if (!(sum & 255)) McfgLength = length;
}
static ULONGLONG EcamAddress(ULONG bdf)
{
    ULONG i, matches = 0, bus = bdf >> 8;
    ULONGLONG result = 0;
    for (i = 44; i < McfgLength; i += 16) {
        ULONGLONG base = *(UNALIGNED ULONGLONG *)(Mcfg + i);
        USHORT segment = *(UNALIGNED USHORT *)(Mcfg + i + 8);
        UCHAR first = Mcfg[i + 10], last = Mcfg[i + 11];
        if (!base || (base & 0xfffff) || base > 0x000fffffffffffffULL - 0x10000000 || first > last)
            return 0;
        if (!segment && bus >= first && bus <= last) {
            result = base + ((ULONGLONG)bus << 20) + ((ULONGLONG)(bdf & 255) << 12);
            ++matches;
        }
    }
    return matches == 1 ? result : 0;
}
static NTSTATUS BindHardware(const CMP_TARGET *target)
{
    ULONG low, type, buses, i, boot, command;
    PHYSICAL_ADDRESS address;
    if (target->Gpu > 0xffff || target->Bridge > 0xffff || target->Gpu == target->Bridge)
        return STATUS_INVALID_PARAMETER;
    if (Bound) return target->Gpu == GpuBdf && target->Bridge == BridgeBdf ?
        STATUS_SUCCESS : STATUS_DEVICE_BUSY;
    if (PciValue(target->Gpu, 0, 4) != 0x220d10de ||
        (PciValue(target->Gpu, 0x0e, 1) & 0x7f) != 0 ||
        PciValue(target->Bridge, 8, 4) >> 16 != 0x0604 ||
        (PciValue(target->Bridge, 0x0e, 1) & 0x7f) != 1)
        return STATUS_NO_SUCH_DEVICE;
    buses = PciValue(target->Bridge, 0x18, 4);
    if (((buses >> 8) & 255) != target->Gpu >> 8 || ((buses >> 16) & 255) < target->Gpu >> 8)
        return STATUS_INVALID_PARAMETER;
    low = PciValue(target->Gpu, 0x10, 4); type = low & 6;
    if (!low || low == MAXULONG || (low & 1) || (type != 0 && type != 4))
        return STATUS_INVALID_DEVICE_STATE;
    GpuPhysical = low & ~15u;
    if (type == 4) GpuPhysical |= (ULONGLONG)PciValue(target->Gpu, 0x14, 4) << 32;
    command = PciValue(target->Gpu, 4, 2);
    if (!GpuPhysical || (GpuPhysical & (CMP_BAR0_BYTES - 1)) ||
        GpuPhysical > 0x000fffffffffffffULL - CMP_BAR0_BYTES ||
        command > 65535 || !(command & 2))
        return STATUS_INVALID_DEVICE_STATE;
    GpuCap = Capability(target->Gpu, 0x10); BridgeCap = Capability(target->Bridge, 0x10);
    PmCap = Capability(target->Gpu, 1);
    if (!GpuCap || GpuCap > 0xc0 || !BridgeCap || BridgeCap > 0xc0)
        return STATUS_NOT_SUPPORTED;
    BridgePhysical = EcamAddress(target->Bridge);
    if (!BridgePhysical) return STATUS_NOT_SUPPORTED;
    address.QuadPart = (LONGLONG)BridgePhysical;
    BridgeMapping = MmMapIoSpace(address, 4096, MmNonCached);
    if (!BridgeMapping) return STATUS_INSUFFICIENT_RESOURCES;
    /* Compare immutable identity/routing/capability fields before enabling writes. */
    if (READ_REGISTER_ULONG((PULONG)BridgeMapping) != PciValue(target->Bridge, 0, 4) ||
        READ_REGISTER_ULONG((PULONG)((PUCHAR)BridgeMapping + 8)) != PciValue(target->Bridge, 8, 4) ||
        READ_REGISTER_ULONG((PULONG)((PUCHAR)BridgeMapping + 0x18)) != buses ||
        READ_REGISTER_ULONG((PULONG)((PUCHAR)BridgeMapping + BridgeCap)) != PciValue(target->Bridge, BridgeCap, 4)) {
        HardwareCleanup(); return STATUS_DEVICE_CONFIGURATION_ERROR;
    }
    address.QuadPart = (LONGLONG)GpuPhysical;
    GpuMapping = MmMapIoSpace(address, CMP_BAR0_BYTES, MmNonCached);
    if (!GpuMapping) { HardwareCleanup(); return STATUS_INSUFFICIENT_RESOURCES; }
    boot = READ_REGISTER_ULONG((PULONG)GpuMapping);
    if ((boot & 0x1f000000) != 0x17000000 || (boot & 0xf00000) != 0x200000) {
        HardwareCleanup(); return STATUS_NO_SUCH_DEVICE;
    }
    for (i = 0; i < 6; ++i) SavedBars[i] = PciValue(target->Gpu, 0x10 + i * 4, 4);
    SavedCommand = command;
    GpuBdf = target->Gpu; BridgeBdf = target->Bridge; Bound = TRUE;
    return STATUS_SUCCESS;
}
static NTSTATUS BridgeWord(ULONG offset, ULONG value)
{
    ULONG mask = cmp_bridge_mask(BridgeCap, offset), old;
    PUSHORT word;
    if (!mask || value > 65535 ||
        (offset == BridgeCap + 0x30 && (PciValue(BridgeBdf, BridgeCap + 2, 2) & 15) < 2))
        return STATUS_ACCESS_DENIED;
    word = (PUSHORT)((PUCHAR)BridgeMapping + offset);
    old = READ_REGISTER_USHORT(word);
    if (offset == 0x3e) { old &= ~0x400u; value &= ~0x400u; }
    if ((old ^ value) & ~mask) return STATUS_ACCESS_DENIED;
    /* Exact 16-bit write: never replay adjacent W1C link status. */
    WRITE_REGISTER_USHORT(word, (USHORT)(offset == 0x3e ? value & ~0x400u : value));
    return STATUS_SUCCESS;
}
static NTSTATUS HardwareIoctl(ULONG code, PVOID buffer, ULONG input, ULONG output, ULONG_PTR *bytes)
{
    if (code == CMP_BIND) {
        if (input != sizeof(CMP_TARGET) || output) return STATUS_INFO_LENGTH_MISMATCH;
        return BindHardware((CMP_TARGET *)buffer);
    }
    if (code == CMP_PCI_READ || code == CMP_PCI_WRITE) {
        CMP_PCI request;
        ULONG old, mask = 0;
        if (input != sizeof(request) || output != (code == CMP_PCI_READ ? sizeof(ULONG) : 0))
            return STATUS_INFO_LENGTH_MISMATCH;
        request = *(CMP_PCI *)buffer;
        if (!cmp_pci_valid(request.Bdf, request.Offset, request.Width)) return STATUS_INVALID_PARAMETER;
        if (code == CMP_PCI_READ) {
            ULONG value = MAXULONG;
            ULONG count = HalGetBusDataByOffset(PCIConfiguration, request.Bdf >> 8,
                Slot(request.Bdf), &value, request.Offset, request.Width);
            /* Enumeration sees an absent function as all ones, not a transport error. */
            if (count != request.Width && request.Offset != 0) return STATUS_NO_SUCH_DEVICE;
            *(ULONG *)buffer = value & (request.Width == 4 ? MAXULONG : (1u << (request.Width * 8)) - 1);
            *bytes = sizeof(ULONG); return STATUS_SUCCESS;
        }
        /* D0/memory decoding preparation precedes BAR0 access and binding. */
        if (!Bound) {
            ULONG pm = Capability(request.Bdf, 1);
            if (PciValue(request.Bdf, 0, 4) != 0x220d10de || request.Width != 2 || request.Value > 65535)
                return STATUS_ACCESS_DENIED;
            if (request.Offset == 4) mask = 7;
            else if (pm && request.Offset == pm + 4) mask = 0x8003;
            old = PciValue(request.Bdf, request.Offset, 2);
            if (!mask || ((old ^ request.Value) & ~mask)) return STATUS_ACCESS_DENIED;
            return HalSetBusDataByOffset(PCIConfiguration, request.Bdf >> 8, Slot(request.Bdf),
                &request.Value, request.Offset, request.Width) == request.Width ? STATUS_SUCCESS : STATUS_UNSUCCESSFUL;
        }
        if (request.Bdf == BridgeBdf && request.Width == 2) return BridgeWord(request.Offset, request.Value);
        if (request.Bdf != GpuBdf || PciValue(GpuBdf, 0, 4) != 0x220d10de) return STATUS_ACCESS_DENIED;
        if (request.Width == 4 && request.Offset >= 0x10 && request.Offset <= 0x24) {
            if (request.Value != SavedBars[(request.Offset - 0x10) / 4]) return STATUS_ACCESS_DENIED;
        } else {
            if (request.Width != 2 || request.Value > 65535) return STATUS_ACCESS_DENIED;
            if (request.Offset == 4) mask = 7;
            else if (PmCap && request.Offset == PmCap + 4) mask = 0x8003;
            else if (request.Offset == GpuCap + 0x10) mask = 0x23;
            else if (request.Offset == GpuCap + 0x30 && (PciValue(GpuBdf, GpuCap + 2, 2) & 15) >= 2) mask = 15;
            old = PciValue(GpuBdf, request.Offset, 2);
            /* SBR resets Command bits beyond decoding/BME (e.g. bit 10).
             * Restore only the non-decoding bits captured at bind, just as
             * BAR restoration accepts only the saved resource assignments. */
            if (request.Offset == 4) old = SavedCommand;
            if (!mask || ((old ^ request.Value) & ~mask)) return STATUS_ACCESS_DENIED;
        }
        return HalSetBusDataByOffset(PCIConfiguration, GpuBdf >> 8, Slot(GpuBdf),
            &request.Value, request.Offset, request.Width) == request.Width ? STATUS_SUCCESS : STATUS_UNSUCCESSFUL;
    }
    if (code == CMP_MMIO_READ || code == CMP_MMIO_WRITE) {
        CMP_MMIO request;
        PULONG reg;
        if (input != sizeof(request) || output != (code == CMP_MMIO_READ ? sizeof(ULONG) : 0))
            return STATUS_INFO_LENGTH_MISMATCH;
        request = *(CMP_MMIO *)buffer;
        if (request.Reserved || (request.Address & 3)) return STATUS_INVALID_PARAMETER;
        /* Arena reads use the existing cached kernel mapping, never an MMIO alias. */
        if (Buffer && UserAddress && cmp_range(request.Address, (ULONGLONG)Physical.QuadPart, CMP_ARENA_BYTES)) {
            if (code != CMP_MMIO_READ) return STATUS_ACCESS_DENIED;
            KeMemoryBarrier();
            *(ULONG *)buffer = *(volatile ULONG *)((PUCHAR)Buffer + (SIZE_T)(request.Address - Physical.QuadPart));
            *bytes = sizeof(ULONG); return STATUS_SUCCESS;
        }
        if (!Bound) return STATUS_INVALID_DEVICE_STATE;
        if (cmp_range(request.Address, GpuPhysical, CMP_BAR0_BYTES))
            reg = (PULONG)((PUCHAR)GpuMapping + (SIZE_T)(request.Address - GpuPhysical));
        else if (cmp_range(request.Address, BridgePhysical, 256)) {
            ULONG offset = (ULONG)(request.Address - BridgePhysical);
            reg = (PULONG)((PUCHAR)BridgeMapping + offset);
            if (code == CMP_MMIO_WRITE) {
                if (offset == 0x3c) {
                    if ((request.Value & 65535) != (READ_REGISTER_ULONG(reg) & 65535)) return STATUS_ACCESS_DENIED;
                    return BridgeWord(0x3e, request.Value >> 16);
                }
                if (request.Value > 65535) return STATUS_ACCESS_DENIED;
                return BridgeWord(offset, request.Value);
            }
        } else return STATUS_ACCESS_DENIED;
        if (code == CMP_MMIO_READ) { *(ULONG *)buffer = READ_REGISTER_ULONG(reg); *bytes = sizeof(ULONG); }
        else WRITE_REGISTER_ULONG(reg, request.Value);
        return STATUS_SUCCESS;
    }
    return STATUS_INVALID_DEVICE_REQUEST;
}
