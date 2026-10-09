using System;
using System.IO;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Dear ImGui platform and renderer backend for an OpenTK window: feeds window input into ImGui and draws
	///     its draw lists with OpenGL 4. Implements the ImGui 1.92 texture protocol (the backend creates, updates and
	///     destroys the textures ImGui asks for, e.g. the font atlas as glyphs are added).
	/// </summary>
	internal sealed unsafe class ImGuiController : IDisposable
	{
		#region PublicFields

		public ImGuiContextPtr Context { get; }

		#endregion

		#region PrivateFields

		private readonly NativeWindow window;
		private readonly IntPtr iniPath;
		private int vertexArray;
		private int vertexBuffer;
		private int indexBuffer;
		private int vertexBufferSize;
		private int indexBufferSize;

		// Base vertex and first index of each draw list in this frame's buffers.
		private (int vertex, int index)[] drawLists = new (int, int)[16];
		private int program;
		private int projectionLocation;
		private int textureLocation;
		private ImGuiMouseCursor lastCursor = (ImGuiMouseCursor)(-2);

		private const string VertexSource = @"#version 410 core
layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 UV;
layout(location = 2) in vec4 Color;
uniform mat4 ProjMtx;
out vec2 Frag_UV;
out vec4 Frag_Color;
void main()
{
	Frag_UV = UV;
	Frag_Color = Color;
	gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
}";

		private const string FragmentSource = @"#version 410 core
in vec2 Frag_UV;
in vec4 Frag_Color;
uniform sampler2D Texture;
layout(location = 0) out vec4 Out_Color;
void main()
{
	Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
}";

		#endregion

		#region Constructors

		public ImGuiController(NativeWindow window, string iniFile)
		{
			this.window = window;
			Context = ImGui.CreateContext();
			ImGui.SetCurrentContext(Context);
			ImGuizmo.SetImGuiContext(Context);

			ImGuiIOPtr io = ImGui.GetIO();
			io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
			io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.HasMouseCursors;

			Directory.CreateDirectory(Path.GetDirectoryName(iniFile));
			iniPath = Marshal.StringToHGlobalAnsi(iniFile);
			io.IniFilename = (byte*)iniPath;

			string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
			if (File.Exists(font))
			{
				io.Fonts.AddFontFromFileTTF(font, 17f);
			}
			else
			{
				io.Fonts.AddFontDefault();
			}

			CreateDeviceObjects();

			window.KeyDown += OnKeyDown;
			window.KeyUp += OnKeyUp;
			window.TextInput += OnTextInput;
			window.MouseDown += OnMouseDown;
			window.MouseUp += OnMouseUp;
			window.MouseMove += OnMouseMove;
			window.MouseWheel += OnMouseWheel;
			window.FocusedChanged += OnFocusChanged;
		}

		#endregion

		#region PublicMethods

		public void NewFrame(float deltaTime, int width, int height)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.DisplaySize = new NVector2(Math.Max(1, width), Math.Max(1, height));
			io.DisplayFramebufferScale = new NVector2(1f, 1f);
			io.DeltaTime = deltaTime > 0f ? deltaTime : 1f / 60f;
			UpdateCursor();

			ImGui.NewFrame();
			ImGuizmo.BeginFrame();
		}

		/// <summary>Ends the ImGui frame (call before rendering anything the frame's UI shows).</summary>
		public void EndFrame()
		{
			ImGui.Render();
		}

		/// <summary>Draws the ended frame into the bound framebuffer.</summary>
		public void RenderDrawData(int framebufferWidth, int framebufferHeight)
		{
			ImDrawDataPtr data = ImGui.GetDrawData();
			ImDrawData* raw = data.Handle;
			if (raw == null)
			{
				return;
			}

			if (raw->Textures != null)
			{
				for (int i = 0; i < raw->Textures->Size; i++)
				{
					UpdateTexture(raw->Textures->Data[i]);
				}
			}

			if (raw->CmdListsCount == 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
			{
				return;
			}

			GL.Enable(EnableCap.Blend);
			GL.BlendEquation(BlendEquationMode.FuncAdd);
			GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
			GL.Disable(EnableCap.CullFace);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.StencilTest);
			GL.Enable(EnableCap.ScissorTest);
			GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
			GL.Viewport(0, 0, framebufferWidth, framebufferHeight);

			float left = raw->DisplayPos.X;
			float right = raw->DisplayPos.X + raw->DisplaySize.X;
			float top = raw->DisplayPos.Y;
			float bottom = raw->DisplayPos.Y + raw->DisplaySize.Y;
			float* projection = stackalloc float[16]
			{
				2f / (right - left), 0f, 0f, 0f,
				0f, 2f / (top - bottom), 0f, 0f,
				0f, 0f, -1f, 0f,
				(right + left) / (left - right), (top + bottom) / (bottom - top), 0f, 1f
			};

			GL.UseProgram(program);
			GL.UniformMatrix4(projectionLocation, 1, false, projection);
			GL.Uniform1(textureLocation, 0);
			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
			GL.ActiveTexture(TextureUnit.Texture0);

			// All draw lists go into one fresh buffer store per frame. Orphaning (BufferData with no data) never waits
			// for the GPU to finish reading the previous frame's data; rewriting the store in place would wait for
			// it, which stalls the UI for as long as the driver holds the GPU (seconds right after a window resize).
			int totalVertexBytes = 0;
			int totalIndexBytes = 0;
			for (int n = 0; n < raw->CmdListsCount; n++)
			{
				ImDrawList* list = raw->CmdLists.Data[n];
				totalVertexBytes += list->VtxBuffer.Size * sizeof(ImDrawVert);
				totalIndexBytes += list->IdxBuffer.Size * sizeof(ushort);
			}

			vertexBufferSize = Math.Max(vertexBufferSize, totalVertexBytes);
			indexBufferSize = Math.Max(indexBufferSize, totalIndexBytes);
			GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
			if (drawLists.Length < raw->CmdListsCount)
			{
				Array.Resize(ref drawLists, raw->CmdListsCount * 2);
			}

			int vertexOffset = 0;
			int indexOffset = 0;
			for (int n = 0; n < raw->CmdListsCount; n++)
			{
				ImDrawList* list = raw->CmdLists.Data[n];
				int vertexBytes = list->VtxBuffer.Size * sizeof(ImDrawVert);
				int indexBytes = list->IdxBuffer.Size * sizeof(ushort);
				GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)vertexOffset, vertexBytes, (IntPtr)list->VtxBuffer.Data);
				GL.BufferSubData(BufferTarget.ElementArrayBuffer, (IntPtr)indexOffset, indexBytes, (IntPtr)list->IdxBuffer.Data);
				drawLists[n] = (vertexOffset / sizeof(ImDrawVert), indexOffset / sizeof(ushort));
				vertexOffset += vertexBytes;
				indexOffset += indexBytes;
			}

			NVector2 clipOffset = raw->DisplayPos;
			for (int n = 0; n < raw->CmdListsCount; n++)
			{
				ImDrawList* list = raw->CmdLists.Data[n];
				(int baseVertex, int baseIndex) = drawLists[n];

				for (int c = 0; c < list->CmdBuffer.Size; c++)
				{
					ImDrawCmd* cmd = &list->CmdBuffer.Data[c];
					if (cmd->UserCallback != null)
					{
						continue;
					}

					float clipMinX = cmd->ClipRect.X - clipOffset.X;
					float clipMinY = cmd->ClipRect.Y - clipOffset.Y;
					float clipMaxX = cmd->ClipRect.Z - clipOffset.X;
					float clipMaxY = cmd->ClipRect.W - clipOffset.Y;
					if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
					{
						continue;
					}

					GL.Scissor((int)clipMinX, (int)(framebufferHeight - clipMaxY), (int)(clipMaxX - clipMinX), (int)(clipMaxY - clipMinY));
					ImTextureID texture = new ImDrawCmdPtr(cmd).GetTexID();
					GL.BindTexture(TextureTarget.Texture2D, (int)texture.Handle);
					GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)cmd->ElemCount, DrawElementsType.UnsignedShort, (IntPtr)((baseIndex + cmd->IdxOffset) * sizeof(ushort)), baseVertex + (int)cmd->VtxOffset);
				}
			}

			// Back to the state the engine's passes expect.
			GL.Disable(EnableCap.ScissorTest);
			GL.Disable(EnableCap.Blend);
			GL.Enable(EnableCap.DepthTest);
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.UseProgram(0);
		}

		public void Dispose()
		{
			window.KeyDown -= OnKeyDown;
			window.KeyUp -= OnKeyUp;
			window.TextInput -= OnTextInput;
			window.MouseDown -= OnMouseDown;
			window.MouseUp -= OnMouseUp;
			window.MouseMove -= OnMouseMove;
			window.MouseWheel -= OnMouseWheel;
			window.FocusedChanged -= OnFocusChanged;

			ImGuiPlatformIOPtr platform = ImGui.GetPlatformIO();
			for (int i = 0; i < platform.Textures.Size; i++)
			{
				ImTextureDataPtr texture = platform.Textures.Data[i];
				if (texture.RefCount == 1 && texture.TexID.Handle != 0)
				{
					GL.DeleteTexture((int)texture.TexID.Handle);
					texture.SetTexID(ImTextureID.Null);
					texture.SetStatus(ImTextureStatus.Destroyed);
				}
			}

			GL.DeleteBuffer(vertexBuffer);
			GL.DeleteBuffer(indexBuffer);
			GL.DeleteVertexArray(vertexArray);
			GL.DeleteProgram(program);
			ImGui.DestroyContext(Context);
			Marshal.FreeHGlobal(iniPath);
		}

		#endregion

		#region PrivateMethods

		private void CreateDeviceObjects()
		{
			int vertex = CompileShader(ShaderType.VertexShader, VertexSource);
			int fragment = CompileShader(ShaderType.FragmentShader, FragmentSource);
			program = GL.CreateProgram();
			GL.AttachShader(program, vertex);
			GL.AttachShader(program, fragment);
			GL.LinkProgram(program);
			GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
			if (linked == 0)
			{
				Debug.LogError("[ImGui] Shader link failed: " + GL.GetProgramInfoLog(program));
			}

			GL.DetachShader(program, vertex);
			GL.DetachShader(program, fragment);
			GL.DeleteShader(vertex);
			GL.DeleteShader(fragment);
			projectionLocation = GL.GetUniformLocation(program, "ProjMtx");
			textureLocation = GL.GetUniformLocation(program, "Texture");

			vertexArray = GL.GenVertexArray();
			vertexBuffer = GL.GenBuffer();
			indexBuffer = GL.GenBuffer();
			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
			vertexBufferSize = 10000 * sizeof(ImDrawVert);
			indexBufferSize = 20000 * sizeof(ushort);
			GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);

			int stride = sizeof(ImDrawVert);
			GL.EnableVertexAttribArray(0);
			GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
			GL.EnableVertexAttribArray(1);
			GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
			GL.EnableVertexAttribArray(2);
			GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
		}

		private static int CompileShader(ShaderType type, string source)
		{
			int shader = GL.CreateShader(type);
			GL.ShaderSource(shader, source);
			GL.CompileShader(shader);
			GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
			if (compiled == 0)
			{
				Debug.LogError("[ImGui] Shader compile failed: " + GL.GetShaderInfoLog(shader));
			}

			return shader;
		}

		// ImGui 1.92 texture protocol: create / update / destroy what ImGui asks for.
		private static void UpdateTexture(ImTextureDataPtr texture)
		{
			switch (texture.Status)
			{
				case ImTextureStatus.WantCreate:
				{
					int handle = GL.GenTexture();
					GL.BindTexture(TextureTarget.Texture2D, handle);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
					GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)texture.GetPixels());
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
					GL.BindTexture(TextureTarget.Texture2D, 0);
					texture.SetTexID(new ImTextureID((ulong)handle));
					texture.SetStatus(ImTextureStatus.Ok);
					break;
				}
				case ImTextureStatus.WantUpdates:
				{
					ImTextureRect rect = texture.UpdateRect;
					GL.BindTexture(TextureTarget.Texture2D, (int)texture.TexID.Handle);
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, texture.Width);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
					GL.TexSubImage2D(TextureTarget.Texture2D, 0, rect.X, rect.Y, rect.W, rect.H, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)texture.GetPixelsAt(rect.X, rect.Y));
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
					GL.BindTexture(TextureTarget.Texture2D, 0);
					texture.SetStatus(ImTextureStatus.Ok);
					break;
				}
				case ImTextureStatus.WantDestroy when texture.UnusedFrames > 0:
					GL.DeleteTexture((int)texture.TexID.Handle);
					texture.SetTexID(ImTextureID.Null);
					texture.SetStatus(ImTextureStatus.Destroyed);
					break;
			}
		}

		private void UpdateCursor()
		{
			ImGuiMouseCursor cursor = ImGui.GetMouseCursor();
			if (cursor == lastCursor)
			{
				return;
			}

			lastCursor = cursor;
			switch (cursor)
			{
				case ImGuiMouseCursor.TextInput:
					window.Cursor = MouseCursor.IBeam;
					break;
				case ImGuiMouseCursor.ResizeEw:
					window.Cursor = MouseCursor.HResize;
					break;
				case ImGuiMouseCursor.ResizeNs:
					window.Cursor = MouseCursor.VResize;
					break;
				case ImGuiMouseCursor.Hand:
					window.Cursor = MouseCursor.Hand;
					break;
				default:
					window.Cursor = MouseCursor.Default;
					break;
			}
		}

		private static void OnKeyDown(KeyboardKeyEventArgs e)
		{
			UpdateModifiers(e.Modifiers);
			ImGuiKey key = MapKey(e.Key);
			if (key != ImGuiKey.None)
			{
				ImGui.GetIO().AddKeyEvent(key, true);
			}
		}

		private static void OnKeyUp(KeyboardKeyEventArgs e)
		{
			UpdateModifiers(e.Modifiers);
			ImGuiKey key = MapKey(e.Key);
			if (key != ImGuiKey.None)
			{
				ImGui.GetIO().AddKeyEvent(key, false);
			}
		}

		private static void UpdateModifiers(KeyModifiers modifiers)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddKeyEvent(ImGuiKey.ModCtrl, (modifiers & KeyModifiers.Control) != 0);
			io.AddKeyEvent(ImGuiKey.ModShift, (modifiers & KeyModifiers.Shift) != 0);
			io.AddKeyEvent(ImGuiKey.ModAlt, (modifiers & KeyModifiers.Alt) != 0);
			io.AddKeyEvent(ImGuiKey.ModSuper, (modifiers & KeyModifiers.Super) != 0);
		}

		private static void OnTextInput(TextInputEventArgs e)
		{
			ImGui.GetIO().AddInputCharacter((uint)e.Unicode);
		}

		private static void OnMouseDown(MouseButtonEventArgs e)
		{
			int button = MapMouseButton(e.Button);
			if (button >= 0)
			{
				ImGui.GetIO().AddMouseButtonEvent(button, true);
			}
		}

		private static void OnMouseUp(MouseButtonEventArgs e)
		{
			int button = MapMouseButton(e.Button);
			if (button >= 0)
			{
				ImGui.GetIO().AddMouseButtonEvent(button, false);
			}
		}

		private void OnMouseMove(MouseMoveEventArgs e)
		{
			// A grabbed cursor (game view mouse look) has no meaningful position for the UI.
			if (window.CursorState != CursorState.Grabbed)
			{
				ImGui.GetIO().AddMousePosEvent(e.X, e.Y);
			}
		}

		private static void OnMouseWheel(MouseWheelEventArgs e)
		{
			ImGui.GetIO().AddMouseWheelEvent(e.OffsetX, e.OffsetY);
		}

		private static void OnFocusChanged(FocusedChangedEventArgs e)
		{
			ImGui.GetIO().AddFocusEvent(e.IsFocused);
		}

		private static int MapMouseButton(MouseButton button)
		{
			switch (button)
			{
				case MouseButton.Left: return 0;
				case MouseButton.Right: return 1;
				case MouseButton.Middle: return 2;
				case MouseButton.Button4: return 3;
				case MouseButton.Button5: return 4;
				default: return -1;
			}
		}

		private static ImGuiKey MapKey(Keys key)
		{
			if (key >= Keys.A && key <= Keys.Z)
			{
				return ImGuiKey.A + (key - Keys.A);
			}

			if (key >= Keys.D0 && key <= Keys.D9)
			{
				return ImGuiKey.Key0 + (key - Keys.D0);
			}

			if (key >= Keys.F1 && key <= Keys.F12)
			{
				return ImGuiKey.F1 + (key - Keys.F1);
			}

			if (key >= Keys.KeyPad0 && key <= Keys.KeyPad9)
			{
				return ImGuiKey.Keypad0 + (key - Keys.KeyPad0);
			}

			switch (key)
			{
				case Keys.Tab: return ImGuiKey.Tab;
				case Keys.Left: return ImGuiKey.LeftArrow;
				case Keys.Right: return ImGuiKey.RightArrow;
				case Keys.Up: return ImGuiKey.UpArrow;
				case Keys.Down: return ImGuiKey.DownArrow;
				case Keys.PageUp: return ImGuiKey.PageUp;
				case Keys.PageDown: return ImGuiKey.PageDown;
				case Keys.Home: return ImGuiKey.Home;
				case Keys.End: return ImGuiKey.End;
				case Keys.Insert: return ImGuiKey.Insert;
				case Keys.Delete: return ImGuiKey.Delete;
				case Keys.Backspace: return ImGuiKey.Backspace;
				case Keys.Space: return ImGuiKey.Space;
				case Keys.Enter: return ImGuiKey.Enter;
				case Keys.KeyPadEnter: return ImGuiKey.KeypadEnter;
				case Keys.Escape: return ImGuiKey.Escape;
				case Keys.LeftControl: return ImGuiKey.LeftCtrl;
				case Keys.RightControl: return ImGuiKey.RightCtrl;
				case Keys.LeftShift: return ImGuiKey.LeftShift;
				case Keys.RightShift: return ImGuiKey.RightShift;
				case Keys.LeftAlt: return ImGuiKey.LeftAlt;
				case Keys.RightAlt: return ImGuiKey.RightAlt;
				case Keys.LeftSuper: return ImGuiKey.LeftSuper;
				case Keys.RightSuper: return ImGuiKey.RightSuper;
				case Keys.Menu: return ImGuiKey.Menu;
				case Keys.Apostrophe: return ImGuiKey.Apostrophe;
				case Keys.Comma: return ImGuiKey.Comma;
				case Keys.Minus: return ImGuiKey.Minus;
				case Keys.Period: return ImGuiKey.Period;
				case Keys.Slash: return ImGuiKey.Slash;
				case Keys.Semicolon: return ImGuiKey.Semicolon;
				case Keys.Equal: return ImGuiKey.Equal;
				case Keys.LeftBracket: return ImGuiKey.LeftBracket;
				case Keys.Backslash: return ImGuiKey.Backslash;
				case Keys.RightBracket: return ImGuiKey.RightBracket;
				case Keys.GraveAccent: return ImGuiKey.GraveAccent;
				case Keys.CapsLock: return ImGuiKey.CapsLock;
				case Keys.ScrollLock: return ImGuiKey.ScrollLock;
				case Keys.NumLock: return ImGuiKey.NumLock;
				case Keys.PrintScreen: return ImGuiKey.PrintScreen;
				case Keys.Pause: return ImGuiKey.Pause;
				case Keys.KeyPadDecimal: return ImGuiKey.KeypadDecimal;
				case Keys.KeyPadDivide: return ImGuiKey.KeypadDivide;
				case Keys.KeyPadMultiply: return ImGuiKey.KeypadMultiply;
				case Keys.KeyPadSubtract: return ImGuiKey.KeypadSubtract;
				case Keys.KeyPadAdd: return ImGuiKey.KeypadAdd;
				case Keys.KeyPadEqual: return ImGuiKey.KeypadEqual;
				default: return ImGuiKey.None;
			}
		}

		#endregion
	}
}
