/* libc / libm / liblog / pthread imports of libgame.so, implemented on the host libc with
 * 32-bit guest structures converted at the boundary. The browser's equivalents are the
 * def('...') entries in apk-cpu-engine/runtime.js. Struct layouts are 32-bit ARM bionic. */
#include "rt.h"

#include <arpa/inet.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <math.h>
#include <netdb.h>
#include <pthread.h>
#include <sched.h>
#include <signal.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <strings.h>
#include <sys/mman.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#define SHIM(name) void imp_##name(CPU *c)
#define A(i) ARG(c, i)
#define RET(v) (c->r[0] = (uint32_t)(v))
static inline void RET64(CPU *c, uint64_t v) { c->r[0] = (uint32_t)v; c->r[1] = (uint32_t)(v >> 32); }
static inline double DARG(CPU *c, int i) { uint64_t u = (uint64_t)A(i) | ((uint64_t)A(i + 1) << 32); double d; memcpy(&d, &u, 8); return d; }
static inline void RETD(CPU *c, double d) { uint64_t u; memcpy(&u, &d, 8); RET64(c, u); }
static inline uint32_t G2H(const void *p) { return p ? (uint32_t)((const uint8_t *)p - guest_mem) : 0; }

/* ------------------------------------------------------------------ errno */
static uint32_t errno_slot(CPU *c) { return rt_thread(c)->tls_area + 128; }   /* past the TLS slots kuser get_tls exposes */
static void seterr(CPU *c) { G32(errno_slot(c)) = (uint32_t)errno; }
#define SYSRET(c, v) do { long _r = (long)(v); if (_r < 0) seterr(c); RET(_r); } while (0)
SHIM(__errno) { RET(errno_slot(c)); }

/* ------------------------------------------------------------------ memory */
SHIM(malloc) { RET(guest_malloc(A(0))); }
SHIM(free) { guest_free(A(0)); }
SHIM(calloc) { RET(guest_calloc(A(0) * A(1))); }
SHIM(realloc) { RET(guest_realloc(A(0), A(1))); }
SHIM(memcpy) { memmove(G(A(0)), G(A(1)), A(2)); }   /* r0 unchanged = dest */
SHIM(memmove) { memmove(G(A(0)), G(A(1)), A(2)); }
SHIM(memset) { memset(G(A(0)), (int)A(1), A(2)); }
SHIM(memcmp) { RET(memcmp(G(A(0)), G(A(1)), A(2))); }
SHIM(mmap) {
  uint32_t len = A(1), flags = A(3);
  int fd = (int)A(4);
  uint32_t off = A(5);
  uint32_t p = guest_malloc(len + 4096);
  if (!p) { RET(0xFFFFFFFFu); return; }
  uint32_t a = (p + 4095) & ~4095u;
  memset(G(a), 0, len);
  if (!(flags & MAP_ANONYMOUS) && fd >= 0) pread(fd, G(a), len, off);
  RET(a);
}
SHIM(munmap) { RET(0); }

/* ------------------------------------------------------------------ strings */
SHIM(strlen) { RET(strlen(GS(A(0)))); }
SHIM(strcpy) { char *d = GS(A(0)); memmove(d, GS(A(1)), strlen(GS(A(1))) + 1); }
SHIM(strncpy) { strncpy(GS(A(0)), GS(A(1)), A(2)); }
SHIM(strcat) { strcat(GS(A(0)), GS(A(1))); }
SHIM(strchr) { RET(G2H(strchr(GS(A(0)), (int)A(1)))); }
SHIM(strrchr) { RET(G2H(strrchr(GS(A(0)), (int)A(1)))); }
SHIM(strstr) { RET(G2H(strstr(GS(A(0)), GS(A(1))))); }
SHIM(strcasestr) { RET(G2H(strcasestr(GS(A(0)), GS(A(1))))); }
SHIM(strcmp) { RET(strcmp(GS(A(0)), GS(A(1)))); }
SHIM(strncmp) { RET(strncmp(GS(A(0)), GS(A(1)), A(2))); }
SHIM(strcasecmp) { RET(strcasecmp(GS(A(0)), GS(A(1)))); }
SHIM(strncasecmp) { RET(strncasecmp(GS(A(0)), GS(A(1)), A(2))); }
SHIM(strdup) { RET(guest_strdup(GS(A(0)))); }
SHIM(atoi) { RET(atoi(GS(A(0)))); }
SHIM(atol) { RET((int32_t)atol(GS(A(0)))); }
SHIM(atoll) { RET64(c, (uint64_t)atoll(GS(A(0)))); }
SHIM(strtoll) {
  char *end;
  long long v = strtoll(GS(A(0)), &end, (int)A(2));
  if (A(1)) G32(A(1)) = G2H(end);
  RET64(c, (uint64_t)v);
}
SHIM(strtod) {
  char *end;
  double v = strtod(GS(A(0)), &end);
  if (A(1)) G32(A(1)) = G2H(end);
  RETD(c, v);
}
static __thread uint32_t strtok_pos;
SHIM(strtok) {
  uint32_t s = A(0) ? A(0) : strtok_pos;
  const char *delim = GS(A(1));
  if (!s) { RET(0); return; }
  s += (uint32_t)strspn(GS(s), delim);
  if (!*GS(s)) { strtok_pos = 0; RET(0); return; }
  uint32_t e = s + (uint32_t)strcspn(GS(s), delim);
  if (*GS(e)) { *GS(e) = 0; strtok_pos = e + 1; } else strtok_pos = 0;
  RET(s);
}
static uint32_t strerror_buf;
SHIM(strerror) {
  if (!strerror_buf) strerror_buf = guest_malloc(128);
  snprintf(GS(strerror_buf), 128, "%s", strerror((int)A(0)));
  RET(strerror_buf);
}

