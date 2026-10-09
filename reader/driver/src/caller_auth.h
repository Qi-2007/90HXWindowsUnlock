#ifndef CMP_CALLER_AUTH_H
#define CMP_CALLER_AUTH_H
NTSTATUS CallerAuthInitialize(void);
void CallerAuthShutdown(void);
NTSTATUS CallerAuthVerify(PEPROCESS process);
#endif
