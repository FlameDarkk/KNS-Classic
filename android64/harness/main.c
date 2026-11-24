/* Boot test for the translated libgame: load the guest image, run init_array, call JNI_OnLoad
 * with a fake JavaVM, run the Java startup sequence of browser/port/java_shim.js (startup())
 * against a minimal fake Java, then N nativeProcess frames on an EGL pbuffer GLES1 context.
 * The last frame is saved as a PPM. Every JNI call is logged (KNS64_QUIET=1 hides them).
 *   adb push kns64_harness-<abi> /data/local/tmp/kns64
 *   adb shell /data/local/tmp/kns64 [frames] [out.ppm]
 * Proves the translated code and runtime boot; it is not the game's Java layer (no assets,
 * no network, no input).
 */
#include "rt.h"

#include <EGL/egl.h>
#include <GLES/gl.h>
#include <pthread.h>
#include <fcntl.h>
#include <stdarg.h>
#include <strings.h>
#include <sys/stat.h>
#include <stdio.h>
#include <unistd.h>
#include <stdlib.h>
#include <time.h>

#define K "Java_com_com2us_wrapper_kernel_"
#define PKG_NAME "com.com2us.imhero.normal.freefull.google.global.android.common"
#define FONT_SIZE 2048
#define SCREEN_W 1024
#define SCREEN_H 592

/* ------------------------------------------------------------------ fake Java objects */
typedef struct { uint32_t buf, len, esize; char name[96]; } Obj;   /* arrays: buf/len; strings: buf = guest C string */
static Obj objs[4096];
static uint32_t nobj = 0x100;
static char ids[1024][128];      /* method/field ids -> "name sig" */
static uint32_t nids = 1;
typedef struct { uint32_t obj, fid, val; } Field;
static Field fields[512];
static int nfields, jni_calls, quiet;

static uint32_t new_obj(const char *name, uint32_t buf, uint32_t len, uint32_t esize) {
  uint32_t h = nobj++;
  Obj *o = &objs[h % 4096];
  o->buf = buf; o->len = len; o->esize = esize;
  snprintf(o->name, sizeof o->name, "%s", name);
  return h;
}
static Obj *obj(uint32_t h) { return &objs[h % 4096]; }
static uint32_t new_id(const char *name, const char *sig) {
  snprintf(ids[nids % 1024], 128, "%s %s", name, sig);
  return 0x10000 + nids++;
}
static const char *id_name(uint32_t id) { return id >= 0x10000 ? ids[(id - 0x10000) % 1024] : "?"; }
static uint32_t *field(uint32_t o, uint32_t fid) {
  for (int i = 0; i < nfields; i++) if (fields[i].obj == o && fields[i].fid == fid) return &fields[i].val;
  fields[nfields].obj = o; fields[nfields].fid = fid; fields[nfields].val = 0;
  return &fields[nfields++].val;
}

#define LOG(...) do { if (!quiet) rt_log(__VA_ARGS__); } while (0)

/* Game files: one pack (game_res.pack) with an index of "name offset length" lines, the same
 * (offset, length)-in-APK layout the Android build gives the game (build_android64.py assets). */
static const char *pack_path = "/data/local/tmp/game_res.pack";
typedef struct { char name[96]; uint32_t off, len; } PackEntry;
static PackEntry *pack;
static int npack;
static void pack_load(const char *idx) {
  FILE *f = fopen(idx, "r");
  if (!f) { rt_log("no asset index %s", idx); return; }
  pack = calloc(8192, sizeof *pack);
  while (npack < 8192 && fscanf(f, "%95s %u %u", pack[npack].name, &pack[npack].off, &pack[npack].len) == 3) npack++;
  fclose(f);
  rt_log("asset pack: %d files", npack);
}
static const PackEntry *pack_find(const char *name) {
  char alt[128];
  snprintf(alt, sizeof alt, "%s.jpg", name);
  for (int i = 0; i < npack; i++) if (!strcmp(pack[i].name, name) || !strcmp(pack[i].name, alt)) return &pack[i];
  return NULL;
}
#define FILES_DIR "/data/local/tmp/kns_files/"

/* i-th Java argument of a Call*Method / Call*MethodV / Call*MethodA (word-sized arguments only) */
static uint32_t marg(CPU *c, const char *name, int i) {
  size_t n = strlen(name);
  if (name[n - 1] == 'V') return G32(ARG(c, 3) + 4 * i);
  if (name[n - 1] == 'A') return G32(ARG(c, 3) + 8 * i);
  return ARG(c, 3 + i);
}

