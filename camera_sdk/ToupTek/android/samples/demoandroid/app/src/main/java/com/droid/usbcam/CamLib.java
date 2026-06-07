package com.droid.usbcam;

import android.os.Handler;
import android.os.Message;
import java.nio.ByteBuffer;

public class CamLib {
    public static final int MSG_EVENT = 100;
	private static CamLib sInstance;
    private static Handler localHandler = null;

    boolean OpenDevice(int vendorId, int productId, int fd) {
        openDevice(vendorId, productId, fd);
        return isAlive();
    }

    void setLocalHandler(Handler handler) {
        localHandler = handler;
    }

    static CamLib getInstance() {
        synchronized (CamLib.class) {
            if (sInstance == null)
                sInstance = new CamLib();
        }
        return sInstance;
    }

    static {
        System.loadLibrary("toupcam");
        System.loadLibrary("jnicam");
    }

    public void OnEvent(int event) {
        if (localHandler != null) {
            Message Msg = localHandler.obtainMessage();
            Msg.what = MSG_EVENT;
            Msg.arg1 = event;
            localHandler.sendMessage(Msg);
        }
    }

    public native void init();
    public native int[] getPreviewSize();
    private native int openDevice(int vendorId, int productId, int fd);
    public native void pullImage(ByteBuffer directBuffer);
    public native void releaseCamera();
    public native boolean isAlive();
    public static native String getModelName(int vendorId, int productId);
}
