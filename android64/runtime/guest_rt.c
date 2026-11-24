/* Core of the 64-bit runtime: guest memory, image load, block dispatch, host -> guest calls,
 * guest heap and guest threads. Replaces what apk-cpu-engine/runtime.js (loadElf, malloc,
 * threads, callSync) does in the browser. */
#include "rt.h"

#include <errno.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <unistd.h>

uint8_t *guest_mem;
static __thread CPU *tls_cpu;

/* image.S: the relocated image produced by arm2c.py */
extern const uint8_t guest_image[];

/* ------------------------------------------------------------------ logging */
void rt_log(const char *fmt, ...) {
  char buf[1024];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(buf, sizeof buf, fmt, ap);
  va_end(ap);
#ifdef __ANDROID__
  __android_log_write(4, "KnS64", buf);
#endif
  if (rt_log_stderr) fprintf(stderr, "%s\n", buf);
}
int rt_log_stderr = 0;

void rt_dump(CPU *c, const char *why) {
  rt_log("%s: pc=%08x lr=%08x sp=%08x depth=%d", why, c->pc, c->r[14], c->r[13], c->depth);
  for (int i = 0; i < 16; i += 4)
    rt_log("  r%-2d %08x %08x %08x %08x", i, c->r[i], c->r[i + 1], c->r[i + 2], c->r[i + 3]);
}

void rt_fatal(CPU *c, const char *fmt, ...) {
  char buf[512];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(buf, sizeof buf, fmt, ap);
  va_end(ap);
  if (c) rt_dump(c, buf); else rt_log("%s", buf);
  abort();
}

/* ------------------------------------------------------------------ memory */
static pthread_mutex_t heap_lock = PTHREAD_MUTEX_INITIALIZER;
static uint32_t heap_brk;
/* free lists: 16-byte classes up to 1 KB, then powers of two */
#define SMALL_CLASSES 64
static uint32_t free_small[SMALL_CLASSES + 1];
static uint32_t free_big[33];

static uint32_t size_class(uint32_t n, int *idx, int *big) {
  if (n <= 1024) { uint32_t s = n < 16 ? 16 : (n + 15) & ~15u; *idx = s / 16; *big = 0; return s; }
  int b = 32 - __builtin_clz(n - 1);
  *idx = b; *big = 1;
  return 1u << b;
}

/* Every block has a 16-byte header; the size sits at p - 4. Pointers are 16-aligned (Scudo-like),
 * which the patched operator delete relies on (patch_native.py ALIGN_DELETE_TARGETS). */
uint32_t guest_malloc(uint32_t n) {
  int idx, big;
  if (n > 0x40000000u) return 0;
  uint32_t size = size_class(n ? n : 1, &idx, &big);
  pthread_mutex_lock(&heap_lock);
  uint32_t *list = big ? &free_big[idx] : &free_small[idx];
  uint32_t p = *list;
  if (p) {
    *list = G32(p);
  } else {
    p = heap_brk + 16;
    if ((uint64_t)p + size > GUEST_HEAP_END) { pthread_mutex_unlock(&heap_lock); rt_log("guest heap exhausted (%u)", n); return 0; }
    heap_brk = p + size;
    G32(p - 4) = size;
  }
  pthread_mutex_unlock(&heap_lock);
  return p;
}

void guest_free(uint32_t p) {
  if (!p || p & 15 || p < GUEST_HEAP_BASE || p >= heap_brk) return;
  uint32_t size = G32(p - 4);
  int idx, big;
  size_class(size, &idx, &big);
  if (size != (big ? 1u << idx : (uint32_t)idx * 16)) return;   /* not ours / corrupt: leak */
  pthread_mutex_lock(&heap_lock);
  uint32_t *list = big ? &free_big[idx] : &free_small[idx];
  G32(p) = *list;
  *list = p;
  pthread_mutex_unlock(&heap_lock);
}

uint32_t guest_size(uint32_t p) { return p ? G32(p - 4) : 0; }

uint32_t guest_calloc(uint32_t n) {
  uint32_t p = guest_malloc(n);
  if (p) memset(G(p), 0, n);
  return p;
}

uint32_t guest_realloc(uint32_t p, uint32_t n) {
  if (!p) return guest_malloc(n);
  uint32_t old = guest_size(p);
  if (n <= old) return p;
  uint32_t q = guest_malloc(n);
  if (q) { memcpy(G(q), G(p), old); guest_free(p); }
  return q;
}

uint32_t guest_strdup(const char *s) {
  size_t n = strlen(s) + 1;
  uint32_t p = guest_malloc((uint32_t)n);
  if (p) memcpy(G(p), s, n);
  return p;
}

/* ------------------------------------------------------------------ dispatch */
static const GuestBlock *find_block(uint32_t key) {
  unsigned lo = 0, hi = guest_block_count;
  while (lo < hi) {
    unsigned mid = (lo + hi) / 2;
    uint32_t k = guest_blocks[mid].key;
    if (k == key) return &guest_blocks[mid];
    if (k < key) lo = mid + 1; else hi = mid;
  }
  return NULL;
}

