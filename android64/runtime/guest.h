/* Guest CPU state and the macros the translated code (android64/tools/arm2c.py output) uses.
 *
 * Guest memory is one reserved 4 GB host region: guest address a is host address mem + a.
 * A block key is a guest address with bit 0 set in Thumb state. Translated functions keep
 * guest registers in locals (R[], N Z C V, PC) and spill them to the CPU struct around calls.
 */
#pragma once
#include <stdint.h>
#include <string.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CPU {
  uint32_t r[16];
  uint32_t n, z, c, v;      /* flags, 0 or 1 */
  uint32_t pc;              /* block key to run next / where control went */
  int depth;                /* nested translated-function frames on this host thread */
  uint8_t *mem;             /* guest memory base */
  void *thread;             /* runtime per-thread data */
} CPU;

typedef void (*GuestFn)(CPU *c);
typedef struct { uint32_t key; uint32_t entry; GuestFn fn; } GuestBlock;
typedef struct { const char *name; uint32_t addr; } GuestSym;
typedef GuestFn GuestImport;

extern const GuestBlock guest_blocks[];
extern const unsigned guest_block_count;
extern const GuestImport guest_imports[];
extern const char *const guest_import_names[];
extern const uint32_t guest_init_array[];
extern const GuestSym guest_exports[];
extern const unsigned guest_export_count;

/* runtime (guest_rt.c) */
void guest_call_key(CPU *c);                 /* run c->pc as a call: returns when it leaves */
int guest_is_entry(uint32_t key);
void guest_undef(CPU *c, uint32_t pc, uint32_t op) __attribute__((noreturn));
void guest_swi(CPU *c, uint32_t n);
void guest_missing_import(CPU *c, const char *name);
extern int guest_trace_imports;              /* KNS64_TRACE=1: log every direct import call */
void guest_trace_import(CPU *c, unsigned i);

#ifndef GUEST_MAX_DEPTH
#define GUEST_MAX_DEPTH 6000
#endif

/* ---- memory ---- */
static inline uint32_t g_ld32(const uint8_t *m, uint32_t a) { uint32_t v; memcpy(&v, m + a, 4); return v; }
static inline uint32_t g_ld16(const uint8_t *m, uint32_t a) { uint16_t v; memcpy(&v, m + a, 2); return v; }
static inline void g_st32(uint8_t *m, uint32_t a, uint32_t v) { memcpy(m + a, &v, 4); }
static inline void g_st16(uint8_t *m, uint32_t a, uint32_t v) { uint16_t h = (uint16_t)v; memcpy(m + a, &h, 2); }
#define LD32(a) g_ld32(mem, (uint32_t)(a))
#define LD16(a) g_ld16(mem, (uint32_t)(a))
#define LD8(a) ((uint32_t)mem[(uint32_t)(a)])
#define ST32(a, v) g_st32(mem, (uint32_t)(a), (v))
#define ST16(a, v) g_st16(mem, (uint32_t)(a), (v))
#define ST8(a, v) (mem[(uint32_t)(a)] = (uint8_t)(v))
#define ATOMIC_XCHG32(a, v) __atomic_exchange_n((uint32_t *)(mem + (uint32_t)(a)), (v), __ATOMIC_SEQ_CST)
#define ATOMIC_XCHG8(a, v) ((uint32_t)__atomic_exchange_n(mem + (uint32_t)(a), (uint8_t)(v), __ATOMIC_SEQ_CST))

