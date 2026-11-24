/* Guest-side JavaVM and JNIEnv: 32-bit function tables whose slots are host traps. A backend
 * (the harness's fake Java, or jni_bridge.c on Android) implements the calls. Same slot order
 * as JNINativeInterface / JNIInvokeInterface and apk-cpu-engine/jni.js. */
#include "rt.h"

/* JNINativeInterface slots, in table order (same list as apk-cpu-engine/jni.js) */
const char *const jni_env_names[] = {
  "reserved0", "reserved1", "reserved2", "reserved3",
  "GetVersion", "DefineClass", "FindClass", "FromReflectedMethod",
  "FromReflectedField", "ToReflectedMethod", "GetSuperclass", "IsAssignableFrom",
  "ToReflectedField", "Throw", "ThrowNew", "ExceptionOccurred",
  "ExceptionDescribe", "ExceptionClear", "FatalError", "PushLocalFrame",
  "PopLocalFrame", "NewGlobalRef", "DeleteGlobalRef", "DeleteLocalRef",
  "IsSameObject", "NewLocalRef", "EnsureLocalCapacity", "AllocObject",
  "NewObject", "NewObjectV", "NewObjectA", "GetObjectClass",
  "IsInstanceOf", "GetMethodID", "CallObjectMethod", "CallObjectMethodV",
  "CallObjectMethodA", "CallBooleanMethod", "CallBooleanMethodV", "CallBooleanMethodA",
  "CallByteMethod", "CallByteMethodV", "CallByteMethodA", "CallCharMethod",
  "CallCharMethodV", "CallCharMethodA", "CallShortMethod", "CallShortMethodV",
  "CallShortMethodA", "CallIntMethod", "CallIntMethodV", "CallIntMethodA",
  "CallLongMethod", "CallLongMethodV", "CallLongMethodA", "CallFloatMethod",
  "CallFloatMethodV", "CallFloatMethodA", "CallDoubleMethod", "CallDoubleMethodV",
  "CallDoubleMethodA", "CallVoidMethod", "CallVoidMethodV", "CallVoidMethodA",
  "CallNonvirtualObjectMethod", "CallNonvirtualObjectMethodV", "CallNonvirtualObjectMethodA", "CallNonvirtualBooleanMethod",
  "CallNonvirtualBooleanMethodV", "CallNonvirtualBooleanMethodA", "CallNonvirtualByteMethod", "CallNonvirtualByteMethodV",
  "CallNonvirtualByteMethodA", "CallNonvirtualCharMethod", "CallNonvirtualCharMethodV", "CallNonvirtualCharMethodA",
  "CallNonvirtualShortMethod", "CallNonvirtualShortMethodV", "CallNonvirtualShortMethodA", "CallNonvirtualIntMethod",
  "CallNonvirtualIntMethodV", "CallNonvirtualIntMethodA", "CallNonvirtualLongMethod", "CallNonvirtualLongMethodV",
  "CallNonvirtualLongMethodA", "CallNonvirtualFloatMethod", "CallNonvirtualFloatMethodV", "CallNonvirtualFloatMethodA",
  "CallNonvirtualDoubleMethod", "CallNonvirtualDoubleMethodV", "CallNonvirtualDoubleMethodA", "CallNonvirtualVoidMethod",
  "CallNonvirtualVoidMethodV", "CallNonvirtualVoidMethodA", "GetFieldID", "GetObjectField",
  "GetBooleanField", "GetByteField", "GetCharField", "GetShortField",
  "GetIntField", "GetLongField", "GetFloatField", "GetDoubleField",
  "SetObjectField", "SetBooleanField", "SetByteField", "SetCharField",
  "SetShortField", "SetIntField", "SetLongField", "SetFloatField",
  "SetDoubleField", "GetStaticMethodID", "CallStaticObjectMethod", "CallStaticObjectMethodV",
  "CallStaticObjectMethodA", "CallStaticBooleanMethod", "CallStaticBooleanMethodV", "CallStaticBooleanMethodA",
  "CallStaticByteMethod", "CallStaticByteMethodV", "CallStaticByteMethodA", "CallStaticCharMethod",
  "CallStaticCharMethodV", "CallStaticCharMethodA", "CallStaticShortMethod", "CallStaticShortMethodV",
  "CallStaticShortMethodA", "CallStaticIntMethod", "CallStaticIntMethodV", "CallStaticIntMethodA",
  "CallStaticLongMethod", "CallStaticLongMethodV", "CallStaticLongMethodA", "CallStaticFloatMethod",
  "CallStaticFloatMethodV", "CallStaticFloatMethodA", "CallStaticDoubleMethod", "CallStaticDoubleMethodV",
  "CallStaticDoubleMethodA", "CallStaticVoidMethod", "CallStaticVoidMethodV", "CallStaticVoidMethodA",
  "GetStaticFieldID", "GetStaticObjectField", "GetStaticBooleanField", "GetStaticByteField",
  "GetStaticCharField", "GetStaticShortField", "GetStaticIntField", "GetStaticLongField",
  "GetStaticFloatField", "GetStaticDoubleField", "SetStaticObjectField", "SetStaticBooleanField",
  "SetStaticByteField", "SetStaticCharField", "SetStaticShortField", "SetStaticIntField",
  "SetStaticLongField", "SetStaticFloatField", "SetStaticDoubleField", "NewString",
  "GetStringLength", "GetStringChars", "ReleaseStringChars", "NewStringUTF",
  "GetStringUTFLength", "GetStringUTFChars", "ReleaseStringUTFChars", "GetArrayLength",
  "NewObjectArray", "GetObjectArrayElement", "SetObjectArrayElement", "NewBooleanArray",
  "NewByteArray", "NewCharArray", "NewShortArray", "NewIntArray",
  "NewLongArray", "NewFloatArray", "NewDoubleArray", "GetBooleanArrayElements",
  "GetByteArrayElements", "GetCharArrayElements", "GetShortArrayElements", "GetIntArrayElements",
  "GetLongArrayElements", "GetFloatArrayElements", "GetDoubleArrayElements", "ReleaseBooleanArrayElements",
  "ReleaseByteArrayElements", "ReleaseCharArrayElements", "ReleaseShortArrayElements", "ReleaseIntArrayElements",
  "ReleaseLongArrayElements", "ReleaseFloatArrayElements", "ReleaseDoubleArrayElements", "GetBooleanArrayRegion",
  "GetByteArrayRegion", "GetCharArrayRegion", "GetShortArrayRegion", "GetIntArrayRegion",
  "GetLongArrayRegion", "GetFloatArrayRegion", "GetDoubleArrayRegion", "SetBooleanArrayRegion",
  "SetByteArrayRegion", "SetCharArrayRegion", "SetShortArrayRegion", "SetIntArrayRegion",
  "SetLongArrayRegion", "SetFloatArrayRegion", "SetDoubleArrayRegion", "RegisterNatives",
  "UnregisterNatives", "MonitorEnter", "MonitorExit", "GetJavaVM",
  "GetStringRegion", "GetStringUTFRegion", "GetPrimitiveArrayCritical", "ReleasePrimitiveArrayCritical",
  "GetStringCritical", "ReleaseStringCritical", "NewWeakGlobalRef", "DeleteWeakGlobalRef",
  "ExceptionCheck", "NewDirectByteBuffer", "GetDirectBufferAddress", "GetDirectBufferCapacity",
  "GetObjectRefType",
};
const int jni_env_count = 233;