int guest_is_entry(uint32_t key) {
  if (key == GUEST_RETURN_MAGIC) return 0;   /* the host caller's return: leave, never call */
  if (key >= GUEST_TRAP_BASE) return 1;
  const GuestBlock *b = find_block(key);
  return b && b->entry;
}

/* Linux ARM kuser helpers (fixed addresses in the vector page). */
static int kuser(CPU *c, uint32_t pc) {
  switch (pc) {
  case 0xffff0fc0: { /* __kernel_cmpxchg(old r0, new r1, ptr r2): r0 = 0 and C set on success */
    uint32_t old = c->r[0];
    int ok = __atomic_compare_exchange_n((uint32_t *)G(c->r[2]), &old, c->r[1], 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    c->r[0] = ok ? 0 : 1; c->c = ok; c->z = ok; break;
  }
  case 0xffff0fa0: __atomic_thread_fence(__ATOMIC_SEQ_CST); break;   /* __kernel_dmb */
  case 0xffff0fe0: c->r[0] = rt_thread(c)->tls_area; break;            /* __kernel_get_tls */
  default: return 0;
  }
  c->pc = c->r[14];
  return 1;
}

void guest_call_key(CPU *c) {
  uint32_t pc = c->pc;
  if (pc >= GUEST_TRAP_BASE) {
    uint32_t i = (pc - GUEST_TRAP_BASE) / 4;
    c->pc = c->r[14];
    if (i < GUEST_IMPORT_COUNT) { guest_imports[i](c); return; }
    if (pc >= HOST_TRAP_BASE && pc < HOST_TRAP_BASE + 4 * HOST_TRAP_MAX) { host_trap_call(c, (pc - HOST_TRAP_BASE) / 4); return; }
    if (pc == GUEST_RETURN_MAGIC) { c->pc = pc; return; }
    c->pc = pc;
    if (kuser(c, pc)) return;
    rt_fatal(c, "jump to unknown trap %08x", pc);
  }
  const GuestBlock *b = find_block(pc);
  if (!b) {
    /* ARM state target given without its mode bit cleared, or a key we never translated */
    rt_fatal(c, "no translated code at %08x (lr %08x)", pc, c->r[14]);
  }
  b->fn(c);
}

void guest_undef(CPU *c, uint32_t pc, uint32_t op) {
  rt_fatal(c, "undefined/untranslated instruction %08x at %08x", op, pc);
}

void guest_swi(CPU *c, uint32_t n) { rt_fatal(c, "raw syscall %u", n); }

int guest_trace_imports;
void guest_trace_import(CPU *c, unsigned i) {
  const char *n = guest_import_names[i];
  if (!strncmp(n, "gl", 2) || !strncmp(n, "pthread_", 8) || !strcmp(n, "malloc") || !strcmp(n, "free") ||
      !strncmp(n, "mem", 3) || !strncmp(n, "str", 3) || !strcmp(n, "__errno")) return;
  int path = !strcmp(n, "open") || !strcmp(n, "fopen") || !strcmp(n, "stat") || !strcmp(n, "lstat") || !strcmp(n, "mkdir") ||
             !strcmp(n, "opendir") || !strcmp(n, "unlink") || !strcmp(n, "remove") || !strcmp(n, "rename");
  if (path) rt_log("import %s(\"%s\", %08x) lr=%08x", n, (char *)guest_mem + c->r[0], c->r[1], c->r[14]);
  else rt_log("import %s(%08x, %08x, %08x, %08x) lr=%08x", n, c->r[0], c->r[1], c->r[2], c->r[3], c->r[14]);
}

void guest_missing_import(CPU *c, const char *name) {
  rt_log("missing import called: %s (lr %08x) -> returns 0", name, c->r[14]);
  c->r[0] = 0;
}

/* ------------------------------------------------------------------ host traps */
static HostTrap host_traps[HOST_TRAP_MAX];
static int host_trap_args[HOST_TRAP_MAX];
static const char *host_trap_names[HOST_TRAP_MAX];
static unsigned host_trap_count;

uint32_t host_trap_add(const char *name, HostTrap fn, int arg) {
  if (host_trap_count >= HOST_TRAP_MAX) rt_fatal(NULL, "too many host traps");
  host_traps[host_trap_count] = fn;
  host_trap_args[host_trap_count] = arg;
  host_trap_names[host_trap_count] = name;
  return HOST_TRAP_BASE + 4 * host_trap_count++;
}

void host_trap_call(CPU *c, uint32_t i) {
  if (i >= host_trap_count) rt_fatal(c, "bad host trap %u", i);
  host_traps[i](c, host_trap_args[i]);
}

/* ------------------------------------------------------------------ threads */
RtThread *rt_thread(CPU *c) { return (RtThread *)c->thread; }
CPU *rt_cpu(void) { return tls_cpu; }
void rt_bind_thread(RtThread *t) { tls_cpu = &t->cpu; }

CPU *rt_cpu_for_thread(void) {
  if (tls_cpu) return tls_cpu;
  RtThread *t = calloc(1, sizeof *t);
  CPU *c = &t->cpu;
  c->mem = guest_mem;
  c->thread = t;
  t->stack_size = 0x100000;
  t->stack_base = guest_malloc(t->stack_size);
  t->tls_area = guest_calloc(256);
  c->r[13] = (t->stack_base + t->stack_size - 16) & ~7u;
  t->handle = rt_thread_register(t);
  tls_cpu = c;
  return c;
}

/* Call guest code at addr with up to 8 word arguments; returns r0 (r1 in *hi if given). */
uint32_t guest_call(CPU *c, uint32_t addr, int nargs, const uint32_t *args, uint32_t *hi) {
  uint32_t saved[16], sf[5];
  memcpy(saved, c->r, sizeof saved);
  sf[0] = c->n; sf[1] = c->z; sf[2] = c->c; sf[3] = c->v; sf[4] = c->pc;
  uint32_t sp = (c->r[13] - 64) & ~7u;
  int extra = nargs > 4 ? nargs - 4 : 0;
  sp = (sp - 4 * extra) & ~7u;
  for (int i = 0; i < nargs; i++) {
    if (i < 4) c->r[i] = args[i];
    else G32(sp + 4 * (i - 4)) = args[i];
  }
  c->r[13] = sp;
  c->r[14] = GUEST_RETURN_MAGIC;
  c->pc = addr;
  while (c->pc != GUEST_RETURN_MAGIC) guest_call_key(c);
  uint32_t r0 = c->r[0];
  if (hi) *hi = c->r[1];
  memcpy(c->r, saved, sizeof saved);
  c->n = sf[0]; c->z = sf[1]; c->c = sf[2]; c->v = sf[3]; c->pc = sf[4];
  return r0;
}

/* ------------------------------------------------------------------ crash report */
#include <dlfcn.h>
#include <signal.h>
#include <ucontext.h>

static void on_fault(int sig, siginfo_t *si, void *uc_) {
  CPU *c = tls_cpu;
  uintptr_t fa = (uintptr_t)si->si_addr, base = (uintptr_t)guest_mem;
  uintptr_t hpc = 0;
#if defined(__aarch64__)
  hpc = ((ucontext_t *)uc_)->uc_mcontext.pc;
#elif defined(__x86_64__)
  hpc = (uintptr_t)((ucontext_t *)uc_)->uc_mcontext.gregs[REG_RIP];
#endif
  Dl_info di = { 0 };
  dladdr((void *)hpc, &di);
  rt_log("signal %d at host pc %p (%s+0x%lx), fault address %p (guest %08lx)", sig, (void *)hpc,
         di.dli_sname ? di.dli_sname : "?", (unsigned long)(hpc - (uintptr_t)di.dli_saddr), (void *)fa,
         (unsigned long)(fa >= base && fa - base < 0x100000000ull ? fa - base : 0));
  if (c) rt_dump(c, "guest state at last call/spill");
  _exit(128 + sig);
}

static void install_fault_handler(void) {
  struct sigaction sa;
  memset(&sa, 0, sizeof sa);
  sa.sa_sigaction = on_fault;
  sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
  sigaction(SIGSEGV, &sa, NULL);
  sigaction(SIGBUS, &sa, NULL);
  sigaction(SIGILL, &sa, NULL);
}

/* ------------------------------------------------------------------ boot */
uint32_t guest_export(const char *name) {
  for (unsigned i = 0; i < guest_export_count; i++)
    if (!strcmp(guest_exports[i].name, name)) return guest_exports[i].addr;
  return 0;
}

int guest_boot(void) {
  if (guest_mem) return 0;
  /* 4 GB of guest address space plus a guard page; pages are only backed when touched. */
  size_t span = (size_t)1 << 32;
  void *m = mmap(NULL, span + 0x10000, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
  if (m == MAP_FAILED) { rt_log("guest memory reserve failed: %s", strerror(errno)); return -1; }
  guest_mem = m;
  /* Below the image stays PROT_NONE, so null-pointer use faults as it did on 32-bit Android. */
  if (mprotect(guest_mem + GUEST_LOAD_BASE, GUEST_HEAP_END - GUEST_LOAD_BASE, PROT_READ | PROT_WRITE)) {
    rt_log("guest memory commit failed: %s", strerror(errno));
    return -1;
  }
  memcpy(guest_mem + GUEST_LOAD_BASE, guest_image, GUEST_IMAGE_SIZE);
  heap_brk = GUEST_HEAP_BASE;
  if (getenv("KNS64_FAULTS")) install_fault_handler();
  guest_trace_imports = getenv("KNS64_TRACE") != NULL;
  libc_init();
  return 0;
}

void guest_run_init_array(CPU *c) {
  for (unsigned i = 0; i < GUEST_INIT_COUNT; i++) {
    rt_log("init_array[%u] %08x", i, guest_init_array[i]);
    guest_call(c, guest_init_array[i], 0, NULL, NULL);
  }
}
