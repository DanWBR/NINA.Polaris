LOCAL_PATH:= $(call my-dir)
include $(CLEAR_VARS)  
LOCAL_MODULE := toupcam
LOCAL_SRC_FILES := $(TARGET_ARCH_ABI)/libtoupcam.so
include $(PREBUILT_SHARED_LIBRARY)