/* ------------------------------------------------------------------ formatting */
/* Argument stream of a variadic guest call: core registers from ri, then the stack (AAPCS). */
typedef struct { CPU *c; int ri; uint32_t sp; } GVa;
static uint32_t va32(GVa *v) {
  if (v->ri < 4) return v->c->r[v->ri++];
  uint32_t x = G32(v->sp); v->sp += 4; return x;
}
static uint64_t va64(GVa *v) {
  if (v->ri < 4) {
    if (v->ri & 1) v->ri++;
    if (v->ri <= 2) { uint64_t x = (uint64_t)v->c->r[v->ri] | ((uint64_t)v->c->r[v->ri + 1] << 32); v->ri += 2; return x; }
    v->ri = 4;
  }
  v->sp = (v->sp + 7) & ~7u;
  uint64_t x = (uint64_t)G32(v->sp) | ((uint64_t)G32(v->sp + 4) << 32);
  v->sp += 8;
  return x;
}
static GVa va_regs(CPU *c, int first) { GVa v = { c, first, c->r[13] }; return v; }
static GVa va_list_at(CPU *c, uint32_t ap) { GVa v = { c, 4, ap }; return v; }

typedef struct { char *p; size_t n, cap; } Buf;
static void bput(Buf *b, const char *s, size_t n) {
  if (b->n + n + 1 > b->cap) {
    b->cap = (b->n + n + 1) * 2;
    b->p = realloc(b->p, b->cap);
  }
  memcpy(b->p + b->n, s, n);
  b->n += n;
  b->p[b->n] = 0;
}

/* printf with guest arguments. Guest long is 32-bit; pointers are guest addresses. */
static char *gformat(const char *fmt, GVa *va, size_t *len) {
  Buf b = { 0 };
  bput(&b, "", 0);
  while (*fmt) {
    const char *pct = strchr(fmt, '%');
    if (!pct) { bput(&b, fmt, strlen(fmt)); break; }
    bput(&b, fmt, (size_t)(pct - fmt));
    const char *f = pct + 1;
    char spec[64];
    size_t sn = 0;
    spec[sn++] = '%';
    while (*f && strchr("-+ #0'", *f) && sn < 20) spec[sn++] = *f++;
    if (*f == '*') { sn += (size_t)snprintf(spec + sn, 16, "%d", (int32_t)va32(va)); f++; }
    else while (*f >= '0' && *f <= '9' && sn < 40) spec[sn++] = *f++;
    if (*f == '.') {
      spec[sn++] = *f++;
      if (*f == '*') { sn += (size_t)snprintf(spec + sn, 16, "%d", (int32_t)va32(va)); f++; }
      else while (*f >= '0' && *f <= '9' && sn < 50) spec[sn++] = *f++;
    }
    int lng = 0, half = 0;
    for (;;) {
      if (*f == 'l') { lng++; f++; }
      else if (*f == 'h') { half++; f++; }
      else if (*f == 'q' || *f == 'j' || *f == 'L') { lng = 2; f++; }
      else if (*f == 'z' || *f == 't') { f++; }
      else break;
    }
    char conv = *f ? *f++ : 0;
    char tmp[512];
    int n = 0;
    switch (conv) {
    case 'd': case 'i':
      if (lng >= 2) { spec[sn] = 0; strcat(spec, "lld"); n = snprintf(tmp, sizeof tmp, spec, (long long)va64(va)); }
      else {
        int32_t v = (int32_t)va32(va);
        if (half == 1) v = (int16_t)v; else if (half >= 2) v = (int8_t)v;
        spec[sn++] = 'd'; spec[sn] = 0; n = snprintf(tmp, sizeof tmp, spec, v);
      }
      break;
    case 'u': case 'o': case 'x': case 'X':
      if (lng >= 2) { spec[sn] = 0; char e[4] = { 'l', 'l', conv, 0 }; strcat(spec, e); n = snprintf(tmp, sizeof tmp, spec, (unsigned long long)va64(va)); }
      else {
        uint32_t v = va32(va);
        if (half == 1) v = (uint16_t)v; else if (half >= 2) v = (uint8_t)v;
        spec[sn++] = conv; spec[sn] = 0; n = snprintf(tmp, sizeof tmp, spec, v);
      }
      break;
    case 'c': spec[sn++] = 'c'; spec[sn] = 0; n = snprintf(tmp, sizeof tmp, spec, (int)va32(va)); break;
    case 'p': n = snprintf(tmp, sizeof tmp, "0x%x", va32(va)); break;
    case 'f': case 'F': case 'e': case 'E': case 'g': case 'G': case 'a': case 'A': {
      uint64_t u = va64(va); double d; memcpy(&d, &u, 8);
      spec[sn++] = conv; spec[sn] = 0; n = snprintf(tmp, sizeof tmp, spec, d);
      break;
    }
    case 's': {
      uint32_t p = va32(va);
      const char *s = p ? GS(p) : "(null)";
      spec[sn++] = 's'; spec[sn] = 0;
      if (sn == 2) { bput(&b, s, strlen(s)); n = -1; }
      else {
        int need = snprintf(NULL, 0, spec, s);
        char *big = malloc((size_t)need + 1);
        snprintf(big, (size_t)need + 1, spec, s);
        bput(&b, big, (size_t)need);
        free(big);
        n = -1;
      }
      break;
    }
    case 'n': { uint32_t p = va32(va); if (p) G32(p) = (uint32_t)b.n; n = -1; break; }
    case '%': bput(&b, "%", 1); n = -1; break;
    default: bput(&b, pct, (size_t)(f - pct)); n = -1; break;
    }
    if (n > 0) bput(&b, tmp, (size_t)(n < (int)sizeof tmp ? n : (int)sizeof tmp - 1));
    fmt = f;
  }
  *len = b.n;
  return b.p;
}

