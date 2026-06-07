package com.droid.usbcam;

import android.graphics.Matrix;
import android.graphics.RectF;
import android.opengl.GLES20;
import android.opengl.GLSurfaceView;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import javax.microedition.khronos.egl.EGLConfig;
import javax.microedition.khronos.opengles.GL10;

public class CamRender implements GLSurfaceView.Renderer {
	private static final int FLOAT_BYTE_LENGTH = 4;
	private CamLib mLib = null;
	private ByteBuffer mDirectBuffer = null;
	private int mCurrentBitmapWidth = 0;
	private int mCurrentBitmapHeight = 0;
	private int mCurrentWidth = 0;
	private int mCurrentHeight = 0;
	private Matrix mTransformMatrix;
	private RectF mImageBoundsRect;
	private FloatBuffer mVertexBuffer;
	private int mProgram = 0;
	private int mPositionAttributeLocation;
	private int mTexCoordAttributeLocation;
	private int mCurrentTextureId;
	private final float[] mVertices = {-1.f, -1.f, 0.f, 0.f, 1.f, 1.f, -1.f, 0.f, 1.f, 1.f, -1.f, 1.f, 0.f, 0.f, 0.f, 1.f, 1.f, 0.f, 1.f, 0.f};

	CamRender(CamLib lib) {
		mLib = lib;
		mTransformMatrix = new Matrix();
		mImageBoundsRect = new RectF(-1, -1, 1, 1);
	}

	@Override
	public void onSurfaceCreated(GL10 gl, EGLConfig config) {
		mProgram = createProgram();
		mPositionAttributeLocation = GLES20.glGetAttribLocation(mProgram, "position");
		mTexCoordAttributeLocation = GLES20.glGetAttribLocation(mProgram, "TexCoordIn");
		GLES20.glDisable(GL10.GL_CULL_FACE);
		mVertexBuffer = ByteBuffer.allocateDirect(mVertices.length * FLOAT_BYTE_LENGTH).order(ByteOrder.nativeOrder()).asFloatBuffer();
		mVertexBuffer.put(mVertices);
		mVertexBuffer.position(0);
		GenTexture();
	}

	@Override
	public void onSurfaceChanged(GL10 gl, int width, int height) {
		mCurrentWidth = width;
		mCurrentHeight = height;
		GLES20.glViewport(0, 0, width, height);
		setupRenderRegion();
	}

	@Override
	public void onDrawFrame(GL10 gl) {
		if (mCurrentTextureId == 0)
			return;
		GLES20.glClearColor(0.1490196f, 0.1490196f, 0.1490196f, 0.f);
		GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT);
		GLES20.glUseProgram(mProgram);
		GLES20.glActiveTexture(GLES20.GL_TEXTURE_2D);
		GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, mCurrentTextureId);
		GLES20.glTexImage2D(GLES20.GL_TEXTURE_2D, 0, GLES20.GL_RGB, mCurrentBitmapWidth, mCurrentBitmapHeight, 0, GLES20.GL_RGB, GLES20.GL_UNSIGNED_BYTE, mDirectBuffer);
		GLES20.glUniform1i(GLES20.glGetUniformLocation(mProgram, "bDrawWindow"), 0);
		float[] points = {mImageBoundsRect.left, mImageBoundsRect.top, mImageBoundsRect.right, mImageBoundsRect.top, mImageBoundsRect.left, mImageBoundsRect.bottom, mImageBoundsRect.right, mImageBoundsRect.bottom};
		mTransformMatrix.mapPoints(points);
		mVertices[0] = points[0];
		mVertices[1] = points[1];
		mVertices[5] = points[2];
		mVertices[6] = points[3];
		mVertices[10] = points[4];
		mVertices[11] = points[5];
		mVertices[15] = points[6];
		mVertices[16] = points[7];
		mVertexBuffer.position(0);
		mVertexBuffer.put(mVertices);
		mVertexBuffer.position(0);
		GLES20.glVertexAttribPointer(mPositionAttributeLocation, 3, GLES20.GL_FLOAT, false, 5 * FLOAT_BYTE_LENGTH, mVertexBuffer);
		mVertexBuffer.position(3);
		GLES20.glVertexAttribPointer(mTexCoordAttributeLocation, 2, GLES20.GL_FLOAT, false, 5 * FLOAT_BYTE_LENGTH, mVertexBuffer);
		GLES20.glEnableVertexAttribArray(mPositionAttributeLocation);
		GLES20.glEnableVertexAttribArray(mTexCoordAttributeLocation);
		GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4);
	}

	public void setRenderSize(int width, int height) {
		mCurrentBitmapWidth = width;
		mCurrentBitmapHeight = height;
		mDirectBuffer = ByteBuffer.allocateDirect((((width * 24 + 31) & (~31)) / 8) * height);
	}

	public void setupRenderRegion() {
		if (mCurrentBitmapHeight == 0 || mCurrentBitmapWidth == 0 || mCurrentHeight == 0 || mCurrentWidth == 0) {
			return;
		}
		float halfW = 1.f, halfH = 1.f;
		float imageAspect = 1.0f * mCurrentBitmapWidth / mCurrentBitmapHeight;
		float renderAspect = 1.0f * mCurrentWidth / mCurrentHeight;
		if (imageAspect > renderAspect) {
			halfW = Math.min(1.f, 1.0f * mCurrentBitmapWidth / mCurrentWidth);
			halfH = halfW * renderAspect / imageAspect;
		} else {
			halfH = Math.min(1.f, 1.0f * mCurrentBitmapHeight / mCurrentHeight);
			halfW = imageAspect / renderAspect;
		}
		mImageBoundsRect.set(-halfW, -halfH, halfW, halfH);
	}

	public void GenTexture() {
		int[] textures = new int[2];
		GLES20.glGenTextures(2, textures, 0);
		GLES20.glBindTexture(GLES20.GL_TEXTURE_2D, textures[0]);
		GLES20.glTexParameterf(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_NEAREST);
		GLES20.glTexParameterf(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR);
		GLES20.glTexParameterf(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE);
		GLES20.glTexParameterf(GLES20.GL_TEXTURE_2D, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE);
		mCurrentTextureId = textures[0];
	}

	private int createProgram() {
		final String mVertexShader = "attribute vec4 position; attribute vec2 TexCoordIn; varying vec2 TexCoordOut;" +
				"void main() { gl_Position = position; TexCoordOut = TexCoordIn;}";
		final String mFragmentShader = "uniform bool bDrawWindow; precision mediump float; varying vec2 TexCoordOut;" +
				" uniform vec4 vColor; uniform sampler2D texture1; void main() { " +
				" if (bDrawWindow) gl_FragColor = vec4(1,1,1,0); else gl_FragColor = texture2D(texture1, TexCoordOut); }";
		final int program = GLES20.glCreateProgram();
		final int vShader = getShader(GLES20.GL_VERTEX_SHADER, mVertexShader);
		final int fShader = getShader(GLES20.GL_FRAGMENT_SHADER, mFragmentShader);
		GLES20.glAttachShader(program, vShader);
		GLES20.glAttachShader(program, fShader);
		GLES20.glLinkProgram(program);
		GLES20.glUseProgram(program);
		return program;
	}

	private int getShader(int type, String shaderSource) {
		final int shader = GLES20.glCreateShader(type);
		GLES20.glShaderSource(shader, shaderSource);
		GLES20.glCompileShader(shader);
		int[] compiled = new int[1];
		GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, compiled, 0);
		return shader;
	}

	public void OnImage() {
		if (mDirectBuffer != null)
			mLib.pullImage(mDirectBuffer);
	}
}