static uint32_t jstr(const char *s) { return new_obj("String", guest_strdup(s), 0, 1); }

/* The few Java methods the boot path needs answers from (values as browser/port/java_shim.js). */
static uint32_t fake_call(CPU *c, const char *name, const char *m) {
  LOG("JNI %s %s", name, m);
  if (!strncmp(m, "GetDeviceWidth ", 15)) return SCREEN_W;
  if (!strncmp(m, "GetDeviceHeight ", 16)) return SCREEN_H;
  if (!strncmp(m, "GetVersionSDK ", 14)) return 19;
  if (!strncmp(m, "CheckMinLoad ", 13)) return 1;
  if (!strncmp(m, "getFileDescriptorFromAsset ", 27)) {
    const char *file = GS(obj(marg(c, name, 0))->buf);
    Obj *out = obj(marg(c, name, 1));
    const PackEntry *e = pack_find(file);
    if (!e) { LOG("asset missing: %s", file); return 0; }
    if (out->buf) { G32(out->buf) = e->off; G32(out->buf + 4) = e->len; }
    LOG("asset %s -> pack offset %u (%u bytes)", file, e->off, e->len);
    return new_obj("FileDescriptor", (uint32_t)open(pack_path, O_RDONLY), 0, 0);
  }
  if (strstr(name, "Object") && strstr(m, ")Ljava/lang/String;")) {
    static const char *const paths[] = { "GetFilePath ", "GetDownPath ", "getAbsolutePath ", "getMediaPath ", "getPatchPath " , "getPatchPath_Completed " };
    for (unsigned i = 0; i < sizeof paths / sizeof *paths; i++) if (!strncmp(m, paths[i], strlen(paths[i]))) return jstr(FILES_DIR);
    if (!strncmp(m, "getBundleVersion ", 17)) return jstr("1.4.0");
    if (!strncasecmp(m, "getPackageName ", 15)) return jstr(PKG_NAME);
    if (!strncmp(m, "getApkFilePath ", 15)) return jstr(pack_path);
    if (!strncmp(m, "GetDeviceVersion ", 17)) return jstr("4.4.2");
    if (!strncasecmp(m, "getDeviceModel ", 15)) return jstr("KnS64-harness");
    if (!strncmp(m, "SetDeviceMacAddress ", 20)) return jstr("02:00:00:00:00:00");
    return jstr("");
  }
  if (strstr(name, "Long") && !strncmp(m, "GetCurrentTimeStamp ", 20)) {
    struct timespec ts; clock_gettime(CLOCK_REALTIME, &ts);
    uint64_t ms = (uint64_t)ts.tv_sec * 1000 + (uint64_t)ts.tv_nsec / 1000000;
    c->r[1] = (uint32_t)(ms >> 32);
    return (uint32_t)ms;
  }
  return 0;
}