static uint32_t put_guest(uint32_t dst, uint32_t cap, const char *s, size_t n) {
  if (cap) {
    size_t m = n < cap - 1 ? n : cap - 1;
    memcpy(G(dst), s, m);
    G(dst)[m] = 0;
  }
  return (uint32_t)n;
}

SHIM(snprintf) { GVa v = va_regs(c, 3); size_t n; char *s = gformat(GS(A(2)), &v, &n); RET(put_guest(A(0), A(1), s, n)); free(s); }
SHIM(sprintf) { GVa v = va_regs(c, 2); size_t n; char *s = gformat(GS(A(1)), &v, &n); RET(put_guest(A(0), 0xFFFFFFFFu, s, n)); free(s); }
SHIM(vsnprintf) { GVa v = va_list_at(c, A(3)); size_t n; char *s = gformat(GS(A(2)), &v, &n); RET(put_guest(A(0), A(1), s, n)); free(s); }
SHIM(vsprintf) { GVa v = va_list_at(c, A(2)); size_t n; char *s = gformat(GS(A(1)), &v, &n); RET(put_guest(A(0), 0xFFFFFFFFu, s, n)); free(s); }
SHIM(__android_log_print) {
  GVa v = va_regs(c, 3); size_t n; char *s = gformat(GS(A(2)), &v, &n);
  rt_log("[%s] %s", A(1) ? GS(A(1)) : "", s);
  RET(n); free(s);
}
SHIM(__android_log_write) { rt_log("[%s] %s", A(1) ? GS(A(1)) : "", A(2) ? GS(A(2)) : ""); RET(1); }
SHIM(puts) { rt_log("%s", GS(A(0))); RET(1); }

/* sscanf: guest long is 32-bit, so integer conversions drop one 'l'; outputs are guest pointers. */
SHIM(sscanf) {
  const char *fmt = GS(A(1));
  char conv[512];
  size_t o = 0;
  int nptr = 0;
  for (const char *f = fmt; *f && o < sizeof conv - 4; f++) {
    conv[o++] = *f;
    if (*f != '%') continue;
    f++;
    if (*f == '%') { conv[o++] = '%'; continue; }
    int skip = 0;
    if (*f == '*') { skip = 1; conv[o++] = *f++; }
    while (*f >= '0' && *f <= '9') conv[o++] = *f++;
    int l = 0;
    while (*f == 'l' || *f == 'h' || *f == 'L' || *f == 'q') {
      if (*f == 'l') l++;
      else conv[o++] = *f;
      f++;
    }
    char k = *f;
    if (strchr("eEfgGaA", k)) { if (l) conv[o++] = 'l'; }
    else if (l >= 2) { conv[o++] = 'l'; conv[o++] = 'l'; }
    if (k == '[') { while (*f && *f != ']') conv[o++] = *f++; if (*f) conv[o++] = *f; }
    else conv[o++] = k;
    if (!skip) nptr++;
    if (!*f) break;
  }
  conv[o] = 0;
  void *p[16] = { 0 };
  GVa v = va_regs(c, 2);
  for (int i = 0; i < nptr && i < 16; i++) p[i] = G(va32(&v));
  RET(sscanf(GS(A(0)), conv, p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], p[8], p[9], p[10], p[11], p[12], p[13], p[14], p[15]));
}

/* ------------------------------------------------------------------ stdio */
#define GUEST_FILE_SIZE 84
#define MAX_FILES 256
static FILE *files[MAX_FILES];
static uint32_t file_addr[MAX_FILES];
static pthread_mutex_t file_lock = PTHREAD_MUTEX_INITIALIZER;

