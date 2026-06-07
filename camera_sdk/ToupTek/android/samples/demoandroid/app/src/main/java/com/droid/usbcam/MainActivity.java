package com.droid.usbcam;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbDeviceConnection;
import android.hardware.usb.UsbManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.Message;
import android.view.WindowManager;
import java.lang.ref.WeakReference;

public class MainActivity extends Activity {
    private static CamLib mLib = null;
    private Handler mHandler = new CamHandler(this);
    private CamView mGlView = null;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON, WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setContentView(R.layout.activity_main);
        mLib = CamLib.getInstance();
        mLib.setLocalHandler(mHandler);
        mGlView = findViewById(R.id.gl_view);
        UsbDevice dev = getIntent().getParcelableExtra("UsbDevice");
        if (dev != null) {
            UsbManager usbManager = (UsbManager)getApplicationContext().getSystemService(Context.USB_SERVICE);
            UsbDeviceConnection usbDeviceConnection = usbManager.openDevice(dev);
            if (usbDeviceConnection != null) {
                if (mLib.OpenDevice(dev.getVendorId(), dev.getProductId(), usbDeviceConnection.getFileDescriptor())) {
                    int[] size = mLib.getPreviewSize();
                    initSize(size[0], size[1]);
                }
                usbDeviceConnection.close();
            }
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (mLib.isAlive())
            mGlView.OnResume();
    }

    @Override
    protected void onPause() {
        super.onPause();
        if (mLib.isAlive())
            mGlView.onPause();
    }

    @Override
    protected void onDestroy() {
        mLib.releaseCamera();
        mHandler = null;
        super.onDestroy();
    }

    public void event(int ev)
    {
        switch (ev) {
            case 0x04:
                mGlView.OnImage();
                break;
            case 0x80:
                new AlertDialog.Builder(this)
                        .setMessage("Camera generic error.")
                        .setPositiveButton("OK", null)
                        .show();
                break;
            case 0x81:
                new AlertDialog.Builder(this)
                        .setMessage("Camera disconnect.")
                        .setPositiveButton("OK", null)
                        .show();
                break;
            default:
                break;
        }
    }

    public void initSize(int videoWidth, int videoHeight) {
        if (mGlView != null)
            mGlView.setVideoSize(videoWidth, videoHeight);
    }

    public static class CamHandler extends Handler {
        private final WeakReference<MainActivity> mOwner;

        CamHandler(MainActivity owner) {
            mOwner = new WeakReference<>(owner);
        }

        @Override
        public void handleMessage(Message msg) {
            if (CamLib.MSG_EVENT == msg.what) {
                MainActivity mainActivity = mOwner.get();
                if (mainActivity != null)
                    mainActivity.event(msg.arg1);
            }
        }
    }
}