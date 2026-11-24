/* JNI bridge: the jni_guest.c backend on the device's real JNIEnv, for the APK build.
 *
 * Guest code sees 32-bit JNI: references, method and field IDs are u32 handles. This file maps
 * them to host jobject / jmethodID / jfieldID, copies strings and arrays between Java and guest
 * memory, converts Call*Method / Call*MethodV / Call*MethodA arguments by method signature
 * (guest AAPCS soft-float varargs -> host jvalue[]), and runs guest code on a large stack.
 *
 * Handles: local  = idx << 2 | 1 (per-thread table, truncated when the native frame returns)
 *          global = idx << 2 | 2, weak = idx << 2 | 3 (process-wide chunked tables)
 *          method / field IDs = index + 1 into chunked tables that keep the signature / type.
 *
 * Memory Java shares with native code by pointer:
 *  - Get<T>ArrayElements / GetPrimitiveArrayCritical: a guest copy per array ("pin"), one per
 *    array however often it is pinned, refreshed on every Get. Pins up to PIN_SYNC_MAX bytes are
 *    also synced around every Java call and native frame (the game keeps some pinned forever).
 *    Bigger pins (the 2048x2048 font buffer) are refreshed by kns64.Guest.getPixels, which the
 *    APK build substitutes for Bitmap.getPixels in CFont (staged smali only).
 *  - Direct ByteBuffers: kns64.Guest.allocateDirect makes them inside guest memory (the APK build
 *    substitutes it in CEventHandler), so GetDirectBufferAddress is exact. Any other direct buffer
 *    gets a guest mirror refreshed Java -> guest at native entry and after Java calls.
 *
 * Stacks: Java_* entry switches to a 64 MB per-thread stack (stack_switch.S) for guest code;
 * every JNI call from the guest switches back to the thread's own stack, where ART expects to run.
 */
#include "rt.h"

#include <jni.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#ifdef __ANDROID__
#include <sys/system_properties.h>
#endif

#define BIG_STACK (64u << 20)
#define PIN_SYNC_MAX 0x10000u
#define CHUNK 16384u
#define CHUNKS 64u

extern void kns64_run_on_stack(void *top, void (*fn)(void *), void *arg, uintptr_t *old_sp);

static JavaVM *host_vm;
static int trace_jni;
static uint32_t guest_vm;

/* ------------------------------------------------------------------ per-thread state */
typedef struct TState {
  JNIEnv *env;
  jobject *loc;              /* local handle table; slot 0 unused */
  uint32_t ltop, lcap;
  uint32_t frames[64];       /* PushLocalFrame marks */
  int nframes;
  int attached_by_us;
  uint8_t *stk;              /* big stack (mmap) */
  uintptr_t big_sp, java_sp;
  int on_big;
} TState;

static __thread TState *tls_state;
static pthread_key_t ts_key;

static void ts_dtor(void *p) {
  TState *t = p;
  if (t->attached_by_us && host_vm) (*host_vm)->DetachCurrentThread(host_vm);
  if (t->stk) munmap(t->stk, BIG_STACK);
  free(t->loc);
  free(t);
}

static TState *ts(void) {
  TState *t = tls_state;
  if (!t) {
    t = calloc(1, sizeof *t);
    t->ltop = 1;
    tls_state = t;
    pthread_setspecific(ts_key, t);
  }
  return t;
}

static JNIEnv *henv(TState *t) {
  if (t->env) return t->env;
  JNIEnv *e = NULL;
  if ((*host_vm)->GetEnv(host_vm, (void **)&e, JNI_VERSION_1_6) != JNI_OK) {
    if ((*host_vm)->AttachCurrentThread(host_vm, &e, NULL) != JNI_OK) rt_fatal(NULL, "JNI: cannot attach thread");
    t->attached_by_us = 1;
    rt_log("JNI: attached a guest thread that used JNIEnv without AttachCurrentThread");
  }
  t->env = e;
  return e;
}

