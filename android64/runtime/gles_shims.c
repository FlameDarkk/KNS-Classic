/* OpenGL ES 1.x imports: passthrough to the device's real GLESv1_CM. Guest pointers become host
 * pointers into guest memory (client arrays stay valid: guest memory never moves). Soft-float ABI:
 * float arguments arrive as raw bits in core registers. The browser maps the same calls to WebGL
 * in apk-cpu-engine/gl.js. */
#include "rt.h"

#include <GLES/gl.h>
#include <GLES/glext.h>

#define SHIM(name) void imp_##name(CPU *c)
#define A(i) ARG(c, i)
#define I(i) ((GLint)A(i))
#define U(i) ((GLuint)A(i))
#define P(i) GP(A(i))
static inline GLfloat F(CPU *c, int i) { uint32_t u = ARG(c, i); GLfloat f; memcpy(&f, &u, 4); return f; }
#define FA(i) F(c, i)
#define RET(v) (c->r[0] = (uint32_t)(v))

/* Buffer-object offsets are not pointers: with a bound buffer, pass the guest value through. */
static GLint bound_array_buffer, bound_element_buffer;
#define PTR_OR_OFFSET(v, bound) ((bound) ? (const void *)(uintptr_t)(v) : GP(v))

unsigned long gl_draw_calls, gl_clear_calls;   /* boot/diagnostic counters */