static FILE *gfile(uint32_t a) {
  if (a >= GUEST_DATA_sF && a < GUEST_DATA_sF + 3 * GUEST_FILE_SIZE) {
    int i = (int)((a - GUEST_DATA_sF) / GUEST_FILE_SIZE);
    return i == 0 ? stdin : i == 1 ? stdout : stderr;
  }
  for (int i = 0; i < MAX_FILES; i++) if (file_addr[i] == a && a) return files[i];
  return NULL;
}
static uint32_t new_gfile(FILE *f) {
  if (!f) return 0;
  pthread_mutex_lock(&file_lock);
  for (int i = 0; i < MAX_FILES; i++) if (!file_addr[i]) {
    file_addr[i] = guest_calloc(GUEST_FILE_SIZE);
    files[i] = f;
    pthread_mutex_unlock(&file_lock);
    return file_addr[i];
  }
  pthread_mutex_unlock(&file_lock);
  fclose(f);
  return 0;
}
SHIM(fopen) { FILE *f = fopen(GS(A(0)), GS(A(1))); if (!f) seterr(c); RET(new_gfile(f)); }
SHIM(fdopen) { RET(new_gfile(fdopen((int)A(0), GS(A(1))))); }
SHIM(fclose) {
  uint32_t a = A(0);
  pthread_mutex_lock(&file_lock);
  for (int i = 0; i < MAX_FILES; i++) if (file_addr[i] == a && a) {
    fclose(files[i]); guest_free(file_addr[i]); file_addr[i] = 0; files[i] = NULL;
    pthread_mutex_unlock(&file_lock); RET(0); return;
  }
  pthread_mutex_unlock(&file_lock);
  RET(-1);
}
#define FILEARG(i) FILE *f = gfile(A(i)); if (!f) { RET(-1); return; }
SHIM(fread) { FILEARG(3); RET(fread(G(A(0)), A(1), A(2), f)); }
SHIM(fwrite) {
  FILE *f = gfile(A(3));
  if (f == stdout || f == stderr) { rt_log("%.*s", (int)(A(1) * A(2)), GS(A(0))); RET(A(2)); return; }
  if (!f) { RET(0); return; }
  RET(fwrite(G(A(0)), A(1), A(2), f));
}
SHIM(fseek) { FILEARG(0); RET(fseek(f, (int32_t)A(1), (int)A(2))); }
SHIM(ftell) { FILEARG(0); RET(ftell(f)); }
SHIM(feof) { FILEARG(0); RET(feof(f)); }
SHIM(fgetc) { FILEARG(0); RET(fgetc(f)); }
SHIM(fgets) { FILE *f = gfile(A(2)); RET(f && fgets(GS(A(0)), (int)A(1), f) ? A(0) : 0); }
SHIM(fsync) { SYSRET(c, fsync((int)A(0))); }
SHIM(fprintf) {
  GVa v = va_regs(c, 2); size_t n; char *s = gformat(GS(A(1)), &v, &n);
  FILE *f = gfile(A(0));
  if (f == stdout || f == stderr || !f) rt_log("%s", s); else fwrite(s, 1, n, f);
  RET(n); free(s);
}

/* ------------------------------------------------------------------ files */
#define OPEN_FLAGS (O_ACCMODE | O_CREAT | O_EXCL | O_TRUNC | O_APPEND | O_NONBLOCK)
SHIM(open) { SYSRET(c, open(GS(A(0)), (int)(A(1) & OPEN_FLAGS), (int)A(2))); }
SHIM(close) { SYSRET(c, close((int)A(0))); }
SHIM(read) { SYSRET(c, read((int)A(0), G(A(1)), A(2))); }
SHIM(write) { SYSRET(c, write((int)A(0), G(A(1)), A(2))); }
SHIM(lseek) { SYSRET(c, lseek((int)A(0), (int32_t)A(1), (int)A(2))); }
SHIM(dup) { SYSRET(c, dup((int)A(0))); }
SHIM(fcntl) {
  int cmd = (int)A(1);
  if (cmd == F_GETFL || cmd == F_SETFL || cmd == F_GETFD || cmd == F_SETFD) SYSRET(c, fcntl((int)A(0), cmd, (int)(cmd == F_SETFL ? (A(2) & OPEN_FLAGS) : A(2))));
  else { errno = EINVAL; seterr(c); RET(-1); }
}
SHIM(mkdir) { SYSRET(c, mkdir(GS(A(0)), (mode_t)A(1))); }
SHIM(rmdir) { SYSRET(c, rmdir(GS(A(0)))); }
SHIM(unlink) { SYSRET(c, unlink(GS(A(0)))); }
SHIM(remove) { SYSRET(c, remove(GS(A(0)))); }
SHIM(rename) { SYSRET(c, rename(GS(A(0)), GS(A(1)))); }

