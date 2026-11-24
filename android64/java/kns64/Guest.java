package kns64;

import android.graphics.Bitmap;

import java.nio.ByteBuffer;

/**
 * Memory the 32-bit game shares with Java by pointer, for the 64-bit (translated) libgame.so.
 * build_android64.py substitutes these calls in the staged smali copy only:
 * CEventHandler ByteBuffer.allocateDirect -> allocateDirect (event ring inside guest memory),
 * CFont Bitmap.getPixels -> getPixels (copies the drawn rows into the native side's pinned
 * copy of the font buffer). Natives live in android64/runtime/jni_bridge.c.
 */
public final class Guest {
    private Guest() {}

    private static native ByteBuffer nativeAllocateDirect(int capacity);

    private static native void nativeSyncInts(int[] array, int offset, int count);

    public static ByteBuffer allocateDirect(int capacity) {
        ByteBuffer b = nativeAllocateDirect(capacity);
        return b != null ? b : ByteBuffer.allocateDirect(capacity);
    }

    public static void getPixels(Bitmap bitmap, int[] pixels, int offset, int stride, int x, int y, int width, int height) {
        bitmap.getPixels(pixels, offset, stride, x, y, width, height);
        if (width > 0 && height > 0) nativeSyncInts(pixels, offset, stride * (height - 1) + width);
    }
}
