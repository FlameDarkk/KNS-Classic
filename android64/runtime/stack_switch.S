/* kns64_run_on_stack(void *top, void (*fn)(void *), void *arg, uintptr_t *old_sp)
 * Stores the current stack pointer in *old_sp (everything below it is free), switches to the
 * stack whose top is `top` (rounded down to 16), calls fn(arg) there and switches back.
 * jni_bridge.c uses it to run translated guest code on a large stack and JNI calls back into
 * Java on the thread's own stack. */
#if defined(__aarch64__)
  .text
  .globl kns64_run_on_stack
  .hidden kns64_run_on_stack
  .type kns64_run_on_stack, %function
  .p2align 2
kns64_run_on_stack:
  stp x29, x30, [sp, #-16]!
  mov x29, sp
  mov x9, sp
  str x9, [x3]
  and x0, x0, #0xfffffffffffffff0
  mov sp, x0
  mov x0, x2
  blr x1
  mov sp, x29
  ldp x29, x30, [sp], #16
  ret
  .size kns64_run_on_stack, . - kns64_run_on_stack
#elif defined(__x86_64__)
  .text
  .globl kns64_run_on_stack
  .hidden kns64_run_on_stack
  .type kns64_run_on_stack, @function
kns64_run_on_stack:
  pushq %rbp
  movq %rsp, %rbp
  movq %rsp, (%rcx)
  andq $-16, %rdi
  movq %rdi, %rsp
  movq %rdx, %rdi
  callq *%rsi
  movq %rbp, %rsp
  popq %rbp
  retq
  .size kns64_run_on_stack, . - kns64_run_on_stack
#else
#error "kns64_run_on_stack: unsupported architecture"
#endif
  .section .note.GNU-stack, "", %progbits