/* struct stat, 32-bit ARM bionic (104 bytes) */
static void put_stat(uint32_t g, const struct stat *s) {
  uint8_t *p = G(g);
  memset(p, 0, 104);
  uint64_t dev = s->st_dev, rdev = s->st_rdev, size = (uint64_t)s->st_size, blocks = (uint64_t)s->st_blocks, ino = s->st_ino;
  memcpy(p + 0, &dev, 8);
  uint32_t w;
  w = (uint32_t)ino; memcpy(p + 12, &w, 4);
  w = s->st_mode; memcpy(p + 16, &w, 4);
  w = (uint32_t)s->st_nlink; memcpy(p + 20, &w, 4);
  w = s->st_uid; memcpy(p + 24, &w, 4);
  w = s->st_gid; memcpy(p + 28, &w, 4);
  memcpy(p + 32, &rdev, 8);
  memcpy(p + 48, &size, 8);
  w = (uint32_t)s->st_blksize; memcpy(p + 56, &w, 4);
  memcpy(p + 64, &blocks, 8);
  uint32_t t[6] = { (uint32_t)s->st_atim.tv_sec, (uint32_t)s->st_atim.tv_nsec, (uint32_t)s->st_mtim.tv_sec,
                    (uint32_t)s->st_mtim.tv_nsec, (uint32_t)s->st_ctim.tv_sec, (uint32_t)s->st_ctim.tv_nsec };
  memcpy(p + 72, t, 24);
  memcpy(p + 96, &ino, 8);
}
SHIM(stat) { struct stat s; int r = stat(GS(A(0)), &s); if (!r) put_stat(A(1), &s); SYSRET(c, r); }
SHIM(lstat) { struct stat s; int r = lstat(GS(A(0)), &s); if (!r) put_stat(A(1), &s); SYSRET(c, r); }
SHIM(fstat) { struct stat s; int r = fstat((int)A(0), &s); if (!r) put_stat(A(1), &s); SYSRET(c, r); }
SHIM(statfs) {
  struct statfs s;
  int r = statfs(GS(A(0)), &s);
  if (!r) {
    uint8_t *p = G(A(1));
    memset(p, 0, 84);
    uint32_t w = (uint32_t)s.f_type; memcpy(p, &w, 4);
    w = (uint32_t)s.f_bsize; memcpy(p + 4, &w, 4);
    uint64_t q[5] = { s.f_blocks, s.f_bfree, s.f_bavail, s.f_files, s.f_ffree };
    memcpy(p + 8, q, 40);
    w = (uint32_t)s.f_namelen; memcpy(p + 56, &w, 4);
    w = (uint32_t)s.f_frsize; memcpy(p + 60, &w, 4);
  }
  SYSRET(c, r);
}

#define MAX_DIRS 64
static DIR *dirs[MAX_DIRS];
static uint32_t dir_buf[MAX_DIRS];
SHIM(opendir) {
  DIR *d = opendir(GS(A(0)));
  if (!d) { seterr(c); RET(0); return; }
  for (int i = 1; i < MAX_DIRS; i++) if (!dirs[i]) {
    dirs[i] = d;
    if (!dir_buf[i]) dir_buf[i] = guest_calloc(280);
    RET(i); return;
  }
  closedir(d); RET(0);
}
SHIM(readdir) {
  uint32_t i = A(0);
  if (!i || i >= MAX_DIRS || !dirs[i]) { RET(0); return; }
  struct dirent *e = readdir(dirs[i]);
  if (!e) { RET(0); return; }
  uint8_t *p = G(dir_buf[i]);
  uint64_t ino = e->d_ino; int64_t off = e->d_off; uint16_t rl = 280;
  memcpy(p, &ino, 8); memcpy(p + 8, &off, 8); memcpy(p + 16, &rl, 2);
  p[18] = e->d_type;
  snprintf((char *)p + 19, 256, "%s", e->d_name);
  RET(dir_buf[i]);
}
SHIM(closedir) { uint32_t i = A(0); if (i && i < MAX_DIRS && dirs[i]) { closedir(dirs[i]); dirs[i] = NULL; } RET(0); }

/* ------------------------------------------------------------------ time */
SHIM(time) { uint32_t t = (uint32_t)time(NULL); if (A(0)) G32(A(0)) = t; RET(t); }
SHIM(gettimeofday) {
  struct timeval tv;
  gettimeofday(&tv, NULL);
  if (A(0)) { G32(A(0)) = (uint32_t)tv.tv_sec; G32(A(0) + 4) = (uint32_t)tv.tv_usec; }
  RET(0);
}
SHIM(difftime) { RETD(c, difftime((time_t)(int32_t)A(0), (time_t)(int32_t)A(1))); }
static void tm_to_guest(uint32_t g, const struct tm *t) {
  int32_t v[11] = { t->tm_sec, t->tm_min, t->tm_hour, t->tm_mday, t->tm_mon, t->tm_year, t->tm_wday, t->tm_yday,
                    t->tm_isdst, (int32_t)t->tm_gmtoff, 0 };
  memcpy(G(g), v, sizeof v);
}
static void tm_from_guest(uint32_t g, struct tm *t) {
  int32_t v[9];
  memcpy(v, G(g), sizeof v);
  memset(t, 0, sizeof *t);
  t->tm_sec = v[0]; t->tm_min = v[1]; t->tm_hour = v[2]; t->tm_mday = v[3]; t->tm_mon = v[4];
  t->tm_year = v[5]; t->tm_wday = v[6]; t->tm_yday = v[7]; t->tm_isdst = v[8];
}
static uint32_t tm_buf;
SHIM(localtime) { time_t t = (time_t)(int32_t)G32(A(0)); struct tm r; localtime_r(&t, &r); if (!tm_buf) tm_buf = guest_calloc(44); tm_to_guest(tm_buf, &r); RET(tm_buf); }
SHIM(gmtime) { time_t t = (time_t)(int32_t)G32(A(0)); struct tm r; gmtime_r(&t, &r); if (!tm_buf) tm_buf = guest_calloc(44); tm_to_guest(tm_buf, &r); RET(tm_buf); }
SHIM(mktime) { struct tm t; tm_from_guest(A(0), &t); time_t r = mktime(&t); tm_to_guest(A(0), &t); RET((int32_t)r); }
SHIM(nanosleep) {
  struct timespec ts = { (time_t)(int32_t)G32(A(0)), (long)(int32_t)G32(A(0) + 4) };
  SYSRET(c, nanosleep(&ts, NULL));
}
SHIM(usleep) { RET(usleep(A(0))); }
SHIM(sleep) { RET(sleep(A(0))); }

