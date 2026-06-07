JNI_RTPATH := $(call my-dir)
include $(call all-subdir-makefiles)
include $(CLEAR_VARS)
LOCAL_MODULE := libjnicam
LOCAL_CFLAGS    := -Wno-narrowing -Wno-deprecated-declarations -Werror -O2 -fPIC -fvisibility=hidden
LOCAL_SRC_FILES := $(JNI_RTPATH)/jnicam.cpp
LOCAL_LDLIBS    := -llog
LOCAL_SHARED_LIBRARIES := toupcam
include $(BUILD_SHARED_LIBRARY)