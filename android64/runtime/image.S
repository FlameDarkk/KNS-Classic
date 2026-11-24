/* The relocated guest image written by tools/arm2c.py (path set with -DIMAGE_BIN). */
#define STR2(x) #x
#define STR(x) STR2(x)
  .section .rodata
  .global guest_image
  .balign 16
guest_image:
  .incbin STR(IMAGE_BIN)
