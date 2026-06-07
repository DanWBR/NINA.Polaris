package com.droid.usbcam;

import android.content.Context;
import android.opengl.GLSurfaceView;
import android.util.AttributeSet;
import javax.microedition.khronos.egl.EGL10;
import javax.microedition.khronos.egl.EGLConfig;
import javax.microedition.khronos.egl.EGLContext;
import javax.microedition.khronos.egl.EGLDisplay;

public class CamView extends GLSurfaceView {
	private CamRender mRender = null;

	public CamView(Context context) {
		super(context);
		init();
	}

	public CamView(Context context, AttributeSet attrs) {
		super(context, attrs);
		init();
	}

	public void setVideoSize(int width, int height) {
		if (mRender != null) {
			mRender.setRenderSize(width, height);
			mRender.setupRenderRegion();
		}
	}

	public void OnResume() {
		onResume();
		if (mRender != null)
			mRender.GenTexture();
		requestRender();
	}

	public void OnImage() {
		if (mRender != null) {
			mRender.OnImage();
			requestRender();
		}
	}

	private void init() {
		setEGLContextFactory(new ContextFactory());
		setEGLContextClientVersion(2);
		setEGLConfigChooser(new ConfigChooser(5, 6, 5, 0, 0, 0));
		if (null == mRender)
			mRender = new CamRender(CamLib.getInstance());
		setRenderer(mRender);
		setRenderMode(RENDERMODE_WHEN_DIRTY);
	}

	private static class ContextFactory implements EGLContextFactory {
		public EGLContext createContext(EGL10 egl, EGLDisplay display, EGLConfig eglConfig) {
			int[] attrib = {0x3098, 2, EGL10.EGL_NONE};
			return egl.eglCreateContext(display, eglConfig, EGL10.EGL_NO_CONTEXT, attrib);
		}

		public void destroyContext(EGL10 egl, EGLDisplay display, EGLContext context) {
			egl.eglDestroyContext(display, context);
		}
	}

	private static class ConfigChooser implements EGLConfigChooser {
		ConfigChooser(int r, int g, int b, int a, int depth, int stencil) {
			mRedSize = r;
			mGreenSize = g;
			mBlueSize = b;
			mAlphaSize = a;
			mDepthSize = depth;
			mStencilSize = stencil;
		}

		private static final int[] s_configAttribs2 = {EGL10.EGL_RED_SIZE, 4, EGL10.EGL_GREEN_SIZE, 4, EGL10.EGL_BLUE_SIZE, 4, EGL10.EGL_RENDERABLE_TYPE, 4, EGL10.EGL_NONE};

		public EGLConfig chooseConfig(EGL10 egl, EGLDisplay display) {
			int[] num_config = new int[1];
			egl.eglChooseConfig(display, s_configAttribs2, null, 0, num_config);
			int numConfigs = num_config[0];
			if (numConfigs <= 0)
				throw new IllegalArgumentException("No configs match configSpec");
			EGLConfig[] configs = new EGLConfig[numConfigs];
			egl.eglChooseConfig(display, s_configAttribs2, configs, numConfigs, num_config);
			return chooseConfig(egl, display, configs);
		}

		EGLConfig chooseConfig(EGL10 egl, EGLDisplay display, EGLConfig[] configs) {
			for (EGLConfig config : configs) {
				int d = findConfigAttrib(egl, display, config, EGL10.EGL_DEPTH_SIZE);
				int s = findConfigAttrib(egl, display, config, EGL10.EGL_STENCIL_SIZE);
				if (d < mDepthSize || s < mStencilSize)
					continue;
				int r = findConfigAttrib(egl, display, config, EGL10.EGL_RED_SIZE);
				int g = findConfigAttrib(egl, display, config, EGL10.EGL_GREEN_SIZE);
				int b = findConfigAttrib(egl, display, config, EGL10.EGL_BLUE_SIZE);
				int a = findConfigAttrib(egl, display, config, EGL10.EGL_ALPHA_SIZE);

				if (r == mRedSize && g == mGreenSize && b == mBlueSize && a == mAlphaSize)
					return config;
			}
			return null;
		}

		private int findConfigAttrib(EGL10 egl, EGLDisplay display, EGLConfig config, int attribute) {
			if (egl.eglGetConfigAttrib(display, config, attribute, mValue))
				return mValue[0];
			return 0;
		}

		int mRedSize;
		int mGreenSize;
		int mBlueSize;
		int mAlphaSize;
		int mDepthSize;
		int mStencilSize;
		private final int[] mValue = new int[1];
	}
}