/* ------------------------------------------------------------------ math (soft-float ABI) */
SHIM(cos) { RETD(c, cos(DARG(c, 0))); }
SHIM(sin) { RETD(c, sin(DARG(c, 0))); }
SHIM(sqrt) { RETD(c, sqrt(DARG(c, 0))); }
SHIM(pow) { RETD(c, pow(DARG(c, 0), DARG(c, 2))); }
SHIM(ceilf) { float f; uint32_t u = A(0); memcpy(&f, &u, 4); f = ceilf(f); memcpy(&u, &f, 4); RET(u); }
SHIM(lrand48) { RET((uint32_t)lrand48()); }
SHIM(srand48) { srand48((long)(int32_t)A(0)); }

/* ------------------------------------------------------------------ process */
SHIM(exit) { rt_log("guest exit(%d)", (int)A(0)); exit((int)A(0)); }
SHIM(abort) { rt_fatal(c, "guest abort()"); }
SHIM(getpid) { RET(getpid()); }
SHIM(kill) { RET(0); }      /* anti-tamper self-kill; the PLT stub is patched too (patch_native.py) */
SHIM(raise) { rt_log("guest raise(%d) ignored", (int)A(0)); RET(0); }
SHIM(system) { RET(-1); }
SHIM(popen) { RET(0); }
SHIM(pclose) { RET(-1); }
SHIM(setenv) { RET(setenv(GS(A(0)), GS(A(1)), (int)A(2))); }
SHIM(unsetenv) { RET(unsetenv(GS(A(0)))); }
SHIM(__stack_chk_fail) { rt_fatal(c, "guest stack protector failure"); }
SHIM(__cxa_pure_virtual) { rt_fatal(c, "pure virtual call"); }
SHIM(__cxa_finalize) { RET(0); }
SHIM(__aeabi_atexit) { RET(0); }
SHIM(__gnu_Unwind_Find_exidx) {
  /* .ARM.exidx of the guest image: address and entry count (arm2c.py layout) */
  if (A(1)) G32(A(1)) = GUEST_EXIDX_SIZE / 8;
  RET(GUEST_EXIDX_ADDR);
}

static __thread uint32_t qsort_fn;
static __thread CPU *qsort_cpu;
static int qsort_cmp(const void *a, const void *b) {
  uint32_t args[2] = { G2H(a), G2H(b) };
  return (int32_t)guest_call(qsort_cpu, qsort_fn, 2, args, NULL);
}
SHIM(qsort) {
  uint32_t pf = qsort_fn; CPU *pc = qsort_cpu;
  qsort_fn = A(3); qsort_cpu = c;
  qsort(G(A(0)), A(1), A(2), qsort_cmp);
  qsort_fn = pf; qsort_cpu = pc;
}

/* setjmp/longjmp: jmp_buf keeps r4-r14 and the return address. longjmp resumes at the block after
 * setjmp's call; translated code unwinds C frames until a function owning that block (guest.h). */
SHIM(setjmp) {
  for (int i = 4; i <= 14; i++) G32(A(0) + 4 * (i - 4)) = c->r[i];
  RET(0);
}
SHIM(longjmp) {
  uint32_t env = A(0), val = A(1);
  for (int i = 4; i <= 14; i++) c->r[i] = G32(env + 4 * (i - 4));
  c->r[0] = val ? val : 1;
  c->pc = c->r[14];
}

