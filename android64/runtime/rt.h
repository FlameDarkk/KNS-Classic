/* Shared declarations of the 64-bit runtime (android64/runtime). */
#pragma once
#include "guest.h"
#include "image.h"

#include <stdint.h>
#include <string.h>
#ifdef __ANDROID__
#include <android/log.h>
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Guest address space layout (32-bit guest pointers into one 4 GB host region). */
#define GUEST_HEAP_BASE ((GUEST_DATA_END + 0xFFFFFu) & ~0xFFFFFu)
#define GUEST_HEAP_END 0xF0000000u
#define HOST_TRAP_BASE 0xFFFE8000u   /* runtime-defined traps: JNI tables, thread exit, callbacks */
#define HOST_TRAP_MAX 1024

extern uint8_t *guest_mem;
#define G(a) (guest_mem + (uint32_t)(a))
#define G32(a) (*(uint32_t *)G(a))
#define GS(a) ((char *)G(a))           /* guest C string */
#define GP(a) ((a) ? (void *)G(a) : NULL)

typedef struct RtThread {
  CPU cpu;
  uint32_t handle;       /* guest pthread_t */
  uint32_t stack_base, stack_size;
  uint32_t tls_area;
  uint32_t start, arg, retval;
  int detached, done;
  void *host;            /* pthread_t of the host thread */
  void *jni_env;         /* host JNIEnv* for this thread, if attached */
} RtThread;

typedef void (*HostTrap)(CPU *c, int arg);

/* guest_rt.c */
extern int rt_log_stderr;
void rt_log(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
void rt_fatal(CPU *c, const char *fmt, ...) __attribute__((noreturn, format(printf, 2, 3)));
void rt_dump(CPU *c, const char *why);
uint32_t guest_malloc(uint32_t n);
uint32_t guest_calloc(uint32_t n);
uint32_t guest_realloc(uint32_t p, uint32_t n);
void guest_free(uint32_t p);
uint32_t guest_size(uint32_t p);
uint32_t guest_strdup(const char *s);
uint32_t host_trap_add(const char *name, HostTrap fn, int arg);
void host_trap_call(CPU *c, uint32_t i);
RtThread *rt_thread(CPU *c);
CPU *rt_cpu(void);
CPU *rt_cpu_for_thread(void);
void rt_bind_thread(RtThread *t);
uint32_t guest_call(CPU *c, uint32_t addr, int nargs, const uint32_t *args, uint32_t *hi);
uint32_t guest_export(const char *name);
int guest_boot(void);
void guest_run_init_array(CPU *c);

/* libc_shims.c */
void libc_init(void);
uint32_t rt_thread_register(RtThread *t);

/* jni_guest.c: the guest-side JavaVM / JNIEnv. Each slot is a host trap that calls the backend. */
typedef void (*JniBackend)(CPU *c, int slot, const char *name);
extern JniBackend jni_backend;        /* JNIEnv functions, slot = JNI table index */
extern JniBackend jni_vm_backend;     /* JavaVM functions, slot = invoke interface index */
extern const char *const jni_env_names[];
extern const int jni_env_count;
uint32_t jni_guest_init(void);        /* returns the guest JavaVM*; creates the guest JNIEnv */
uint32_t jni_guest_env(void);
uint32_t jni_guest_vm(void);

/* argument helpers for import shims: word i of the AAPCS argument list */
static inline uint32_t ARG(CPU *c, int i) { return i < 4 ? c->r[i] : G32(c->r[13] + 4 * (i - 4)); }

#ifdef __cplusplus
}
#endif
