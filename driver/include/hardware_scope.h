#ifndef CMP90HX_HARDWARE_SCOPE_H
#define CMP90HX_HARDWARE_SCOPE_H
/* Pure validation helpers, also exercised by the host tests. */
#define CMP_BAR0_BYTES (16u * 1024u * 1024u)
static int cmp_pci_valid(unsigned bdf, unsigned offset, unsigned width)
{
    return bdf <= 0xffff && (width == 1 || width == 2 || width == 4) &&
        offset <= 256 - width && offset % width == 0;
}
static int cmp_range(unsigned long long address, unsigned long long base,
    unsigned long long length)
{
    return !(address & 3) && length >= 4 && address >= base && address - base <= length - 4;
}
static unsigned cmp_bridge_mask(unsigned cap, unsigned offset)
{
    if (offset == 0x3e) return 0x40;
    if (offset == cap + 0x10) return 0x23;
    if (offset == cap + 0x30) return 15;
    return 0;
}
#endif