/* ------------------------------------------------------------------ network */
SHIM(socket) { SYSRET(c, socket((int)A(0), (int)A(1), (int)A(2))); }
SHIM(connect) { SYSRET(c, connect((int)A(0), (const struct sockaddr *)G(A(1)), A(2))); }
SHIM(send) { SYSRET(c, send((int)A(0), G(A(1)), A(2), (int)A(3))); }
SHIM(recvfrom) {
  socklen_t len = A(5) ? G32(A(5)) : 0;
  long r = recvfrom((int)A(0), G(A(1)), A(2), (int)A(3), A(4) ? (struct sockaddr *)G(A(4)) : NULL, A(5) ? &len : NULL);
  if (A(5)) G32(A(5)) = len;
  SYSRET(c, r);
}
SHIM(getsockopt) {
  socklen_t len = G32(A(4));
  int r = getsockopt((int)A(0), (int)A(1), (int)A(2), G(A(3)), &len);
  G32(A(4)) = len;
  SYSRET(c, r);
}
SHIM(select) {
  /* fd_set is the same bit array on both sides; timeval is 2 x int32 on the guest */
  struct timeval tv, *ptv = NULL;
  if (A(4)) { tv.tv_sec = (int32_t)G32(A(4)); tv.tv_usec = (int32_t)G32(A(4) + 4); ptv = &tv; }
  int r = select((int)A(0), GP(A(1)), GP(A(2)), GP(A(3)), ptv);
  if (A(4)) { G32(A(4)) = (uint32_t)tv.tv_sec; G32(A(4) + 4) = (uint32_t)tv.tv_usec; }
  SYSRET(c, r);
}
SHIM(inet_addr) { RET(inet_addr(GS(A(0)))); }
static uint32_t ntoa_buf;
SHIM(inet_ntoa) {
  struct in_addr a = { A(0) };
  if (!ntoa_buf) ntoa_buf = guest_calloc(32);
  snprintf(GS(ntoa_buf), 32, "%s", inet_ntoa(a));
  RET(ntoa_buf);
}
/* struct addrinfo, 32-bit: flags family socktype protocol addrlen canonname addr next (32 bytes) */
SHIM(getaddrinfo) {
  struct addrinfo hints, *res = NULL, *hp = NULL;
  if (A(2)) {
    memset(&hints, 0, sizeof hints);
    int32_t *h = (int32_t *)G(A(2));
    hints.ai_flags = h[0]; hints.ai_family = h[1]; hints.ai_socktype = h[2]; hints.ai_protocol = h[3];
    hp = &hints;
  }
  int r = getaddrinfo(A(0) ? GS(A(0)) : NULL, A(1) ? GS(A(1)) : NULL, hp, &res);
  if (r) { RET(r); return; }
  uint32_t head = 0, *link = &head;
  for (struct addrinfo *a = res; a; a = a->ai_next) {
    uint32_t g = guest_calloc(32 + (uint32_t)a->ai_addrlen);
    int32_t *w = (int32_t *)G(g);
    w[0] = a->ai_flags; w[1] = a->ai_family; w[2] = a->ai_socktype; w[3] = a->ai_protocol;
    w[4] = (int32_t)a->ai_addrlen; w[5] = 0; w[6] = (int32_t)(g + 32); w[7] = 0;
    memcpy(G(g + 32), a->ai_addr, a->ai_addrlen);
    *link = g;
    link = (uint32_t *)&w[7];
  }
  freeaddrinfo(res);
  G32(A(3)) = head;
  RET(0);
}
SHIM(freeaddrinfo) { for (uint32_t a = A(0); a;) { uint32_t n = G32(a + 28); guest_free(a); a = n; } }

/* ------------------------------------------------------------------ pthreads */
#define MAX_THREADS 256
static RtThread *threads[MAX_THREADS];
static pthread_mutex_t thread_lock = PTHREAD_MUTEX_INITIALIZER;

uint32_t rt_thread_register(RtThread *t) {
  pthread_mutex_lock(&thread_lock);
  for (uint32_t i = 1; i < MAX_THREADS; i++) if (!threads[i]) { threads[i] = t; pthread_mutex_unlock(&thread_lock); return i; }
  pthread_mutex_unlock(&thread_lock);
  rt_fatal(NULL, "too many guest threads");
}

static void *thread_main(void *p) {
  RtThread *t = p;
  extern void rt_bind_thread(RtThread *t);
  rt_bind_thread(t);
  t->retval = guest_call(&t->cpu, t->start, 1, &t->arg, NULL);
  t->done = 1;
  return NULL;
}

SHIM(pthread_create) {
  RtThread *t = calloc(1, sizeof *t);
  t->cpu.mem = guest_mem;
  t->cpu.thread = t;
  t->stack_size = 0x100000;
  t->stack_base = guest_malloc(t->stack_size);
  t->tls_area = guest_calloc(256);
  t->cpu.r[13] = (t->stack_base + t->stack_size - 16) & ~7u;
  t->start = A(2);
  t->arg = A(3);
  t->handle = rt_thread_register(t);
  pthread_attr_t at;
  pthread_attr_init(&at);
  pthread_attr_setstacksize(&at, 64 << 20);   /* translated frames are larger than guest frames */
  pthread_t h;
  int r = pthread_create(&h, &at, thread_main, t);
  pthread_attr_destroy(&at);
  if (r) { RET(r); return; }
  t->host = (void *)h;
  if (A(0)) G32(A(0)) = t->handle;
  RET(0);
}
SHIM(pthread_self) { RET(rt_thread(c)->handle); }
SHIM(pthread_equal) { RET(A(0) == A(1)); }
SHIM(pthread_detach) { RET(0); }
SHIM(pthread_join) {
  uint32_t i = A(0);
  if (!i || i >= MAX_THREADS || !threads[i] || !threads[i]->host) { RET(ESRCH); return; }
  pthread_join((pthread_t)threads[i]->host, NULL);
  if (A(1)) G32(A(1)) = threads[i]->retval;
  RET(0);
}
SHIM(pthread_exit) { rt_thread(c)->retval = A(0); rt_thread(c)->done = 1; pthread_exit(NULL); }
SHIM(pthread_attr_init) { memset(G(A(0)), 0, 24); RET(0); }
SHIM(pthread_attr_setdetachstate) { RET(0); }