/* ------------------------------------------------------------------ stacks */
static void on_big_stack(void (*fn)(void *), void *arg) {
  TState *t = ts();
  if (t->on_big) { fn(arg); return; }
  if (!t->stk) {
    void *m = mmap(NULL, BIG_STACK, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    if (m == MAP_FAILED) rt_fatal(NULL, "big stack mmap failed");
    mprotect(m, 0x10000, PROT_NONE);   /* guard */
    t->stk = m;
    t->big_sp = (uintptr_t)m + BIG_STACK;
  }
  uintptr_t old_java = t->java_sp;
  t->on_big = 1;
  kns64_run_on_stack((void *)((t->big_sp - 256) & ~(uintptr_t)15), fn, arg, &t->java_sp);
  t->on_big = 0;
  t->java_sp = old_java;
}

static void on_java_stack(void (*fn)(void *), void *arg) {
  TState *t = ts();
  if (!t->on_big) { fn(arg); return; }
  uintptr_t old_big = t->big_sp;
  t->on_big = 0;
  kns64_run_on_stack((void *)((t->java_sp - 256) & ~(uintptr_t)15), fn, arg, &t->big_sp);
  t->on_big = 1;
  t->big_sp = old_big;
}

/* ------------------------------------------------------------------ reference tables */
typedef struct { jobject o; uint32_t next; } GEnt;
static GEnt *gtab[CHUNKS];
static uint32_t gnext = 1, gfree;
static pthread_mutex_t gref_lock = PTHREAD_MUTEX_INITIALIZER;

static uint32_t global_put(jobject o, int kind) {
  if (!o) return 0;
  pthread_mutex_lock(&gref_lock);
  uint32_t i = gfree;
  if (i) gfree = gtab[i / CHUNK][i % CHUNK].next;
  else {
    i = gnext++;
    if (i >= CHUNK * CHUNKS) rt_fatal(NULL, "JNI: global reference table full");
    if (!gtab[i / CHUNK]) gtab[i / CHUNK] = calloc(CHUNK, sizeof(GEnt));
  }
  gtab[i / CHUNK][i % CHUNK].o = o;
  pthread_mutex_unlock(&gref_lock);
  return i << 2 | (uint32_t)kind;
}

static void global_drop(uint32_t h) {
  uint32_t i = h >> 2;
  pthread_mutex_lock(&gref_lock);
  if (i && i < gnext) { gtab[i / CHUNK][i % CHUNK].o = NULL; gtab[i / CHUNK][i % CHUNK].next = gfree; gfree = i; }
  pthread_mutex_unlock(&gref_lock);
}

static uint32_t local_put(TState *t, jobject o) {
  if (!o) return 0;
  if (t->ltop >= t->lcap) {
    t->lcap = t->lcap ? t->lcap * 2 : 1024;
    t->loc = realloc(t->loc, t->lcap * sizeof(jobject));
  }
  t->loc[t->ltop] = o;
  return t->ltop++ << 2 | 1;
}

static jobject deref(uint32_t h) {
  if (!h) return NULL;
  uint32_t i = h >> 2;
  if ((h & 3) == 1) {
    TState *t = ts();
    if (i < t->ltop) return t->loc[i];
  } else if ((h & 3) && i < gnext) {
    return gtab[i / CHUNK][i % CHUNK].o;
  }
  rt_log("JNI: stale or invalid reference %08x", h);
  return NULL;
}

/* ------------------------------------------------------------------ method and field IDs */
typedef struct { void *id; char ret; uint8_t n; char args[62]; char name[32]; } IdEnt;
typedef struct { IdEnt *tab[CHUNKS]; uint32_t count; uint32_t hash[1 << 16]; } IdTable;
static IdTable methods, fields;
static pthread_mutex_t id_lock = PTHREAD_MUTEX_INITIALIZER;

/* "(ILjava/lang/String;[B)V" -> args "ILL", ret 'V' (arrays and objects are both 'L') */
static void parse_sig(const char *sig, IdEnt *e) {
  const char *p = sig;
  e->n = 0;
  if (*p == '(') {
    p++;
    while (*p && *p != ')') {
      char t = *p;
      while (*p == '[') p++;
      if (*p == 'L') { p = strchr(p, ';'); if (!p) break; }
      p++;
      if (t == '[') t = 'L';
      if (e->n < sizeof e->args) e->args[e->n++] = t;
    }
    if (*p == ')') p++;
  }
  e->ret = *p == '[' ? 'L' : *p;
}

static uint32_t id_put(IdTable *tb, void *id, const char *name, const char *sig) {
  if (!id) return 0;
  uint32_t h = (uint32_t)(((uintptr_t)id >> 3) * 2654435761u) >> 16;
  pthread_mutex_lock(&id_lock);
  for (uint32_t k = 0; k < (1u << 16); k++) {
    uint32_t s = (h + k) & 0xFFFF, i = tb->hash[s];
    if (!i) {
      i = ++tb->count;
      if (i >= CHUNK * CHUNKS) rt_fatal(NULL, "JNI: ID table full");
      if (!tb->tab[i / CHUNK]) tb->tab[i / CHUNK] = calloc(CHUNK, sizeof(IdEnt));
      IdEnt *e = &tb->tab[i / CHUNK][i % CHUNK];
      e->id = id;
      parse_sig(sig, e);
      snprintf(e->name, sizeof e->name, "%s", name);
      tb->hash[s] = i;
      pthread_mutex_unlock(&id_lock);
      return i;
    }
    if (tb->tab[i / CHUNK][i % CHUNK].id == id) { pthread_mutex_unlock(&id_lock); return i; }
  }
  pthread_mutex_unlock(&id_lock);
  rt_fatal(NULL, "JNI: ID hash full");
}

static IdEnt *id_get(IdTable *tb, uint32_t i) {
  if (!i || i > tb->count) { rt_log("JNI: invalid method/field ID %u", i); return NULL; }
  return &tb->tab[i / CHUNK][i % CHUNK];
}

/* ------------------------------------------------------------------ pins and buffer mirrors */
typedef struct { jobject gref; uint32_t gaddr, bytes, len; int refs; char type; int mirror; void *host; } Pin;
static Pin pins[256];
static int npins;
static pthread_mutex_t pin_lock = PTHREAD_MUTEX_INITIALIZER;
static int slot_get_region[128], slot_set_region[128];   /* by type char */

static uint32_t esize(char t) { return t == 'Z' || t == 'B' ? 1 : t == 'C' || t == 'S' ? 2 : t == 'J' || t == 'D' ? 8 : 4; }

typedef void (*RegionFn)(JNIEnv *, jarray, jsize, jsize, void *);
static void region(JNIEnv *env, int set, char t, jarray a, uint32_t start, uint32_t n, void *buf) {
  void **ft = (void **)*env;
  ((RegionFn)ft[set ? slot_set_region[(int)t] : slot_get_region[(int)t]])(env, a, (jsize)start, (jsize)n, buf);
}

static void pin_to_guest(JNIEnv *env, Pin *p) {
  if (p->mirror) memcpy(G(p->gaddr), p->host, p->bytes);
  else region(env, 0, p->type, p->gref, 0, p->len, G(p->gaddr));
}

/* Java -> guest for small pins and all mirrors (native entry, after Java calls) */
static void sync_to_guest(JNIEnv *env) {
  if (!npins) return;
  pthread_mutex_lock(&pin_lock);
  for (int i = 0; i < npins; i++)
    if (pins[i].gref && pins[i].bytes <= PIN_SYNC_MAX) pin_to_guest(env, &pins[i]);
  pthread_mutex_unlock(&pin_lock);
}

/* guest -> Java for small array pins (before Java calls, native exit) */
static void sync_to_host(JNIEnv *env) {
  if (!npins) return;
  pthread_mutex_lock(&pin_lock);
  for (int i = 0; i < npins; i++)
    if (pins[i].gref && !pins[i].mirror && pins[i].bytes <= PIN_SYNC_MAX)
      region(env, 1, pins[i].type, pins[i].gref, 0, pins[i].len, G(pins[i].gaddr));
  pthread_mutex_unlock(&pin_lock);
}

static Pin *pin_find_obj(JNIEnv *env, jobject a) {
  for (int i = 0; i < npins; i++)
    if (pins[i].gref && (*env)->IsSameObject(env, pins[i].gref, a)) return &pins[i];
  return NULL;
}

static Pin *pin_new(void) {
  for (int i = 0; i < npins; i++) if (!pins[i].gref) return &pins[i];
  if (npins >= (int)(sizeof pins / sizeof *pins)) rt_fatal(NULL, "JNI: too many pinned arrays");
  return &pins[npins++];
}

static uint32_t pin_get(JNIEnv *env, char type, jarray a, uint32_t is_copy) {
  if (!a) return 0;
  pthread_mutex_lock(&pin_lock);
  Pin *p = pin_find_obj(env, a);
  if (!p) {
    p = pin_new();
    memset(p, 0, sizeof *p);
    p->type = type;
    p->len = (uint32_t)(*env)->GetArrayLength(env, a);
    p->bytes = p->len * esize(type);
    p->gaddr = guest_malloc(p->bytes ? p->bytes : 16);
    p->gref = (*env)->NewGlobalRef(env, a);
    if (trace_jni) rt_log("JNI: pin %c[%u] at guest %08x", type, p->len, p->gaddr);
  }
  p->refs++;
  pin_to_guest(env, p);
  uint32_t g = p->gaddr;
  pthread_mutex_unlock(&pin_lock);
  if (is_copy) *G(is_copy) = JNI_TRUE;
  return g;
}

static void pin_release(JNIEnv *env, uint32_t g, int mode) {
  pthread_mutex_lock(&pin_lock);
  for (int i = 0; i < npins; i++) {
    Pin *p = &pins[i];
    if (!p->gref || p->mirror || p->gaddr != g) continue;
    if (mode == 0 || mode == JNI_COMMIT) region(env, 1, p->type, p->gref, 0, p->len, G(p->gaddr));
    if (mode != JNI_COMMIT && --p->refs <= 0) {
      (*env)->DeleteGlobalRef(env, p->gref);
      guest_free(p->gaddr);
      p->gref = NULL;
    }
    break;
  }
  pthread_mutex_unlock(&pin_lock);
}

static char array_type(JNIEnv *env, jobject a) {
  static jclass cls[8];
  static const char names[] = "ZBCSIJFD";
  for (int i = 0; i < 8; i++) {
    if (!cls[i]) {
      char n[3] = { '[', names[i], 0 };
      jclass c = (*env)->FindClass(env, n);
      cls[i] = (*env)->NewGlobalRef(env, c);
      (*env)->DeleteLocalRef(env, c);
    }
    if ((*env)->IsInstanceOf(env, a, cls[i])) return names[i];
  }
  return 'B';
}

static uint32_t direct_address(JNIEnv *env, jobject buf) {
  uint8_t *p = (*env)->GetDirectBufferAddress(env, buf);
  if (!p) return 0;
  if (p >= guest_mem && p < guest_mem + 0x100000000ull) return (uint32_t)(p - guest_mem);
  pthread_mutex_lock(&pin_lock);
  for (int i = 0; i < npins; i++)
    if (pins[i].gref && pins[i].mirror && pins[i].host == p) { uint32_t g = pins[i].gaddr; pthread_mutex_unlock(&pin_lock); return g; }
  Pin *m = pin_new();
  memset(m, 0, sizeof *m);
  m->mirror = 1;
  m->host = p;
  m->bytes = m->len = (uint32_t)(*env)->GetDirectBufferCapacity(env, buf);
  m->gaddr = guest_malloc(m->bytes ? m->bytes : 16);
  m->gref = (*env)->NewGlobalRef(env, buf);
  memcpy(G(m->gaddr), p, m->bytes);
  pthread_mutex_unlock(&pin_lock);
  rt_log("JNI: direct buffer %p outside guest memory, mirrored at %08x (%u bytes, Java -> guest only)", p, m->gaddr, m->bytes);
  return m->gaddr;
}

/* ------------------------------------------------------------------ slot metadata */
enum { K_OTHER, K_CALL, K_NEWOBJ, K_GETF, K_SETF, K_NEWARR, K_GETELEMS, K_RELELEMS, K_GETREG, K_SETREG };
typedef struct { uint8_t kind, flavor; char type, variant; int aslot; } Meta;
static Meta meta[256];

static char type_code(const char *s, const char **end) {
  static const struct { const char *n; char c; } T[] = {
    { "Object", 'L' }, { "Boolean", 'Z' }, { "Byte", 'B' }, { "Char", 'C' }, { "Short", 'S' },
    { "Int", 'I' }, { "Long", 'J' }, { "Float", 'F' }, { "Double", 'D' }, { "Void", 'V' } };
  for (unsigned i = 0; i < sizeof T / sizeof *T; i++) {
    size_t n = strlen(T[i].n);
    if (!strncmp(s, T[i].n, n)) { *end = s + n; return T[i].c; }
  }
  *end = s;
  return 0;
}

static int slot_of(const char *name) {
  for (int i = 0; i < jni_env_count; i++) if (!strcmp(jni_env_names[i], name)) return i;
  rt_fatal(NULL, "JNI slot %s not found", name);
}

static void meta_init(void) {
  for (int s = 0; s < jni_env_count; s++) {
    const char *n = jni_env_names[s], *e;
    Meta *m = &meta[s];
    char t;
    if (!strncmp(n, "Call", 4)) {
      const char *r = n + 4;
      if (!strncmp(r, "Nonvirtual", 10)) { m->flavor = 1; r += 10; }
      else if (!strncmp(r, "Static", 6)) { m->flavor = 2; r += 6; }
      t = type_code(r, &e);
      if (t && !strncmp(e, "Method", 6)) {
        m->kind = K_CALL; m->type = t; m->variant = e[6];
        m->aslot = s + (m->variant == 'A' ? 0 : m->variant == 'V' ? 1 : 2);
      }
    } else if (!strcmp(n, "NewObject") || !strcmp(n, "NewObjectV") || !strcmp(n, "NewObjectA")) {
      m->kind = K_NEWOBJ; m->type = 'L'; m->variant = n[9]; m->aslot = slot_of("NewObjectA");
    } else if ((!strncmp(n, "Get", 3) || !strncmp(n, "Set", 3)) && strlen(n) > 8 && !strcmp(n + strlen(n) - 5, "Field")) {
      const char *r = n + 3;
      if (!strncmp(r, "Static", 6)) { m->flavor = 2; r += 6; }
      t = type_code(r, &e);
      if (t && !strcmp(e, "Field")) { m->kind = n[0] == 'G' ? K_GETF : K_SETF; m->type = t; }
    } else if (!strncmp(n, "New", 3) && (t = type_code(n + 3, &e)) && t != 'L' && !strcmp(e, "Array")) {
      m->kind = K_NEWARR; m->type = t;
    } else if ((!strncmp(n, "Get", 3) || !strncmp(n, "Set", 3) || !strncmp(n, "Release", 7))) {
      const char *r = n + (n[0] == 'R' ? 7 : 3);
      t = type_code(r, &e);
      if (t && t != 'L' && t != 'V') {
        if (!strcmp(e, "ArrayElements")) m->kind = n[0] == 'R' ? K_RELELEMS : K_GETELEMS;
        else if (!strcmp(e, "ArrayRegion")) {
          m->kind = n[0] == 'G' ? K_GETREG : K_SETREG;
          (n[0] == 'G' ? slot_get_region : slot_set_region)[(int)t] = s;
        }
        m->type = t;
      }
    }
  }
}

/* ------------------------------------------------------------------ argument conversion */
typedef struct { CPU *c; int mode; int idx; uint32_t p; } GVa;   /* mode 0: AAPCS words; 1: va_list */
static uint32_t va_w(GVa *v) {
  if (!v->mode) return ARG(v->c, v->idx++);
  uint32_t x = G32(v->p); v->p += 4; return x;
}
static uint64_t va_d(GVa *v) {
  uint32_t lo, hi;
  if (!v->mode) { if (v->idx & 1) v->idx++; lo = ARG(v->c, v->idx); hi = ARG(v->c, v->idx + 1); v->idx += 2; }
  else { v->p = (v->p + 7) & ~7u; lo = G32(v->p); hi = G32(v->p + 4); v->p += 8; }
  return (uint64_t)lo | (uint64_t)hi << 32;
}

/* Guest arguments of a Call/NewObject (first = word index of the first Java argument) -> jvalue[] */
static int convert_args(CPU *c, const Meta *m, const IdEnt *e, int first, jvalue *jv) {
  if (m->variant == 'A') {
    uint32_t p = ARG(c, first);
    for (int i = 0; i < e->n; i++, p += 8) {
      switch (e->args[i]) {
      case 'Z': jv[i].z = *G(p); break;
      case 'B': jv[i].b = (jbyte)*G(p); break;
      case 'C': jv[i].c = *(uint16_t *)G(p); break;
      case 'S': jv[i].s = *(int16_t *)G(p); break;
      case 'I': jv[i].i = (jint)G32(p); break;
      case 'F': memcpy(&jv[i].f, G(p), 4); break;
      case 'J': case 'D': memcpy(&jv[i].j, G(p), 8); break;
      default: jv[i].l = deref(G32(p)); break;
      }
    }
    return e->n;
  }
  GVa v = { c, m->variant == 'V', first, 0 };
  if (v.mode) v.p = ARG(c, first);
  for (int i = 0; i < e->n; i++) {
    switch (e->args[i]) {
    case 'Z': jv[i].z = (jboolean)va_w(&v); break;
    case 'B': jv[i].b = (jbyte)va_w(&v); break;
    case 'C': jv[i].c = (jchar)va_w(&v); break;
    case 'S': jv[i].s = (jshort)va_w(&v); break;
    case 'I': jv[i].i = (jint)va_w(&v); break;
    case 'F': { uint64_t u = va_d(&v); double d; memcpy(&d, &u, 8); jv[i].f = (float)d; break; }   /* promoted */
    case 'J': case 'D': { uint64_t u = va_d(&v); memcpy(&jv[i].j, &u, 8); break; }
    default: jv[i].l = deref(va_w(&v)); break;
    }
  }
  return e->n;
}

static void ret_value(CPU *c, TState *t, char type, jvalue r) {
  uint64_t u = 0;
  switch (type) {
  case 'Z': c->r[0] = r.z; break;
  case 'B': c->r[0] = (uint32_t)(int32_t)r.b; break;
  case 'C': c->r[0] = r.c; break;
  case 'S': c->r[0] = (uint32_t)(int32_t)r.s; break;
  case 'I': c->r[0] = (uint32_t)r.i; break;
  case 'F': memcpy(&c->r[0], &r.f, 4); break;                                       /* soft-float */
  case 'J': case 'D': memcpy(&u, &r.j, 8); c->r[0] = (uint32_t)u; c->r[1] = (uint32_t)(u >> 32); break;
  case 'L': c->r[0] = local_put(t, r.l); break;
  default: c->r[0] = 0; break;
  }
}

typedef jobject (*CallL3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jboolean (*CallZ3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jbyte (*CallB3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jchar (*CallC3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jshort (*CallS3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jint (*CallI3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jlong (*CallJ3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jfloat (*CallF3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jdouble (*CallD3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef void (*CallV3)(JNIEnv *, jobject, jmethodID, const jvalue *);
typedef jobject (*CallL4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jboolean (*CallZ4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jbyte (*CallB4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jchar (*CallC4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jshort (*CallS4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jint (*CallI4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jlong (*CallJ4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jfloat (*CallF4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef jdouble (*CallD4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);
typedef void (*CallV4)(JNIEnv *, jobject, jclass, jmethodID, const jvalue *);

static void do_call(CPU *c, TState *t, JNIEnv *env, int slot) {
  const Meta *m = &meta[slot];
  void **ft = (void **)*env;
  void *fn = ft[m->aslot];
  int nonvirt = m->kind == K_CALL && m->flavor == 1;
  jobject obj = deref(ARG(c, 1));
  jclass cls = nonvirt ? deref(ARG(c, 2)) : NULL;
  uint32_t mid = ARG(c, nonvirt ? 3 : 2);
  IdEnt *e = id_get(&methods, mid);
  if (!e || !obj) { rt_log("JNI %s: bad %s", jni_env_names[slot], e ? "object" : "method"); c->r[0] = c->r[1] = 0; return; }
  jvalue jv[64], r;
  convert_args(c, m, e, nonvirt ? 4 : 3, jv);
  jmethodID id = e->id;
  memset(&r, 0, sizeof r);
  if (trace_jni) rt_log("JNI %s %s (%u args)", jni_env_names[slot], e->name, e->n);
  sync_to_host(env);
  if (!nonvirt) {
    switch (m->type) {
    case 'L': r.l = ((CallL3)fn)(env, obj, id, jv); break;
    case 'Z': r.z = ((CallZ3)fn)(env, obj, id, jv); break;
    case 'B': r.b = ((CallB3)fn)(env, obj, id, jv); break;
    case 'C': r.c = ((CallC3)fn)(env, obj, id, jv); break;
    case 'S': r.s = ((CallS3)fn)(env, obj, id, jv); break;
    case 'I': r.i = ((CallI3)fn)(env, obj, id, jv); break;
    case 'J': r.j = ((CallJ3)fn)(env, obj, id, jv); break;
    case 'F': r.f = ((CallF3)fn)(env, obj, id, jv); break;
    case 'D': r.d = ((CallD3)fn)(env, obj, id, jv); break;
    default: ((CallV3)fn)(env, obj, id, jv); break;
    }
  } else {
    switch (m->type) {
    case 'L': r.l = ((CallL4)fn)(env, obj, cls, id, jv); break;
    case 'Z': r.z = ((CallZ4)fn)(env, obj, cls, id, jv); break;
    case 'B': r.b = ((CallB4)fn)(env, obj, cls, id, jv); break;
    case 'C': r.c = ((CallC4)fn)(env, obj, cls, id, jv); break;
    case 'S': r.s = ((CallS4)fn)(env, obj, cls, id, jv); break;
    case 'I': r.i = ((CallI4)fn)(env, obj, cls, id, jv); break;
    case 'J': r.j = ((CallJ4)fn)(env, obj, cls, id, jv); break;
    case 'F': r.f = ((CallF4)fn)(env, obj, cls, id, jv); break;
    case 'D': r.d = ((CallD4)fn)(env, obj, cls, id, jv); break;
    default: ((CallV4)fn)(env, obj, cls, id, jv); break;
    }
  }
  sync_to_guest(env);
  ret_value(c, t, m->type, r);
}

/* Get<T>Field / GetStatic<T>Field (env, obj|cls, fid); Set (env, obj|cls, fid, value) */
static void do_field(CPU *c, TState *t, JNIEnv *env, int slot) {
  const Meta *m = &meta[slot];
  void *fn = ((void **)*env)[slot];
  jobject o = deref(ARG(c, 1));
  IdEnt *e = id_get(&fields, ARG(c, 2));
  if (!e || !o) { c->r[0] = c->r[1] = 0; return; }
  jfieldID f = e->id;
  jvalue r;
  memset(&r, 0, sizeof r);
  if (m->kind == K_GETF) {
    switch (m->type) {
    case 'L': r.l = ((jobject (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'Z': r.z = ((jboolean (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'B': r.b = ((jbyte (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'C': r.c = ((jchar (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'S': r.s = ((jshort (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'I': r.i = ((jint (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'J': r.j = ((jlong (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'F': r.f = ((jfloat (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    case 'D': r.d = ((jdouble (*)(JNIEnv *, jobject, jfieldID))fn)(env, o, f); break;
    }
    ret_value(c, t, m->type, r);
    return;
  }
  uint32_t w = ARG(c, 3);
  uint64_t u = (uint64_t)ARG(c, 4) | (uint64_t)ARG(c, 5) << 32;   /* 64-bit value: even pair -> stack words 4,5 */
  switch (m->type) {
  case 'L': ((void (*)(JNIEnv *, jobject, jfieldID, jobject))fn)(env, o, f, deref(w)); break;
  case 'Z': ((void (*)(JNIEnv *, jobject, jfieldID, jboolean))fn)(env, o, f, (jboolean)w); break;
  case 'B': ((void (*)(JNIEnv *, jobject, jfieldID, jbyte))fn)(env, o, f, (jbyte)w); break;
  case 'C': ((void (*)(JNIEnv *, jobject, jfieldID, jchar))fn)(env, o, f, (jchar)w); break;
  case 'S': ((void (*)(JNIEnv *, jobject, jfieldID, jshort))fn)(env, o, f, (jshort)w); break;
  case 'I': ((void (*)(JNIEnv *, jobject, jfieldID, jint))fn)(env, o, f, (jint)w); break;
  case 'F': { float x; memcpy(&x, &w, 4); ((void (*)(JNIEnv *, jobject, jfieldID, jfloat))fn)(env, o, f, x); break; }
  case 'J': ((void (*)(JNIEnv *, jobject, jfieldID, jlong))fn)(env, o, f, (jlong)u); break;
  case 'D': { double x; memcpy(&x, &u, 8); ((void (*)(JNIEnv *, jobject, jfieldID, jdouble))fn)(env, o, f, x); break; }
  }
  c->r[0] = 0;
}

/* ------------------------------------------------------------------ JNIEnv backend */
#define A1 ARG(c, 1)
#define A2 ARG(c, 2)
#define A3 ARG(c, 3)
#define A4 ARG(c, 4)
#define RET(v) (c->r[0] = (uint32_t)(v))
#define IS(n) (!strcmp(name, n))

static void env_other(CPU *c, TState *t, JNIEnv *env, int slot) {
  const char *name = jni_env_names[slot];
  JNIEnv *E = env;
  switch (name[0]) {
  case 'G':
    if (IS("GetVersion")) { RET((*E)->GetVersion(E)); return; }
    if (IS("GetMethodID") || IS("GetStaticMethodID")) {
      jclass k = deref(A1);
      jmethodID id = IS("GetMethodID") ? (*E)->GetMethodID(E, k, GS(A2), GS(A3)) : (*E)->GetStaticMethodID(E, k, GS(A2), GS(A3));
      if (trace_jni || !id) rt_log("JNI %s(%s %s) -> %p", name, GS(A2), GS(A3), (void *)id);
      RET(id_put(&methods, id, GS(A2), GS(A3)));
      return;
    }
    if (IS("GetFieldID") || IS("GetStaticFieldID")) {
      jclass k = deref(A1);
      jfieldID id = IS("GetFieldID") ? (*E)->GetFieldID(E, k, GS(A2), GS(A3)) : (*E)->GetStaticFieldID(E, k, GS(A2), GS(A3));
      if (trace_jni || !id) rt_log("JNI %s(%s %s) -> %p", name, GS(A2), GS(A3), (void *)id);
      RET(id_put(&fields, id, GS(A2), GS(A3)));
      return;
    }
    if (IS("GetObjectClass")) { RET(local_put(t, (*E)->GetObjectClass(E, deref(A1)))); return; }
    if (IS("GetSuperclass")) { RET(local_put(t, (*E)->GetSuperclass(E, deref(A1)))); return; }
    if (IS("GetStringUTFChars")) {
      jstring s = deref(A1);
      if (!s) { RET(0); return; }
      const char *p = (*E)->GetStringUTFChars(E, s, NULL);
      uint32_t g = p ? guest_strdup(p) : 0;
      if (p) (*E)->ReleaseStringUTFChars(E, s, p);
      if (A2) *G(A2) = JNI_TRUE;
      RET(g);
      return;
    }
    if (IS("GetStringChars") || IS("GetStringCritical")) {
      jstring s = deref(A1);
      if (!s) { RET(0); return; }
      jsize n = (*E)->GetStringLength(E, s);
      uint32_t g = guest_malloc(2 * (uint32_t)n + 2);
      (*E)->GetStringRegion(E, s, 0, n, (jchar *)G(g));
      *(uint16_t *)G(g + 2 * (uint32_t)n) = 0;
      if (A2 && IS("GetStringChars")) *G(A2) = JNI_TRUE;
      RET(g);
      return;
    }
    if (IS("GetStringLength")) { RET((*E)->GetStringLength(E, deref(A1))); return; }
    if (IS("GetStringUTFLength")) { RET((*E)->GetStringUTFLength(E, deref(A1))); return; }
    if (IS("GetStringRegion")) { (*E)->GetStringRegion(E, deref(A1), (jsize)A2, (jsize)A3, (jchar *)G(A4)); return; }
    if (IS("GetStringUTFRegion")) { (*E)->GetStringUTFRegion(E, deref(A1), (jsize)A2, (jsize)A3, (char *)G(A4)); return; }
    if (IS("GetArrayLength")) { RET((*E)->GetArrayLength(E, deref(A1))); return; }
    if (IS("GetObjectArrayElement")) { RET(local_put(t, (*E)->GetObjectArrayElement(E, deref(A1), (jsize)A2))); return; }
    if (IS("GetPrimitiveArrayCritical")) { jobject a = deref(A1); RET(a ? pin_get(E, array_type(E, a), a, A2) : 0); return; }
    if (IS("GetJavaVM")) { G32(A1) = guest_vm; RET(0); return; }
    if (IS("GetDirectBufferAddress")) { RET(direct_address(E, deref(A1))); return; }
    if (IS("GetDirectBufferCapacity")) { RET((*E)->GetDirectBufferCapacity(E, deref(A1))); return; }
    if (IS("GetObjectRefType")) { uint32_t h = A1; RET(!h ? 0 : (h & 3) == 1 ? JNILocalRefType : (h & 3) == 2 ? JNIGlobalRefType : JNIWeakGlobalRefType); return; }
    break;
  case 'F':
    if (IS("FindClass")) {
      jclass k = (*E)->FindClass(E, GS(A1));
      if (!k) { rt_log("JNI FindClass(%s) failed", GS(A1)); if ((*E)->ExceptionCheck(E)) (*E)->ExceptionDescribe(E); }
      RET(local_put(t, k));
      return;
    }
    if (IS("FromReflectedMethod")) { RET(id_put(&methods, (*E)->FromReflectedMethod(E, deref(A1)), "?", "()V")); rt_log("JNI FromReflectedMethod: signature unknown"); return; }
    if (IS("FromReflectedField")) { RET(id_put(&fields, (*E)->FromReflectedField(E, deref(A1)), "?", "I")); return; }
    if (IS("FatalError")) { (*E)->FatalError(E, GS(A1)); return; }
    break;
  case 'N':
    if (IS("NewStringUTF")) { RET(A1 ? local_put(t, (*E)->NewStringUTF(E, GS(A1))) : 0); return; }
    if (IS("NewString")) { RET(local_put(t, (*E)->NewString(E, (const jchar *)GP(A1), (jsize)A2))); return; }
    if (IS("NewGlobalRef")) { RET(global_put((*E)->NewGlobalRef(E, deref(A1)), 2)); return; }
    if (IS("NewWeakGlobalRef")) { RET(global_put((*E)->NewWeakGlobalRef(E, deref(A1)), 3)); return; }
    if (IS("NewLocalRef")) { RET(local_put(t, (*E)->NewLocalRef(E, deref(A1)))); return; }
    if (IS("NewObjectArray")) { RET(local_put(t, (*E)->NewObjectArray(E, (jsize)A1, deref(A2), deref(A3)))); return; }
    if (IS("NewDirectByteBuffer")) { RET(local_put(t, (*E)->NewDirectByteBuffer(E, G(A1), (jlong)(uint64_t)ARG(c, 2) | (jlong)((uint64_t)ARG(c, 3) << 32)))); return; }
    break;
  case 'D':
    if (IS("DeleteLocalRef")) {
      uint32_t h = A1, i = h >> 2;
      if ((h & 3) != 1 || i >= t->ltop || !t->loc[i]) return;
      (*E)->DeleteLocalRef(E, t->loc[i]);
      t->loc[i] = NULL;
      uint32_t floor = t->nframes ? t->frames[t->nframes - 1] : 1;
      while (t->ltop > floor && t->ltop > 1 && !t->loc[t->ltop - 1]) t->ltop--;
      return;
    }
    if (IS("DeleteGlobalRef") || IS("DeleteWeakGlobalRef")) {
      jobject o = deref(A1);
      if (!o || (A1 & 3) < 2) return;
      if (IS("DeleteGlobalRef")) (*E)->DeleteGlobalRef(E, o); else (*E)->DeleteWeakGlobalRef(E, o);
      global_drop(A1);
      return;
    }
    if (IS("DefineClass")) { rt_log("JNI DefineClass unsupported"); RET(0); return; }
    break;
  case 'R':
    if (IS("ReleaseStringUTFChars")) { guest_free(A2); return; }
    if (IS("ReleaseStringChars") || IS("ReleaseStringCritical")) { guest_free(A2); return; }
    if (IS("ReleasePrimitiveArrayCritical")) { pin_release(E, A2, (int)A3); return; }
    if (IS("RegisterNatives")) {
      for (uint32_t i = 0; i < A3; i++)
        rt_log("JNI RegisterNatives %s %s -> guest %08x (not bound: the exported Java_* wrappers are used)",
               GS(G32(A2 + 12 * i)), GS(G32(A2 + 12 * i + 4)), G32(A2 + 12 * i + 8));
      RET(0);
      return;
    }
    break;
  case 'E':
    if (IS("ExceptionCheck")) { RET((*E)->ExceptionCheck(E)); return; }
    if (IS("ExceptionOccurred")) { RET(local_put(t, (*E)->ExceptionOccurred(E))); return; }
    if (IS("ExceptionClear")) { (*E)->ExceptionClear(E); return; }
    if (IS("ExceptionDescribe")) { (*E)->ExceptionDescribe(E); return; }
    if (IS("EnsureLocalCapacity")) { RET((*E)->EnsureLocalCapacity(E, (jint)A1)); return; }
    break;
  case 'I':
    if (IS("IsSameObject")) { RET((*E)->IsSameObject(E, deref(A1), deref(A2))); return; }
    if (IS("IsInstanceOf")) { RET((*E)->IsInstanceOf(E, deref(A1), deref(A2))); return; }
    if (IS("IsAssignableFrom")) { RET((*E)->IsAssignableFrom(E, deref(A1), deref(A2))); return; }
    break;
  case 'P':
    if (IS("PushLocalFrame")) {
      jint r = (*E)->PushLocalFrame(E, (jint)A1);
      if (!r && t->nframes < 64) t->frames[t->nframes++] = t->ltop;
      RET(r);
      return;
    }
    if (IS("PopLocalFrame")) {
      jobject keep = deref(A1);
      jobject r = (*E)->PopLocalFrame(E, keep);
      if (t->nframes) {
        uint32_t mark = t->frames[--t->nframes];
        for (uint32_t i = mark; i < t->ltop; i++) t->loc[i] = NULL;
        t->ltop = mark;
      }
      RET(local_put(t, r));
      return;
    }
    break;
  case 'S':
    if (IS("SetObjectArrayElement")) { (*E)->SetObjectArrayElement(E, deref(A1), (jsize)A2, deref(A3)); return; }
    break;
  case 'T':
    if (IS("Throw")) { RET((*E)->Throw(E, deref(A1))); return; }
    if (IS("ThrowNew")) { RET((*E)->ThrowNew(E, deref(A1), GS(A2))); return; }
    if (IS("ToReflectedMethod")) { IdEnt *e = id_get(&methods, A2); RET(e ? local_put(t, (*E)->ToReflectedMethod(E, deref(A1), e->id, (jboolean)A3)) : 0); return; }
    if (IS("ToReflectedField")) { IdEnt *e = id_get(&fields, A2); RET(e ? local_put(t, (*E)->ToReflectedField(E, deref(A1), e->id, (jboolean)A3)) : 0); return; }
    break;
  case 'M':
    if (IS("MonitorEnter")) { RET((*E)->MonitorEnter(E, deref(A1))); return; }
    if (IS("MonitorExit")) { RET((*E)->MonitorExit(E, deref(A1))); return; }
    break;
  case 'A':
    if (IS("AllocObject")) { RET(local_put(t, (*E)->AllocObject(E, deref(A1)))); return; }
    break;
  case 'U':
    if (IS("UnregisterNatives")) { RET(0); return; }
    break;
  }
  rt_log("JNI %s: not implemented, returns 0", name);
  RET(0);
}

typedef struct { CPU *c; int slot; } EnvCall;

static void env_dispatch(void *p) {
  EnvCall *ec = p;
  CPU *c = ec->c;
  int slot = ec->slot;
  TState *t = ts();
  JNIEnv *env = henv(t);
  const Meta *m = &meta[slot];
  void **ft = (void **)*env;
  uint32_t a1 = ARG(c, 1), a2 = ARG(c, 2), a3 = ARG(c, 3);
  switch (m->kind) {
  case K_CALL: do_call(c, t, env, slot); break;
  case K_NEWOBJ: {
    IdEnt *e = id_get(&methods, ARG(c, 2));
    jclass k = deref(ARG(c, 1));
    if (!e || !k) { c->r[0] = 0; break; }
    jvalue jv[64];
    convert_args(c, m, e, 3, jv);
    sync_to_host(env);
    jobject o = (*env)->NewObjectA(env, k, e->id, jv);
    sync_to_guest(env);
    c->r[0] = local_put(t, o);
    break;
  }
  case K_GETF: case K_SETF: do_field(c, t, env, slot); break;
  case K_NEWARR: c->r[0] = local_put(t, ((jarray (*)(JNIEnv *, jsize))ft[slot])(env, (jsize)ARG(c, 1))); break;
  case K_GETELEMS: c->r[0] = pin_get(env, m->type, deref(ARG(c, 1)), ARG(c, 2)); break;
  case K_RELELEMS: pin_release(env, ARG(c, 2), (int)ARG(c, 3)); c->r[0] = 0; break;
  case K_GETREG: case K_SETREG: {
    jarray a = deref(ARG(c, 1));
    uint32_t start = ARG(c, 2), n = ARG(c, 3), buf = ARG(c, 4);
    if (a) region(env, m->kind == K_SETREG, m->type, a, start, n, G(buf));
    if (a && m->kind == K_SETREG && npins) {   /* keep a pinned copy of the same array current */
      pthread_mutex_lock(&pin_lock);
      Pin *p = pin_find_obj(env, a);
      if (p && !p->mirror && (uint64_t)start + n <= p->len) memcpy(G(p->gaddr + start * esize(m->type)), G(buf), n * esize(m->type));
      pthread_mutex_unlock(&pin_lock);
    }
    break;
  }
  default: env_other(c, t, env, slot); break;
  }
  if (trace_jni && m->kind != K_CALL) rt_log("JNI %s(%x, %x, %x) -> %x", jni_env_names[slot], a1, a2, a3, c->r[0]);
}

static void bridge_env(CPU *c, int slot, const char *name) {
  (void)name;
  EnvCall ec = { c, slot };
  on_java_stack(env_dispatch, &ec);
}

/* ------------------------------------------------------------------ JavaVM backend */
static void vm_dispatch(void *p) {
  EnvCall *ec = p;
  CPU *c = ec->c;
  TState *t = ts();
  switch (ec->slot) {
  case 4: case 7: {   /* AttachCurrentThread(AsDaemon)(vm, JNIEnv **, JavaVMAttachArgs *) */
    JavaVMAttachArgs args = { JNI_VERSION_1_6, NULL, NULL }, *pa = NULL;
    if (ARG(c, 2)) { uint32_t g = ARG(c, 2); args.version = (jint)G32(g); args.name = G32(g + 4) ? GS(G32(g + 4)) : NULL; args.group = deref(G32(g + 8)); pa = &args; }
    JNIEnv *e = NULL;
    jint r = ec->slot == 4 ? (*host_vm)->AttachCurrentThread(host_vm, &e, pa) : (*host_vm)->AttachCurrentThreadAsDaemon(host_vm, &e, pa);
    if (r == JNI_OK) { t->env = e; if (ARG(c, 1)) G32(ARG(c, 1)) = jni_guest_env(); }
    c->r[0] = (uint32_t)r;
    break;
  }
  case 5:   /* DetachCurrentThread */
    c->r[0] = (uint32_t)(*host_vm)->DetachCurrentThread(host_vm);
    t->env = NULL;
    t->attached_by_us = 0;
    for (uint32_t i = 1; i < t->ltop; i++) t->loc[i] = NULL;
    t->ltop = 1;
    t->nframes = 0;
    break;
  case 6: {   /* GetEnv(vm, void **, version) */
    JNIEnv *e = NULL;
    jint r = (*host_vm)->GetEnv(host_vm, (void **)&e, (jint)ARG(c, 2));
    if (r == JNI_OK) { if (!t->env) t->env = e; if (ARG(c, 1)) G32(ARG(c, 1)) = jni_guest_env(); }
    c->r[0] = (uint32_t)r;
    break;
  }
  default:
    rt_log("JavaVM slot %d not supported", ec->slot);
    c->r[0] = (uint32_t)JNI_ERR;
  }
}

static void bridge_vm(CPU *c, int slot, const char *name) {
  (void)name;
  EnvCall ec = { c, slot };
  on_java_stack(vm_dispatch, &ec);
}

/* ------------------------------------------------------------------ native entry (used by jni_exports.c) */
typedef struct { TState *t; JNIEnv *saved_env; uint32_t mark; int nframes; } BridgeFrame;

void bridge_begin(BridgeFrame *f, JNIEnv *env) {
  TState *t = ts();
  f->t = t;
  f->saved_env = t->env;
  f->mark = t->ltop;
  f->nframes = t->nframes;
  t->env = env;
  sync_to_guest(env);
}

void bridge_end(BridgeFrame *f) {
  TState *t = f->t;
  sync_to_host(t->env);
  for (uint32_t i = f->mark; i < t->ltop; i++) t->loc[i] = NULL;
  t->ltop = f->mark;
  t->nframes = f->nframes;
  t->env = f->saved_env;
}

uint32_t bridge_in(jobject o) { return local_put(ts(), o); }

jobject bridge_out(uint32_t h) {
  jobject o = deref(h);
  if (!o || (h & 3) == 1) return o;
  return (*ts()->env)->NewLocalRef(ts()->env, o);
}

uint32_t bridge_guest_env(void) { return jni_guest_env(); }

typedef struct { uint32_t addr; int n; const uint32_t *args; uint32_t r0, r1; } GuestCall;

static void guest_call_thunk(void *p) {
  GuestCall *g = p;
  g->r0 = guest_call(rt_cpu_for_thread(), g->addr, g->n, g->args, &g->r1);
}

uint64_t bridge_call(const char *name, uint32_t *addr, int n, const uint32_t *args) {
  if (!*addr) {
    *addr = guest_export(name);
    if (!*addr) { rt_log("native %s: no such export in the translated library", name); return 0; }
  }
  if (trace_jni) rt_log("native %s(%d words: %x %x %x %x)", name, n, args[0], n > 1 ? args[1] : 0, n > 2 ? args[2] : 0, n > 3 ? args[3] : 0);
  GuestCall g = { *addr, n, args, 0, 0 };
  on_big_stack(guest_call_thunk, &g);
  return (uint64_t)g.r0 | (uint64_t)g.r1 << 32;
}

/* ------------------------------------------------------------------ kns64.Guest natives */
/* Direct ByteBuffer inside guest memory: Java and native code share it exactly. */
JNIEXPORT jobject JNICALL Java_kns64_Guest_nativeAllocateDirect(JNIEnv *env, jclass cls, jint n) {
  (void)cls;
  uint32_t g = guest_calloc(n > 0 ? (uint32_t)n : 16);
  if (!g) return NULL;
  return (*env)->NewDirectByteBuffer(env, G(g), n);
}

/* Java wrote elements [off, off + count) of a pinned int[]: copy them into its guest pin. */
JNIEXPORT void JNICALL Java_kns64_Guest_nativeSyncInts(JNIEnv *env, jclass cls, jintArray a, jint off, jint count) {
  (void)cls;
  if (!npins || off < 0 || count <= 0) return;
  pthread_mutex_lock(&pin_lock);
  Pin *p = pin_find_obj(env, a);
  if (p && !p->mirror) {
    if ((uint32_t)off + (uint32_t)count > p->len) count = (jint)(p->len - (uint32_t)off);
    if (count > 0) (*env)->GetIntArrayRegion(env, a, off, count, (jint *)G(p->gaddr + 4 * (uint32_t)off));
  }
  if (trace_jni) rt_log("JNI: sync int[] %d+%d %s", off, count, p ? "into pin" : "(not pinned)");
  pthread_mutex_unlock(&pin_lock);
}

/* ------------------------------------------------------------------ JNI_OnLoad */
typedef struct { uint32_t vm; uint32_t ver; } OnLoad;

static void onload_thunk(void *p) {
  OnLoad *o = p;
  CPU *c = rt_cpu_for_thread();
  guest_run_init_array(c);
  uint32_t args[2] = { o->vm, 0 };
  o->ver = guest_call(c, guest_export("JNI_OnLoad"), 2, args, NULL);
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
  (void)reserved;
  host_vm = vm;
#ifdef __ANDROID__
  char v[PROP_VALUE_MAX] = "";
  __system_property_get("debug.kns64.jni", v);
  trace_jni = v[0] == '1';
#endif
  pthread_key_create(&ts_key, ts_dtor);
  JNIEnv *env = NULL;
  if ((*vm)->GetEnv(vm, (void **)&env, JNI_VERSION_1_6) != JNI_OK) return JNI_ERR;
  if (guest_boot()) return JNI_ERR;
  rt_log("KnS64: guest memory at %p, image %u bytes, %u blocks", (void *)guest_mem, GUEST_IMAGE_SIZE, guest_block_count);
  meta_init();
  jni_backend = bridge_env;
  jni_vm_backend = bridge_vm;
  guest_vm = jni_guest_init();
  BridgeFrame f;
  bridge_begin(&f, env);
  OnLoad o = { guest_vm, 0 };
  on_big_stack(onload_thunk, &o);
  bridge_end(&f);
  rt_log("KnS64: guest JNI_OnLoad returned %08x", o.ver);
  return JNI_VERSION_1_6;
}
