using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Silk.NET.OpenGL;
using System;
using System.Diagnostics;
using System.Numerics;

namespace stewart_platform
{
    /// <summary>
    /// OpenGL 3D viewport for the Stewart Platform visualiser.
    /// Renders base hexagon, top platform, horns and rods using Silk.NET.OpenGL
    /// via Avalonia's OpenGlControlBase — works on Windows (ANGLE/ES) and Linux/Pi (Desktop GL).
    /// </summary>
    public class PlatformView3D : OpenGlControlBase
    {
        private GL? _gl;
        private uint _vao;
        private uint _vbo;
        private uint _shaderProgram;
        private int _mvpLocation;
        private int _colorLocation;

        // Link the math engine here before the first frame
        public StewartPlatform? Platform { get; set; }

        // -------------------------------------------------------------------------
        // Cross-platform shader selection
        //   Windows ANGLE  → OpenGL ES  → #version 100
        //   Linux / Pi     → Desktop GL → #version 120
        // -------------------------------------------------------------------------
        private string GetVertexShader(bool isES)
        {
            string version = isES ? "#version 100\n" : "#version 120\n";
            return version + @"
                attribute vec3 aPos;
                uniform mat4 uMVP;
                void main() {
                    gl_Position = uMVP * vec4(aPos, 1.0);
                }";
        }

        private string GetFragmentShader(bool isES)
        {
            string version = isES ? "#version 100\n" : "#version 120\n";
            string precision = isES ? "precision mediump float;\n" : "";
            return version + precision + @"
                uniform vec4 uColor;
                void main() {
                    gl_FragColor = uColor;
                }";
        }

        // -------------------------------------------------------------------------
        // OpenGL initialisation
        // -------------------------------------------------------------------------
        protected override unsafe void OnOpenGlInit(GlInterface gl)
        {
            base.OnOpenGlInit(gl);
            _gl = GL.GetApi(gl.GetProcAddress);

            _gl.ClearColor(0.13f, 0.13f, 0.13f, 1.0f);
            _gl.Enable(EnableCap.DepthTest);
            _gl.LineWidth(2.5f);

            bool isES = gl.Version != null && gl.Version.Contains("ES");

            uint vert = CompileShader(ShaderType.VertexShader, GetVertexShader(isES));
            uint frag = CompileShader(ShaderType.FragmentShader, GetFragmentShader(isES));

            _shaderProgram = _gl.CreateProgram();
            _gl.AttachShader(_shaderProgram, vert);
            _gl.AttachShader(_shaderProgram, frag);

            // Bind attribute manually — required for old GL/GLES that doesn't support
            // layout(location = 0) in the shader source
            _gl.BindAttribLocation(_shaderProgram, 0, "aPos");
            _gl.LinkProgram(_shaderProgram);

            string log = _gl.GetProgramInfoLog(_shaderProgram);
            if (!string.IsNullOrWhiteSpace(log))
                Debug.WriteLine($"[OpenGL] Link: {log}");

            _gl.DeleteShader(vert);
            _gl.DeleteShader(frag);

            _mvpLocation = _gl.GetUniformLocation(_shaderProgram, "uMVP");
            _colorLocation = _gl.GetUniformLocation(_shaderProgram, "uColor");

            _vao = _gl.GenVertexArray();
            _vbo = _gl.GenBuffer();
        }

        private unsafe uint CompileShader(ShaderType type, string src)
        {
            uint shader = _gl!.CreateShader(type);
            _gl.ShaderSource(shader, src);
            _gl.CompileShader(shader);

            string log = _gl.GetShaderInfoLog(shader);
            if (!string.IsNullOrWhiteSpace(log))
                Debug.WriteLine($"[OpenGL] Shader ({type}): {log}");

            return shader;
        }

        // -------------------------------------------------------------------------
        // Cleanup
        // -------------------------------------------------------------------------
        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            _gl?.DeleteVertexArray(_vao);
            _gl?.DeleteBuffer(_vbo);
            _gl?.DeleteProgram(_shaderProgram);
            _gl?.Dispose();
            base.OnOpenGlDeinit(gl);
        }

        // -------------------------------------------------------------------------
        // Render loop
        // -------------------------------------------------------------------------
        protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (_gl == null || Bounds.Height == 0 || Bounds.Width == 0) return;

            _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
            _gl.Viewport(0, 0, (uint)Bounds.Width, (uint)Bounds.Height);

            if (Platform == null) return;

            _gl.UseProgram(_shaderProgram);

            // Camera: positioned in front-above, looking at origin
            float aspect = (float)(Bounds.Width / Bounds.Height);
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, -350, 200), Vector3.Zero, Vector3.UnitZ);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, 0.1f, 1000f);
            Matrix4x4 mvp = view * projection;

            float* mvpPtr = (float*)&mvp;
            _gl.UniformMatrix4(_mvpLocation, 1, false, mvpPtr);

            if (_vao != 0) _gl.BindVertexArray(_vao);

            // --- Base platform ring (blue) ---
            DrawLineLoop(Platform.BasePoints, new Vector4(0.2f, 0.6f, 1.0f, 1.0f));

            // --- Top platform ring (green) ---
            DrawLineLoop(Platform.PlatformPoints, new Vector4(0.2f, 1.0f, 0.2f, 1.0f));

            for (int i = 0; i < 6; i++)
            {
                // Horn: base pivot → horn tip (yellow)
                DrawLine(Platform.BasePoints[i], Platform.HornEndPoints[i],
                         new Vector4(1.0f, 1.0f, 0.0f, 1.0f));

                // Rod: horn tip → platform attach point (red)
                DrawLine(Platform.HornEndPoints[i], Platform.PlatformPoints[i],
                         new Vector4(1.0f, 0.25f, 0.25f, 1.0f));
            }

            if (_vao != 0) _gl.BindVertexArray(0);

            // Keep redrawing every frame so the visualiser stays live
            RequestNextFrameRendering();
        }

        // -------------------------------------------------------------------------
        // Draw helpers
        // -------------------------------------------------------------------------
        private unsafe void DrawLineLoop(Vector3[] points, Vector4 color)
        {
            _gl!.Uniform4(_colorLocation, color.X, color.Y, color.Z, color.W);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

            fixed (Vector3* ptr = points)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(points.Length * sizeof(Vector3)), ptr, BufferUsageARB.DynamicDraw);
            }

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
                (uint)(3 * sizeof(float)), null);
            _gl.EnableVertexAttribArray(0);
            _gl.DrawArrays(PrimitiveType.LineLoop, 0, (uint)points.Length);
        }

        private unsafe void DrawLine(Vector3 p1, Vector3 p2, Vector4 color)
        {
            _gl!.Uniform4(_colorLocation, color.X, color.Y, color.Z, color.W);
            Vector3[] line = { p1, p2 };

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (Vector3* ptr = line)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(2 * sizeof(Vector3)), ptr, BufferUsageARB.DynamicDraw);
            }

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
                (uint)(3 * sizeof(float)), null);
            _gl.EnableVertexAttribArray(0);
            _gl.DrawArrays(PrimitiveType.Lines, 0, 2);
        }

        /// <summary>Call from the UI thread to request a new frame.</summary>
        public void Redraw() => RequestNextFrameRendering();
    }
}