/* Guest mutexes and condition variables are 4-byte words; each maps to a host object. */
#define MAP_SIZE 4096
typedef struct { uint32_t key; void *obj; } Slot;
static Slot mutex_map[MAP_SIZE], cond_map[MAP_SIZE];
static pthread_mutex_t map_lock = PTHREAD_MUTEX_INITIALIZER;
static void *map_get(Slot *m, uint32_t key, void *(*make)(uint32_t)) {
  uint32_t h = (key >> 2) * 2654435761u;
  pthread_mutex_lock(&map_lock);
  for (uint32_t i = 0; i < MAP_SIZE; i++) {
    Slot *s = &m[(h + i) & (MAP_SIZE - 1)];
    if (s->key == key) { pthread_mutex_unlock(&map_lock); return s->obj; }
    if (!s->key) { s->key = key; s->obj = make(key); pthread_mutex_unlock(&map_lock); return s->obj; }
  }
  pthread_mutex_unlock(&map_lock);
  rt_fatal(NULL, "mutex/cond map full");
}
static void map_del(Slot *m, uint32_t key) {
  /* keep the slot (open addressing); the host object is reused if the address is re-initialised */
  (void)m; (void)key;
}
static void *make_mutex(uint32_t key) {
  pthread_mutex_t *m = malloc(sizeof *m);
  pthread_mutexattr_t at;
  pthread_mutexattr_init(&at);
  /* bionic 32-bit: 0x4000 recursive, 0x8000 error-check (static initialisers or our init) */
  uint32_t v = G32(key);
  pthread_mutexattr_settype(&at, (v & 0x4000) ? PTHREAD_MUTEX_RECURSIVE : PTHREAD_MUTEX_NORMAL);
  pthread_mutex_init(m, &at);
  pthread_mutexattr_destroy(&at);
  return m;
}
static void *make_cond(uint32_t key) {
  (void)key;
  pthread_cond_t *cv = malloc(sizeof *cv);
  pthread_cond_init(cv, NULL);
  return cv;
}
SHIM(pthread_mutexattr_init) { G32(A(0)) = 0; RET(0); }
SHIM(pthread_mutexattr_destroy) { RET(0); }
SHIM(pthread_mutexattr_settype) { G32(A(0)) = A(1); RET(0); }
SHIM(pthread_mutex_init) {
  uint32_t type = A(1) ? G32(A(1)) : 0;
  G32(A(0)) = type == 1 ? 0x4000 : type == 2 ? 0x8000 : 0;
  map_get(mutex_map, A(0), make_mutex);
  RET(0);
}
SHIM(pthread_mutex_destroy) { map_del(mutex_map, A(0)); RET(0); }
SHIM(pthread_mutex_lock) { RET(pthread_mutex_lock(map_get(mutex_map, A(0), make_mutex))); }
SHIM(pthread_mutex_trylock) { RET(pthread_mutex_trylock(map_get(mutex_map, A(0), make_mutex))); }
SHIM(pthread_mutex_unlock) { RET(pthread_mutex_unlock(map_get(mutex_map, A(0), make_mutex))); }
SHIM(pthread_cond_init) { map_get(cond_map, A(0), make_cond); RET(0); }
SHIM(pthread_cond_destroy) { map_del(cond_map, A(0)); RET(0); }
SHIM(pthread_cond_signal) { RET(pthread_cond_signal(map_get(cond_map, A(0), make_cond))); }
SHIM(pthread_cond_wait) { RET(pthread_cond_wait(map_get(cond_map, A(0), make_cond), map_get(mutex_map, A(1), make_mutex))); }
SHIM(pthread_cond_timedwait) {
  struct timespec ts = { (time_t)(int32_t)G32(A(2)), (long)(int32_t)G32(A(2) + 4) };
  RET(pthread_cond_timedwait(map_get(cond_map, A(0), make_cond), map_get(mutex_map, A(1), make_mutex), &ts));
}
#define MAX_KEYS 128
static pthread_key_t keys[MAX_KEYS];
static uint32_t key_count;
SHIM(pthread_key_create) {
  pthread_mutex_lock(&thread_lock);
  uint32_t k = key_count < MAX_KEYS ? key_count++ : MAX_KEYS;
  pthread_mutex_unlock(&thread_lock);
  if (k >= MAX_KEYS || pthread_key_create(&keys[k], NULL)) { RET(EAGAIN); return; }
  G32(A(0)) = k;
  RET(0);
}
SHIM(pthread_getspecific) { uint32_t k = A(0); RET(k < key_count ? (uint32_t)(uintptr_t)pthread_getspecific(keys[k]) : 0); }
SHIM(pthread_setspecific) { uint32_t k = A(0); RET(k < key_count ? pthread_setspecific(keys[k], (void *)(uintptr_t)A(1)) : EINVAL); }

/* ------------------------------------------------------------------ init */
void libc_init(void) {
  G32(GUEST_DATA_stack_chk_guard) = 0x1234ABCDu;
  G32(GUEST_DATA_page_size) = 4096;
}
