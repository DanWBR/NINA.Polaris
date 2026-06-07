package com.droid.usbcam;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.hardware.usb.UsbDevice;
import android.os.Bundle;
import android.os.Handler;
import android.os.Message;
import android.view.LayoutInflater;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.AdapterView;
import android.widget.BaseAdapter;
import android.widget.GridView;
import android.widget.TextView;
import java.lang.ref.WeakReference;
import java.util.ArrayList;

public class CamListActivity extends Activity {
    private final static int MSG_UPDATEITEM = 1;
    private CamListAdapter mAdapter;
    private final ArrayList<UsbDevice> mData = new ArrayList<>();
    public static USBMonitor mUSBMonitor = null;
    private Handler mCameraHandler;

    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON, WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setContentView(R.layout.lyt_cameralist);
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_TRANSLUCENT_STATUS);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
        getWindow().setStatusBarColor(getResources().getColor(R.color.theme_background));

        GridView camList = findViewById(R.id.gv_camlist);
        mAdapter = new CamListAdapter(CamListActivity.this, mData);
        camList.setAdapter(mAdapter);
        camList.setOnItemClickListener(new AdapterView.OnItemClickListener() {
            @Override
            public void onItemClick(AdapterView<?> parent, View view, int position, long id) {
                UsbDevice usbDevice = mData.get(position);
                if (usbDevice != null) {
                    Intent intent = new Intent(CamListActivity.this, MainActivity.class);
                    intent.putExtra("UsbDevice", usbDevice);
                    startActivity(intent);
                }
            }
        });
        CamLib.getInstance().init();

        mUSBMonitor = new USBMonitor(getApplicationContext(), new USBMonitor.OnDeviceListener() {
            @Override
            public void onAttach(UsbDevice device) {
                mData.clear();
                mData.add(device);
                Message msg = new Message();
                msg.what = MSG_UPDATEITEM;
                mCameraHandler.sendMessage(msg);
            }

            @Override
            public void onDetach(UsbDevice device) {
                mData.clear();
                mData.add(null);
                Message msg = new Message();
                msg.what = MSG_UPDATEITEM;
                mCameraHandler.sendMessage(msg);
            }
        });
        mCameraHandler = new CamHandler(this);
        registerUSB();

        if (mData.isEmpty())
            mData.add(null);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        unregisterUSB();
        release();
    }

    public void registerUSB() {
        if (mUSBMonitor != null) {
            mUSBMonitor.register();
        }
    }

    public void unregisterUSB() {
        if (mUSBMonitor != null) {
            mUSBMonitor.unregister();
        }
    }

    public void release() {
        mCameraHandler = null;
        if (mUSBMonitor != null) {
            mUSBMonitor.destroy();
            mUSBMonitor = null;
        }
    }

    public static class CamHandler extends Handler {
        private WeakReference<CamListActivity> mOwner;

        CamHandler(CamListActivity owner) {
            mOwner = new WeakReference<>(owner);
        }

        @Override
        public void handleMessage(Message msg) {
            CamListActivity mainActivity = mOwner.get();
            if (mainActivity == null)
                return;
            if (msg.what == MSG_UPDATEITEM) {
                mainActivity.mAdapter.notifyDataSetChanged();
            }
        }
    }

    public static class CamListAdapter extends BaseAdapter {
        private final LayoutInflater mInflater;
        private final ArrayList<UsbDevice> mData;

        CamListAdapter(Context context, ArrayList<UsbDevice> data) {
            mInflater = LayoutInflater.from(context);
            mData = data;
        }

        @Override
        public int getCount() {
            return mData.size();
        }

        @Override
        public Object getItem(int position) {
            return mData.get(position);
        }

        @Override
        public long getItemId(int position) {
            return position;
        }

        @Override
        public View getView(int position, View v, ViewGroup parent) {
            ViewHolder holder;
            if (v == null) {
                v = mInflater.inflate(R.layout.camera_items, null);
                holder = new ViewHolder();
                holder.text = v.findViewById(R.id.txt_camname);
                v.setTag(holder);
            } else {
                holder = (ViewHolder) v.getTag();
            }
            UsbDevice usbDevice = mData.get(position);
            if (usbDevice != null)
                holder.text.setText(CamLib.getModelName(usbDevice.getVendorId(), usbDevice.getProductId()));
            else
                holder.text.setText("No Camera");
            return v;
        }

        private static class ViewHolder {
            TextView text;
        }
    }
}
