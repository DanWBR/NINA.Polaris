#include <jni.h>
#include <stdlib.h>
#include <memory.h>
#include <android/log.h>
#include "toupcam.h"

#define  LOGE(...)  __android_log_print(ANDROID_LOG_ERROR, "JNI_CAM", __VA_ARGS__)

jobject gObject = nullptr;
JavaVM *gLocalVm = nullptr;
HToupcam gHcam = nullptr;

static void ucamCallback(unsigned nEvent, void *pCallbackCtx) {
    bool isAttached = false;
    JNIEnv *env = nullptr;
    if (gLocalVm->GetEnv((void**)&env, JNI_VERSION_1_2) < 0) {
        if (gLocalVm->AttachCurrentThread(&env, nullptr) < 0)
            return;
        isAttached = true;
    }
    jmethodID onEvent = env->GetMethodID(env->GetObjectClass(gObject), "OnEvent", "(I)V");
    if (onEvent != nullptr)
        env->CallVoidMethod(gObject, onEvent, nEvent);

    if (isAttached)
        gLocalVm->DetachCurrentThread();
}

extern "C" {
JNIEXPORT void JNICALL Java_com_droid_usbcam_CamLib_init(JNIEnv *env, jobject obj);
JNIEXPORT jstring JNICALL Java_com_droid_usbcam_CamLib_getModelName(JNIEnv *env, jobject obj, jint vendorId, jint productId);
JNIEXPORT jint JNICALL Java_com_droid_usbcam_CamLib_openDevice(JNIEnv *env, jobject obj, jint vendorId, jint productId, jint fd);
JNIEXPORT jintArray JNICALL Java_com_droid_usbcam_CamLib_getPreviewSize(JNIEnv *env, jobject obj);
JNIEXPORT void JNICALL Java_com_droid_usbcam_CamLib_pullImage(JNIEnv *env, jobject obj, jobject directBuffer);
JNIEXPORT jboolean JNICALL Java_com_droid_usbcam_CamLib_isAlive(JNIEnv *env, jobject obj);
JNIEXPORT void JNICALL Java_com_droid_usbcam_CamLib_releaseCamera(JNIEnv *env, jobject obj);

JNIEXPORT void JNICALL Java_com_droid_usbcam_CamLib_init(JNIEnv *env, jobject obj) {
    gObject = env->NewGlobalRef(obj);
}

JNIEXPORT jint JNICALL Java_com_droid_usbcam_CamLib_openDevice(JNIEnv *env, jobject obj, jint vendorId, jint productId, jint fd) {
    if (gHcam) {
        Toupcam_Close(gHcam);
        gHcam = nullptr;
    }
    char camId[128] = {0};
    sprintf(camId, "fd-%d-%04x-%04x", fd, vendorId, productId);
    gHcam = Toupcam_Open(camId);
    if (gHcam) {
        int ret = Toupcam_StartPullModeWithCallback(gHcam, ucamCallback, nullptr);
        if (!SUCCEEDED(ret)) {
            Toupcam_Close(gHcam);
            gHcam = nullptr;
            LOGE("%s: StartPullModeWithCallback failed with ret = %d\n", __FUNCTION__, ret);
            return JNI_ERR;
        }
        return JNI_OK;
    } else {
        LOGE("%s: Open failed!\n", __FUNCTION__);
    }
    return JNI_ERR;
}

JNIEXPORT jintArray JNICALL Java_com_droid_usbcam_CamLib_getPreviewSize(JNIEnv *env, jobject obj) {
    int size[2] = { 0 };
    if (gHcam)
        Toupcam_get_Size(gHcam, size, size + 1);
    jintArray array = env->NewIntArray(2);
    env->SetIntArrayRegion(array, 0, 2, size);
    return array;
}

JNIEXPORT void JNICALL Java_com_droid_usbcam_CamLib_pullImage(JNIEnv *env, jobject obj, jobject directBuffer) {
    if (gHcam) {
        void* buffer = env->GetDirectBufferAddress(directBuffer);
        Toupcam_PullImageV2(gHcam, buffer, 24, nullptr);
    }
}

JNIEXPORT jboolean JNICALL Java_com_droid_usbcam_CamLib_isAlive(JNIEnv *env, jobject obj) {
    return (gHcam != nullptr);
}

JNIEXPORT void JNICALL Java_com_droid_usbcam_CamLib_releaseCamera(JNIEnv *env, jobject obj) {
    if (gHcam) {
        Toupcam_Close(gHcam);
        gHcam = nullptr;
    }
}

JNIEXPORT jstring JNICALL Java_com_droid_usbcam_CamLib_getModelName(JNIEnv *env, jobject obj, jint vendorId, jint productId) {
    const ToupcamModelV2 *pModel = Toupcam_get_Model(vendorId, productId);
    if (nullptr == pModel)
        return nullptr;
    return env->NewStringUTF(pModel->name);
}

jint JNI_OnLoad(JavaVM *vm, void *reserved) {
    gLocalVm = vm;
    return JNI_VERSION_1_2;
}

void JNI_OnUnload(JavaVM *vm, void *reserved) {
    bool isAttached = false;
    JNIEnv *env = nullptr;
    if (vm->GetEnv((void**)&env, JNI_VERSION_1_2) < 0) {
        if (vm->AttachCurrentThread(&env, nullptr) < 0)
            return;
        isAttached = true;
    }
    env->DeleteGlobalRef(gObject);
    if (isAttached)
        vm->DetachCurrentThread();
}
}

