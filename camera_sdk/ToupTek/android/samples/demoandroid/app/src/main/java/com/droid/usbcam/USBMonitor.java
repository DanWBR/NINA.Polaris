package com.droid.usbcam;

import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbManager;
import java.util.HashMap;

public final class USBMonitor {
    private final String ACTION_USB_PERMISSION = "com.USB_PERMISSION." + hashCode();
    private final Context mContext;
    private final UsbManager mUsbManager;
    private final OnDeviceListener mOnDeviceListener;
    private PendingIntent mPermissionIntent = null;

    private final BroadcastReceiver mUsbReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(final Context context, final Intent intent) {
            final String action = intent.getAction();
            if (ACTION_USB_PERMISSION.equals(action)) {
                final UsbDevice device = intent.getParcelableExtra(UsbManager.EXTRA_DEVICE);
                if (device != null && intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false)) {
                    mOnDeviceListener.onAttach(device);
                }
            }
            else if (UsbManager.ACTION_USB_DEVICE_ATTACHED.equals(action)) {
                final UsbDevice device = intent.getParcelableExtra(UsbManager.EXTRA_DEVICE);
                if (device != null && CamLib.getModelName(device.getVendorId(), device.getProductId()) != null) {
                    if (requestPermission(device)) {
                        mOnDeviceListener.onAttach(device);
                    }
                }
            } else if (UsbManager.ACTION_USB_DEVICE_DETACHED.equals(action)) {
                final UsbDevice device = intent.getParcelableExtra(UsbManager.EXTRA_DEVICE);
                if (device != null) {
                    mOnDeviceListener.onDetach(device);
                }
            }
        }
    };

    public USBMonitor(final Context context, final OnDeviceListener listener) {
        mContext = context;
        mUsbManager = (UsbManager) context.getSystemService(Context.USB_SERVICE);
        mOnDeviceListener = listener;
    }

    public void destroy() {
        unregister();
    }

    public synchronized void register() {
        if (mPermissionIntent == null) {
            mPermissionIntent = PendingIntent.getBroadcast(mContext, 0, new Intent(ACTION_USB_PERMISSION), 0);
            final IntentFilter filter = new IntentFilter(ACTION_USB_PERMISSION);
            filter.addAction(UsbManager.ACTION_USB_DEVICE_ATTACHED);
            filter.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);
            filter.addAction(ACTION_USB_PERMISSION);
            mContext.registerReceiver(mUsbReceiver, filter);

            final HashMap<String, UsbDevice> deviceList = mUsbManager.getDeviceList();
            if (deviceList != null) {
                for (final UsbDevice device : deviceList.values()) {
                    if (CamLib.getModelName(device.getVendorId(), device.getProductId()) != null) {
                        if (requestPermission(device))
                            mOnDeviceListener.onAttach(device);
                        break;
                    }
                }
            }
        }
    }

    public synchronized void unregister() {
        if (mPermissionIntent != null) {
            try {
                mContext.unregisterReceiver(mUsbReceiver);
            } catch (final Exception ex) {
            }
            mPermissionIntent = null;
        }
    }

    public final boolean hasPermission(final UsbDevice device) {
        return mUsbManager.hasPermission(device);
    }
    public synchronized boolean requestPermission(final UsbDevice device) {
        if (mPermissionIntent != null && device != null) {
            if (mUsbManager.hasPermission(device))
                return true;
            try {
                mUsbManager.requestPermission(device, mPermissionIntent);
            } catch (final Exception ex) {
            }
        }
        return false;
    }

    public interface OnDeviceListener {
        void onAttach(UsbDevice device);
        void onDetach(UsbDevice device);
    }
}