SHIM(glActiveTexture) { glActiveTexture(U(0)); }
SHIM(glAlphaFunc) { glAlphaFunc(U(0), FA(1)); }
SHIM(glBindBuffer) {
  if (U(0) == GL_ARRAY_BUFFER) bound_array_buffer = I(1);
  else if (U(0) == GL_ELEMENT_ARRAY_BUFFER) bound_element_buffer = I(1);
  glBindBuffer(U(0), U(1));
}
SHIM(glBindTexture) { glBindTexture(U(0), U(1)); }
SHIM(glBlendFunc) { glBlendFunc(U(0), U(1)); }
SHIM(glBufferData) { glBufferData(U(0), (GLsizeiptr)(int32_t)A(1), P(2), U(3)); }
SHIM(glClear) { gl_clear_calls++; glClear(U(0)); }
SHIM(glClearColor) { glClearColor(FA(0), FA(1), FA(2), FA(3)); }
SHIM(glClearColorx) { glClearColorx(I(0), I(1), I(2), I(3)); }
SHIM(glClientActiveTexture) { glClientActiveTexture(U(0)); }
SHIM(glColor4x) { glColor4x(I(0), I(1), I(2), I(3)); }
SHIM(glColorMask) { glColorMask((GLboolean)A(0), (GLboolean)A(1), (GLboolean)A(2), (GLboolean)A(3)); }
SHIM(glColorPointer) { glColorPointer(I(0), U(1), I(2), PTR_OR_OFFSET(A(3), bound_array_buffer)); }
SHIM(glCompressedTexImage2D) { glCompressedTexImage2D(U(0), I(1), U(2), I(3), I(4), I(5), I(6), P(7)); }
SHIM(glCopyTexImage2D) { glCopyTexImage2D(U(0), I(1), U(2), I(3), I(4), I(5), I(6), I(7)); }
SHIM(glCopyTexSubImage2D) { glCopyTexSubImage2D(U(0), I(1), I(2), I(3), I(4), I(5), I(6), I(7)); }
SHIM(glDeleteBuffers) { glDeleteBuffers(I(0), P(1)); }
SHIM(glDeleteTextures) { glDeleteTextures(I(0), P(1)); }
SHIM(glDepthMask) { glDepthMask((GLboolean)A(0)); }
SHIM(glDisable) { glDisable(U(0)); }
SHIM(glDisableClientState) { glDisableClientState(U(0)); }
SHIM(glDrawArrays) { gl_draw_calls++; glDrawArrays(U(0), I(1), I(2)); }
SHIM(glDrawElements) { gl_draw_calls++; glDrawElements(U(0), I(1), U(2), PTR_OR_OFFSET(A(3), bound_element_buffer)); }
SHIM(glEnable) { glEnable(U(0)); }
SHIM(glEnableClientState) { glEnableClientState(U(0)); }
SHIM(glFinish) { glFinish(); }
SHIM(glFrustumx) { glFrustumx(I(0), I(1), I(2), I(3), I(4), I(5)); }
SHIM(glGenBuffers) { glGenBuffers(I(0), P(1)); }
SHIM(glGenTextures) { glGenTextures(I(0), P(1)); }
SHIM(glGetError) { RET(glGetError()); }
SHIM(glGetFixedv) { glGetFixedv(U(0), P(1)); }
SHIM(glGetFloatv) { glGetFloatv(U(0), P(1)); }
SHIM(glGetIntegerv) { glGetIntegerv(U(0), P(1)); }
SHIM(glGetPointerv) {
  /* host pointers are 64-bit; report the guest address when it points into guest memory */
  void *p = NULL;
  glGetPointerv(U(0), &p);
  uintptr_t v = (uintptr_t)p, base = (uintptr_t)guest_mem;
  G32(A(1)) = (v >= base && v - base < 0x100000000ull) ? (uint32_t)(v - base) : (uint32_t)v;
}
SHIM(glGetTexParameteriv) { glGetTexParameteriv(U(0), U(1), P(2)); }
SHIM(glIsEnabled) { RET(glIsEnabled(U(0))); }
SHIM(glIsTexture) { RET(glIsTexture(U(0))); }
SHIM(glLightxv) { glLightxv(U(0), U(1), P(2)); }
SHIM(glLineWidthx) { glLineWidthx(I(0)); }
SHIM(glLoadIdentity) { glLoadIdentity(); }
SHIM(glLoadMatrixf) { glLoadMatrixf(P(0)); }
SHIM(glLoadMatrixx) { glLoadMatrixx(P(0)); }
SHIM(glMatrixMode) { glMatrixMode(U(0)); }
SHIM(glMultMatrixx) { glMultMatrixx(P(0)); }
SHIM(glNormalPointer) { glNormalPointer(U(0), I(1), PTR_OR_OFFSET(A(2), bound_array_buffer)); }
SHIM(glOrthox) { glOrthox(I(0), I(1), I(2), I(3), I(4), I(5)); }
SHIM(glPointSizex) { glPointSizex(I(0)); }
SHIM(glPopMatrix) { glPopMatrix(); }
SHIM(glPushMatrix) { glPushMatrix(); }
SHIM(glRotatex) { glRotatex(I(0), I(1), I(2), I(3)); }
SHIM(glScalef) { glScalef(FA(0), FA(1), FA(2)); }
SHIM(glScalex) { glScalex(I(0), I(1), I(2)); }
SHIM(glScissor) { glScissor(I(0), I(1), I(2), I(3)); }
SHIM(glShadeModel) { glShadeModel(U(0)); }
SHIM(glStencilMask) { glStencilMask(U(0)); }
SHIM(glTexCoordPointer) { glTexCoordPointer(I(0), U(1), I(2), PTR_OR_OFFSET(A(3), bound_array_buffer)); }
SHIM(glTexEnvf) { glTexEnvf(U(0), U(1), FA(2)); }
SHIM(glTexEnvx) { glTexEnvx(U(0), U(1), I(2)); }
SHIM(glTexEnvxv) { glTexEnvxv(U(0), U(1), P(2)); }
SHIM(glTexImage2D) { glTexImage2D(U(0), I(1), I(2), I(3), I(4), I(5), U(6), U(7), P(8)); }
SHIM(glTexParameterf) { glTexParameterf(U(0), U(1), FA(2)); }
SHIM(glTexParameteri) { glTexParameteri(U(0), U(1), I(2)); }
SHIM(glTexParameterx) { glTexParameterx(U(0), U(1), I(2)); }
SHIM(glTexSubImage2D) { glTexSubImage2D(U(0), I(1), I(2), I(3), I(4), I(5), U(6), U(7), P(8)); }
SHIM(glTranslatef) { glTranslatef(FA(0), FA(1), FA(2)); }
SHIM(glTranslatex) { glTranslatex(I(0), I(1), I(2)); }
SHIM(glVertexPointer) { glVertexPointer(I(0), U(1), I(2), PTR_OR_OFFSET(A(3), bound_array_buffer)); }
SHIM(glViewport) { glViewport(I(0), I(1), I(2), I(3)); }