static void fake_env(CPU *c, int slot, const char *name) {
  jni_calls++;
  static int all = -1;
  if (all < 0) all = getenv("KNS64_JNIALL") != NULL;
  if (all) rt_log("JNI> %s(%x, %x, %x, %x) lr=%08x", name, ARG(c, 1), ARG(c, 2), ARG(c, 3), ARG(c, 4), c->r[14]);
  uint32_t a1 = ARG(c, 1), a2 = ARG(c, 2), a3 = ARG(c, 3), a4 = ARG(c, 4);
  uint32_t r = 0;
  if (!strcmp(name, "FindClass")) { r = new_obj(GS(a1), 0, 0, 0); LOG("JNI FindClass(%s) -> %x", GS(a1), r); }
  else if (!strcmp(name, "GetObjectClass")) r = new_obj("class", 0, 0, 0);
  else if (strstr(name, "MethodID") || strstr(name, "FieldID")) { r = new_id(GS(a2), GS(a3)); LOG("JNI %s(%s %s) -> %x", name, GS(a2), GS(a3), r); }
  else if (!strcmp(name, "RegisterNatives")) { LOG("JNI RegisterNatives(%u methods)", a3); }
  else if (!strcmp(name, "GetJavaVM")) G32(a1) = jni_guest_vm();
  else if (!strcmp(name, "GetVersion")) r = 0x00010006;
  else if (!strcmp(name, "NewStringUTF")) { r = new_obj("String", guest_strdup(a1 ? GS(a1) : ""), 0, 1); LOG("JNI NewStringUTF(\"%.80s\") -> %x", a1 ? GS(a1) : "", r); }
  else if (!strcmp(name, "GetStringUTFChars")) { if (a2) G32(a2) = 0; r = obj(a1)->buf ? obj(a1)->buf : guest_strdup(""); }
  else if (!strcmp(name, "GetStringUTFLength") || !strcmp(name, "GetStringLength")) r = obj(a1)->buf ? (uint32_t)strlen(GS(obj(a1)->buf)) : 0;
  else if (!strcmp(name, "NewGlobalRef") || !strcmp(name, "NewLocalRef") || !strcmp(name, "NewWeakGlobalRef")) r = a1;
  else if (!strncmp(name, "New", 3) && strstr(name, "Array")) {
    uint32_t es = strstr(name, "Byte") || strstr(name, "Boolean") ? 1 : strstr(name, "Char") || strstr(name, "Short") ? 2 :
                  strstr(name, "Long") || strstr(name, "Double") ? 8 : 4;
    r = new_obj(name + 3, guest_calloc(a1 * es + 8), a1, es);
  }
  else if (!strcmp(name, "GetArrayLength")) r = obj(a1)->len;
  else if ((!strncmp(name, "Get", 3) && strstr(name, "ArrayElements")) || !strcmp(name, "GetPrimitiveArrayCritical") ||
           !strcmp(name, "GetDirectBufferAddress")) { if (a2 && strcmp(name, "GetDirectBufferAddress")) G32(a2) = 0; r = obj(a1)->buf; }
  else if (!strncmp(name, "Get", 3) && strstr(name, "ArrayRegion")) memcpy(G(a4), G(obj(a1)->buf + a2 * obj(a1)->esize), a3 * obj(a1)->esize);
  else if (!strncmp(name, "Set", 3) && strstr(name, "ArrayRegion")) memcpy(G(obj(a1)->buf + a2 * obj(a1)->esize), G(a4), a3 * obj(a1)->esize);
  else if (!strcmp(name, "GetIntField") && !strcmp(obj(a1)->name, "FileDescriptor")) r = obj(a1)->buf;
  else if (!strcmp(name, "GetIntField") || !strcmp(name, "GetBooleanField") || !strcmp(name, "GetObjectField")) r = *field(a1, a2);
  else if (!strcmp(name, "SetIntField") || !strcmp(name, "SetBooleanField") || !strcmp(name, "SetObjectField")) *field(a1, a2) = a3;
  else if (!strcmp(name, "ExceptionCheck") || !strcmp(name, "ExceptionOccurred") || strstr(name, "Release") ||
           !strcmp(name, "DeleteLocalRef") || !strcmp(name, "DeleteGlobalRef") || !strcmp(name, "ExceptionClear")) r = 0;
  else if (strstr(name, "Call")) r = fake_call(c, name, id_name(a2));
  else LOG("JNI %s(%x, %x, %x) -> 0 (unhandled)", name, a1, a2, a3);
  c->r[0] = r;
  (void)slot;
}

static void fake_vm(CPU *c, int slot, const char *name) {
  if (slot == 4 || slot == 6 || slot == 7) G32(ARG(c, 1)) = jni_guest_env();   /* Attach / GetEnv */
  c->r[0] = 0;
  (void)name;
}

static uint32_t call_native(CPU *c, const char *name, int nargs, ...) {
  uint32_t addr = guest_export(name);
  if (!addr) { rt_log("no export %s", name); return 0; }
  uint32_t a[12] = { jni_guest_env() };
  va_list ap;
  va_start(ap, nargs);
  for (int i = 0; i < nargs && i < 11; i++) a[1 + i] = va_arg(ap, uint32_t);
  va_end(ap);
  int before = jni_calls;
  uint32_t r = guest_call(c, addr, 1 + nargs, a, NULL);
  rt_log("%s = %u (%d JNI calls)", name + 5, r, jni_calls - before);
  return r;
}

static uint32_t wrapper(CPU *c, const char *cls) {
  uint32_t h = new_obj(cls, 0, 0, 0);
  call_native(c, K "CWrapper_nativeInitialize", 2, h, new_obj("String", guest_strdup(cls), 0, 1));
  return h;
}

