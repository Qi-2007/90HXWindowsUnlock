#ifndef CMP90HX_ARENA_STATE_H
#define CMP90HX_ARENA_STATE_H
/* Boot-lifetime state: no operation can clear quarantine. */
#define ARENA_CLEAN 0u
#define ARENA_ARMED 1u
#define ARENA_QUARANTINED 2u
static unsigned arena_disconnect(unsigned state)
{ return state == ARENA_ARMED ? ARENA_QUARANTINED : state; }
static int arena_arm(unsigned *state)
{ if (*state != ARENA_CLEAN) return 0; *state = ARENA_ARMED; return 1; }
static int arena_complete(unsigned *state)
{ if (*state != ARENA_ARMED) return 0; *state = ARENA_CLEAN; return 1; }
#endif
