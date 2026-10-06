using System;
using System.Runtime.InteropServices;

namespace TaskManager.Droid;

/// <summary>Reads GL_RENDERER / GL_VENDOR / GL_VERSION through a throw-away off-screen GL context.</summary>
internal static class GpuInfoEgl
{
    public sealed record Result(string? Renderer, string? Vendor, string? Version);

    // ---- EGL constants (values from the Khronos EGL 1.4 headers) ----
    private const int EGL_NONE = 0x3038;
    private const int EGL_SURFACE_TYPE = 0x3033;
    private const int EGL_PBUFFER_BIT = 0x0001;
    private const int EGL_RENDERABLE_TYPE = 0x3040;
    private const int EGL_OPENGL_ES2_BIT = 0x0004;
    private const int EGL_RED_SIZE = 0x3024;
    private const int EGL_GREEN_SIZE = 0x3023;
    private const int EGL_BLUE_SIZE = 0x3022;
    private const int EGL_WIDTH = 0x3057;
    private const int EGL_HEIGHT = 0x3056;
    private const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;

    // ---- GL constants ----
    private const uint GL_VENDOR = 0x1F00;
    private const uint GL_RENDERER = 0x1F01;
    private const uint GL_VERSION = 0x1F02;

    // ---- Native functions. EGL handles (display, config, surface, context) are opaque
    //      pointers, so they are IntPtr. EGLint is int. EGLBoolean is a 32-bit unsigned int. ----
    [DllImport("libEGL.so")] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport("libEGL.so")] private static extern uint eglInitialize(IntPtr display, out int major, out int minor);
    [DllImport("libEGL.so")] private static extern uint eglChooseConfig(IntPtr display, int[] attribList, IntPtr[] configs, int configSize, out int numConfig);
    [DllImport("libEGL.so")] private static extern IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attribList);
    [DllImport("libEGL.so")] private static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int[] attribList);
    [DllImport("libEGL.so")] private static extern uint eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [DllImport("libEGL.so")] private static extern uint eglDestroySurface(IntPtr display, IntPtr surface);
    [DllImport("libEGL.so")] private static extern uint eglDestroyContext(IntPtr display, IntPtr context);
    [DllImport("libEGL.so")] private static extern int eglGetError();
    [DllImport("libGLESv2.so")] private static extern IntPtr glGetString(uint name);

    /// <summary>Call from a background thread (a GL context is bound to the thread that makes it current).</summary>
    public static Result? Read()
    {
        IntPtr display = IntPtr.Zero, surface = IntPtr.Zero, context = IntPtr.Zero;
        try
        {
            display = eglGetDisplay(IntPtr.Zero);                        // EGL_DEFAULT_DISPLAY is 0
            if (display == IntPtr.Zero) return Fail("eglGetDisplay");

            if (eglInitialize(display, out _, out _) == 0) return Fail("eglInitialize");

            IntPtr config = ChooseConfig(display);
            if (config == IntPtr.Zero) return Fail("eglChooseConfig");

            surface = eglCreatePbufferSurface(display, config, new[] { EGL_WIDTH, 1, EGL_HEIGHT, 1, EGL_NONE });
            if (surface == IntPtr.Zero) return Fail("eglCreatePbufferSurface");

            // Version 2 means "OpenGL ES 2.0 or newer"; the driver may give us a newer one.
            context = eglCreateContext(display, config, IntPtr.Zero, new[] { EGL_CONTEXT_CLIENT_VERSION, 2, EGL_NONE });
            if (context == IntPtr.Zero) return Fail("eglCreateContext");

            if (eglMakeCurrent(display, surface, surface, context) == 0) return Fail("eglMakeCurrent");

            return new Result(GetString(GL_RENDERER), GetString(GL_VENDOR), GetString(GL_VERSION));
        }
        catch (Exception e)
        {
            Android.Util.Log.Warn("PerfMon", $"EGL probe failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
        finally
        {
            try
            {
                if (display != IntPtr.Zero)
                {
                    eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);   // un-bind from this thread
                    if (context != IntPtr.Zero) eglDestroyContext(display, context);
                    if (surface != IntPtr.Zero) eglDestroySurface(display, surface);
                }
            }
            catch { /* best effort cleanup */ }
        }
    }

    private static IntPtr ChooseConfig(IntPtr display)
    {
        int[][] attempts =
        {
            new[] { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
                    EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_NONE },
            new[] { EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT, EGL_NONE }
        };

        foreach (int[] attribs in attempts)
        {
            var configs = new IntPtr[1];
            if (eglChooseConfig(display, attribs, configs, 1, out int count) != 0 && count > 0)
                return configs[0];
        }
        return IntPtr.Zero;
    }

    private static string? GetString(uint name)
    {
        IntPtr p = glGetString(name);                 // returns a C string owned by the driver
        return p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);
    }

    private static Result? Fail(string step)
    {
        Android.Util.Log.Warn("PerfMon", $"EGL probe: {step} failed, eglGetError=0x{eglGetError():X}");
        return null;
    }
}