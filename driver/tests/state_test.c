#include <assert.h>
#include <stdio.h>
#include "../include/arena_state.h"
#include "../include/hardware_scope.h"
int main(void)
{
    unsigned s = ARENA_CLEAN;
    assert(arena_disconnect(s) == ARENA_CLEAN);
    assert(!arena_complete(&s));
    assert(arena_arm(&s)); assert(!arena_arm(&s));
    assert(arena_complete(&s)); assert(s == ARENA_CLEAN);
    assert(arena_arm(&s)); s = arena_disconnect(s);
    assert(s == ARENA_QUARANTINED);
    assert(!arena_arm(&s)); assert(!arena_complete(&s));
    assert(arena_disconnect(s) == ARENA_QUARANTINED);
    assert(cmp_pci_valid(0xffff, 255, 1));
    assert(cmp_pci_valid(0x0200, 0x72, 2));
    assert(!cmp_pci_valid(0x10000, 0, 4));
    assert(!cmp_pci_valid(0, 255, 2));
    assert(!cmp_pci_valid(0, 0xffffffffu, 4));
    assert(!cmp_pci_valid(0, 0, 0));
    assert(!cmp_pci_valid(0, 0x72, 4));
    assert(cmp_range(0x100000, 0x100000, CMP_BAR0_BYTES));
    assert(cmp_range(0x10ffffc, 0x100000, CMP_BAR0_BYTES));
    assert(!cmp_range(0x1100000, 0x100000, CMP_BAR0_BYTES));
    assert(!cmp_range(0xffffc, 0x100000, CMP_BAR0_BYTES));
    assert(!cmp_range(0x100001, 0x100000, CMP_BAR0_BYTES));
    assert(!cmp_range(0xfffffffffffffffcuLL, 0x100000, CMP_BAR0_BYTES));
    assert(cmp_bridge_mask(0x70, 0x3e) == 0x40);
    assert(cmp_bridge_mask(0x70, 0x80) == 0x23);
    assert(cmp_bridge_mask(0x70, 0xa0) == 15);
    assert(cmp_bridge_mask(0x70, 0x82) == 0);
    assert(cmp_bridge_mask(0x70, 0x18) == 0);
    puts("DMA ownership state tests passed"); return 0;
}
