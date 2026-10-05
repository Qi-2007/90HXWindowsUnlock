#define CMP_CALLER_AUTH_TEST
#include "../src/caller_auth.c"
int main(void)
{
    FILE_OBJECT file = { FO_SYNCHRONOUS_IO };
    PS_CREATE_NOTIFY_INFO info = { &file, STATUS_SUCCESS };
    PEPROCESS process = (void *)1, stranger = (void *)2;
    assert(CallerAuthInitialize()==STATUS_SUCCESS);
    assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); /* preexisting process */
    Notify(process,NULL,&info);
    assert(CallerAuthVerify(process)==STATUS_SUCCESS); /* multi-chunk read */
    assert(CallerAuthVerify(stranger)==STATUS_ACCESS_DENIED);
    Tamper=1; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); Tamper=0;
    ShortRead=1; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); ShortRead=0;
    FailRead=1; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); FailRead=0;
    FailOpen=1; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); FailOpen=0;
    FailAllocation=1; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); FailAllocation=0;
    ImageSize=0; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED);
    ImageSize=AUTH_MAX_IMAGE+1LL; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED);
    ImageSize=100000;
    Irql=1; assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED); Irql=0;
    Notify(process,NULL,NULL);
    assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED);
    assert(!LivePools && !LiveHandles && !References);
    FailAllocation=1; Notify(process,NULL,&info); FailAllocation=0;
    assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED);
    info.FileObject=NULL; Notify(process,NULL,&info); info.FileObject=&file;
    assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED);
    file.Flags=0; Notify(process,NULL,&info); file.Flags=FO_SYNCHRONOUS_IO;
    assert(CallerAuthVerify(process)==STATUS_ACCESS_DENIED);
    Notify(process,NULL,&info); Notify(stranger,NULL,&info);
    CallerAuthShutdown();
    assert(!LivePools && !LiveHandles && !References && !Notify);
    RegistrationStatus=STATUS_ACCESS_DENIED;
    assert(CallerAuthInitialize()==STATUS_ACCESS_DENIED);
    puts("CALLER_AUTH_TESTS_PASSED: allow/deny, image lifecycle, I/O failures, IRQL, resource cleanup (mock kernel/crypto)");
    return 0;
}