/* ------------------------------------------------------------------ EGL pbuffer */
static int egl_up(void) {
  EGLDisplay d = eglGetDisplay(EGL_DEFAULT_DISPLAY);
  if (!eglInitialize(d, NULL, NULL)) return -1;
  const EGLint cfg_attr[] = { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES_BIT,
                              EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_DEPTH_SIZE, 16, EGL_NONE };
  EGLConfig cfg; EGLint n;
  if (!eglChooseConfig(d, cfg_attr, &cfg, 1, &n) || n < 1) return -2;
  const EGLint pb[] = { EGL_WIDTH, SCREEN_W, EGL_HEIGHT, SCREEN_H, EGL_NONE };
  EGLSurface s = eglCreatePbufferSurface(d, cfg, pb);
  const EGLint ctx_attr[] = { EGL_CONTEXT_CLIENT_VERSION, 1, EGL_NONE };
  EGLContext ctx = eglCreateContext(d, cfg, EGL_NO_CONTEXT, ctx_attr);
  if (s == EGL_NO_SURFACE || ctx == EGL_NO_CONTEXT || !eglMakeCurrent(d, s, s, ctx)) return -3;
  rt_log("EGL pbuffer %dx%d GLES1: %s / %s", SCREEN_W, SCREEN_H, glGetString(GL_RENDERER), glGetString(GL_VERSION));
  return 0;
}

static void save_ppm(const char *path) {
  static uint8_t px[SCREEN_W * SCREEN_H * 4];
  glReadPixels(0, 0, SCREEN_W, SCREEN_H, GL_RGBA, GL_UNSIGNED_BYTE, px);
  FILE *f = fopen(path, "wb");
  if (!f) return;
  fprintf(f, "P6\n%d %d\n255\n", SCREEN_W, SCREEN_H);
  unsigned long sum = 0;
  for (int y = SCREEN_H - 1; y >= 0; y--)
    for (int x = 0; x < SCREEN_W; x++) { fwrite(px + 4 * (y * SCREEN_W + x), 1, 3, f); sum += px[4 * (y * SCREEN_W + x)] + px[4 * (y * SCREEN_W + x) + 1]; }
  fclose(f);
  rt_log("saved %s (pixel sum %lu)", path, sum);
}

/* KNS64_WATCH=1: print where the main guest thread is every 2 s (pc/lr as of its last call) */
static CPU *watched;
static void *watch_main(void *p) {
  (void)p;
  for (;;) {
    sleep(2);
    if (watched) rt_log("watch: pc=%08x lr=%08x sp=%08x depth=%d r0=%08x", watched->pc, watched->r[14], watched->r[13], watched->depth, watched->r[0]);
  }
  return NULL;
}