static const char *const vm_names[] = {
  "reserved0", "reserved1", "reserved2", "DestroyJavaVM", "AttachCurrentThread", "DetachCurrentThread",
  "GetEnv", "AttachCurrentThreadAsDaemon",
};

JniBackend jni_backend;
JniBackend jni_vm_backend;
static uint32_t g_env, g_vm;

static void env_trap(CPU *c, int slot) {
  if (!jni_backend) rt_fatal(c, "JNI %s has no backend", jni_env_names[slot]);
  jni_backend(c, slot, jni_env_names[slot]);
}
static void vm_trap(CPU *c, int slot) {
  if (!jni_vm_backend) rt_fatal(c, "JavaVM %s has no backend", vm_names[slot]);
  jni_vm_backend(c, slot, vm_names[slot]);
}

uint32_t jni_guest_init(void) {
  if (g_vm) return g_vm;
  uint32_t tab = guest_calloc(4 * (uint32_t)jni_env_count);
  for (int i = 0; i < jni_env_count; i++) G32(tab + 4 * i) = host_trap_add(jni_env_names[i], env_trap, i);
  g_env = guest_calloc(16);
  G32(g_env) = tab;
  uint32_t vtab = guest_calloc(4 * 8);
  for (int i = 0; i < 8; i++) G32(vtab + 4 * i) = host_trap_add(vm_names[i], vm_trap, i);
  g_vm = guest_calloc(16);
  G32(g_vm) = vtab;
  return g_vm;
}

uint32_t jni_guest_env(void) { return g_env; }
uint32_t jni_guest_vm(void) { return g_vm; }
