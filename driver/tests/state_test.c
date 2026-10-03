#include <assert.h>
#include <stdio.h>
#include "../include/arena_state.h"
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
    puts("DMA ownership state tests passed"); return 0;
}