int main(int argc, char **argv) {
  rt_log_stderr = 1;
  quiet = getenv("KNS64_QUIET") != NULL;
  int frames = argc > 1 ? atoi(argv[1]) : 0;
  const char *out = argc > 2 ? argv[2] : "/data/local/tmp/kns64_frame.ppm";
  if (guest_boot()) return 1;
  rt_log("guest memory at %p, image %u bytes, %u blocks", (void *)guest_mem, GUEST_IMAGE_SIZE, guest_block_count);
  CPU *c = rt_cpu_for_thread();
  if (getenv("KNS64_WATCH")) { pthread_t w; watched = c; pthread_create(&w, NULL, watch_main, NULL); }
  jni_backend = fake_env;
  jni_vm_backend = fake_vm;
  uint32_t vm = jni_guest_init();
  guest_run_init_array(c);
  uint32_t args[2] = { vm, 0 };
  uint32_t ver = guest_call(c, guest_export("JNI_OnLoad"), 2, args, NULL);
  rt_log("JNI_OnLoad returned %08x", ver);
  if (frames <= 0) return 0;
  pack_load("/data/local/tmp/game_res.idx");

  int egl = egl_up();
  if (egl) rt_log("EGL setup failed (%d): GL calls run without a context", egl);
  /* browser/port/java_shim.js startup() */
  call_native(c, K "CWrapper_nativeSetClass", 1, new_obj("com/com2us/wrapper/kernel/CWrapper", 0, 0, 0));
  call_native(c, "Java_com_com2us_wrapper_common_CCommonAPI_nativeInitialize", 1, new_obj("CCommonAPI", 0, 0, 0));
  wrapper(c, "com.com2us.wrapper.function.CFunctionTaskRunner");
  call_native(c, K "CWrapperData_nativeInitialize", 2, new_obj("CWrapperData", 0, 0, 0), 0);
  const char *wrappers[] = { "com.com2us.wrapper.kernel.CEventHandler", "com.com2us.wrapper.game.CGameManager",
    "com.com2us.wrapper.sensor.CSensor", "com.com2us.wrapper.ui.CUserInput", "com.com2us.wrapper.ui.CTextInput",
    "com.com2us.wrapper.media.CSoundManager", "com.com2us.wrapper.network.CHttpManager", "com.com2us.wrapper.utility.CUtility",
    "com.com2us.wrapper.font.CFont", PKG_NAME ".CUserDefined" };
  for (unsigned i = 0; i < sizeof wrappers / sizeof *wrappers; i++) wrapper(c, wrappers[i]);
  call_native(c, "Java_com_com2us_module_activeuser_ActiveUser_jniInitialize", 2, new_obj("ActiveUser", 0, 0, 0), new_obj("UserAgreeManager", 0, 0, 0));
  call_native(c, "Java_com_com2us_module_inapp_InApp_jniInAppInitialize", 2, new_obj("InApp", 0, 0, 0), new_obj("InApp", 0, 0, 0));
  call_native(c, "Java_com_com2us_module_mercury_Mercury_nativeMercuryInitialize", 1, new_obj("Mercury", 0, 0, 0));
  call_native(c, "Java_com_com2us_module_push_Push_jniPushInitialize", 2, new_obj("Push", 0, 0, 0), new_obj("Push", 0, 0, 0));
  call_native(c, "Java_com_com2us_module_offerwall_Offerwall_nativeOfferwallInitialize", 1, new_obj("Offerwall", 0, 0, 0));
  call_native(c, "Java_com_com2us_peppermint_HubBridge_callCSHubInitialize", 1, new_obj("HubBridge", 0, 0, 0));
  uint32_t ev = new_obj("EventData", 0, 0, 0), buf = new_obj("ByteBuffer", guest_calloc(0x800), 0x800, 1);
  call_native(c, K "CEventHandler_nativeEventHandlerInitialize", 5, new_obj("CEventHandler", 0, 0, 0), ev, buf, 8, 256);
  uint32_t pixels = new_obj("int[]", guest_calloc(FONT_SIZE * FONT_SIZE * 4), FONT_SIZE * FONT_SIZE, 4);
  call_native(c, "Java_com_com2us_wrapper_font_CFont_nativeFontInitialize", 4, new_obj("CFont", 0, 0, 0), pixels, FONT_SIZE, FONT_SIZE, 1);
  uint32_t data = new_obj("CWrapperData", 0, 0, 0);
  call_native(c, K "CWrapperData_nativeSetScreenSize", 7, data, SCREEN_W, SCREEN_H, SCREEN_W, SCREEN_H, SCREEN_W, SCREEN_H);
  uint32_t kern = new_obj("CWrapperKernel", 0, 0, 0);
  call_native(c, K "CWrapperKernel_nativeStart", 1, kern);
  call_native(c, K "CWrapperKernel_nativeStartClet", 1, kern);
  call_native(c, K "CWrapperKernel_nativeResumeClet", 1, kern);
  call_native(c, K "CWrapperKernel_nativeResume", 1, kern);
  quiet = 1;
  struct timespec t0, t1;
  clock_gettime(CLOCK_MONOTONIC, &t0);
  for (int i = 0; i < frames; i++) {
    uint32_t a[2] = { jni_guest_env(), kern };
    guest_call(c, guest_export(K "CWrapperKernel_nativeProcess"), 2, a, NULL);
    usleep(16000);
  }
  clock_gettime(CLOCK_MONOTONIC, &t1);
  extern unsigned long gl_draw_calls, gl_clear_calls;
  rt_log("%d nativeProcess frames in %.1f ms (incl. 16 ms sleeps), %d JNI calls, GL: %lu clears, %lu draws", frames,
         (t1.tv_sec - t0.tv_sec) * 1e3 + (t1.tv_nsec - t0.tv_nsec) / 1e6, jni_calls, gl_clear_calls, gl_draw_calls);
  if (!egl) save_ppm(out);
  return 0;
}