/* ---- ALU helpers ---- */
static inline uint32_t ADDC(uint32_t a, uint32_t b, uint32_t cin, uint32_t *co, uint32_t *vo) {
  uint64_t s = (uint64_t)a + b + cin;
  uint32_t t = (uint32_t)s;
  *co = (uint32_t)(s >> 32);
  *vo = ((a ^ t) & (b ^ t)) >> 31;
  return t;
}
static inline uint32_t CLZ(uint32_t x) { return x ? (uint32_t)__builtin_clz(x) : 32; }
static inline int32_t SAT64(int64_t x) { return x > INT32_MAX ? INT32_MAX : x < INT32_MIN ? INT32_MIN : (int32_t)x; }
/* register-specified shifts, n = count & 255 */
static inline uint32_t LSLR(uint32_t v, uint32_t n) { return n < 32 ? v << n : 0; }
static inline uint32_t LSRR(uint32_t v, uint32_t n) { return n < 32 ? v >> n : 0; }
static inline uint32_t ASRR(uint32_t v, uint32_t n) { return (uint32_t)((int32_t)v >> (n < 32 ? n : 31)); }
static inline uint32_t RORR(uint32_t v, uint32_t n) { uint32_t m = n & 31; return m ? (v >> m) | (v << (32 - m)) : v; }
static inline uint32_t LSLRC(uint32_t v, uint32_t n, uint32_t c) { return n == 0 ? c : n < 32 ? (v >> (32 - n)) & 1 : n == 32 ? v & 1 : 0; }
static inline uint32_t LSRRC(uint32_t v, uint32_t n, uint32_t c) { return n == 0 ? c : n < 32 ? (v >> (n - 1)) & 1 : n == 32 ? v >> 31 : 0; }
static inline uint32_t ASRRC(uint32_t v, uint32_t n, uint32_t c) { return n == 0 ? c : n < 32 ? (v >> (n - 1)) & 1 : v >> 31; }
static inline uint32_t RORRC(uint32_t v, uint32_t n, uint32_t c) { uint32_t m = n & 31; return n == 0 ? c : m ? (v >> (m - 1)) & 1 : v >> 31; }

/* ---- function frame ---- */
#define RELOAD do { memcpy(R, c->r, sizeof R); N = c->n; Z = c->z; C = c->c; V = c->v; } while (0)
#define SPILL do { memcpy(c->r, R, sizeof R); c->n = N; c->z = Z; c->c = C; c->v = V; } while (0)

#define FN_PROLOGUE \
  uint8_t *const mem = c->mem; uint32_t R[16]; uint32_t N, Z, C, V, PC, X = 0, t = 0, a = 0, b = 0, v = 0, n = 0, o = 0, h = 0, x2 = 0; \
  uint64_t w = 0; (void)mem; (void)X; (void)t; (void)a; (void)b; (void)v; (void)n; (void)o; (void)h; (void)x2; (void)w; \
  RELOAD; PC = c->pc; c->depth++;

/* PC is not a block of this function: a tail call into a function entry, else return to the
 * C caller, which checks it against its own return address. */
#define FN_EPILOGUE \
  other: \
  if (guest_is_entry(PC) && c->depth < GUEST_MAX_DEPTH) { SPILL; c->pc = PC; guest_call_key(c); RELOAD; PC = c->pc; goto disp; } \
  leave: SPILL; c->pc = PC; c->depth--; return;

#define TAILCALL(fn, key) do { SPILL; c->pc = (key); fn(c); RELOAD; PC = c->pc; goto disp; } while (0)
#define CALL(fn, key, ret, CONT) do { R[14] = (ret); SPILL; c->pc = (key); fn(c); RELOAD; PC = c->pc; \
  if (PC == (ret)) { CONT } else goto disp; } while (0)
#define CALLX(key, ret, CONT) do { R[14] = (ret); SPILL; c->pc = (key); guest_call_key(c); RELOAD; PC = c->pc; \
  if (PC == (ret)) { CONT } else goto disp; } while (0)
#define CALLIMP(i, ret, CONT) do { R[14] = (ret); SPILL; c->pc = (ret); if (guest_trace_imports) guest_trace_import(c, i); guest_imports[i](c); RELOAD; PC = c->pc; \
  if (PC == (ret)) { CONT } else goto disp; } while (0)

#ifdef __cplusplus
}
#endif
