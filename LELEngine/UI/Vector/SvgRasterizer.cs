using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Rasterizes an SVG file into an RGBA image for the UI (an .svg named by a style sheet's url() or a UXML
	///     attribute loads through here: Unity's VectorImage, but drawn once into a texture). Covers what UI art uses:
	///     rect (rounded), circle, ellipse, line, polyline, polygon and path (M L H V C S Q T A Z, absolute and relative),
	///     groups with transform (matrix, translate, scale, rotate), fill (solid, linearGradient, radialGradient with
	///     stops, objectBoundingBox or userSpaceOnUse), fill-rule, stroke with width, caps and joins, fill-, stroke- and
	///     group opacity, presentation attributes or a style attribute. Not covered: text, clipping, masks, filters,
	///     patterns, dashes, nested svg elements.
	///     Anti-aliased by exact horizontal coverage on four sub-scanlines per pixel row. The image is as large as the
	///     viewBox, scaled up to 256 pixels on the long side for small icons (at most x8) and down to 1024 for big ones.
	/// </summary>
	internal static class SvgRasterizer
	{
		#region PublicMethods

		/// <summary>The image of the SVG text; a parse error gives a 1 x 1 clear image and a warning.</summary>
		public static ImageData Rasterize(string svgText, string name = null)
		{
			try
			{
				XDocument document = XDocument.Parse(svgText);
				XElement root = document.Root;
				if (root == null || root.Name.LocalName != "svg")
				{
					throw new FormatException("no <svg> root");
				}

				var raster = new Raster(root);
				raster.Draw();
				return raster.ToImage();
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot rasterize " + (name ?? "SVG") + ": " + e.Message);
				return new ImageData(1, 1, new byte[4]);
			}
		}

		#endregion

		#region Nested

		/// <summary>A 2D affine transform: x' = a x + c y + e, y' = b x + d y + f (SVG's matrix order).</summary>
		private struct Matrix
		{
			public float A, B, C, D, E, F;

			public static Matrix Identity => new Matrix { A = 1f, D = 1f };

			public Vector2 Apply(Vector2 p)
			{
				return new Vector2(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
			}

			/// <summary>this x other: other applies first.</summary>
			public Matrix Then(Matrix other)
			{
				return new Matrix
				{
					A = A * other.A + C * other.B,
					B = B * other.A + D * other.B,
					C = A * other.C + C * other.D,
					D = B * other.C + D * other.D,
					E = A * other.E + C * other.F + E,
					F = B * other.E + D * other.F + F
				};
			}

			/// <summary>Length scale of the transform (square root of the determinant's magnitude).</summary>
			public float Scale => (float)Math.Sqrt(Math.Abs(A * D - B * C));

			public static Matrix Translation(float x, float y)
			{
				return new Matrix { A = 1f, D = 1f, E = x, F = y };
			}

			public static Matrix Scaling(float x, float y)
			{
				return new Matrix { A = x, D = y };
			}

			public static Matrix Rotation(float degrees)
			{
				double r = degrees * Math.PI / 180.0;
				var cos = (float)Math.Cos(r);
				var sin = (float)Math.Sin(r);
				return new Matrix { A = cos, B = sin, C = -sin, D = cos };
			}
		}

		private sealed class GradientStop
		{
			public float Offset;
			public Color4 Color;
		}

		private sealed class Gradient
		{
			public bool Radial;
			public bool UserSpace;
			public float X1, Y1, X2 = 1f, Y2;
			public float Cx = 0.5f, Cy = 0.5f, R = 0.5f;
			public Matrix Transform = Matrix.Identity;
			public readonly List<GradientStop> Stops = new List<GradientStop>();
			public string Href;
		}

		/// <summary>What fills a shape: a solid color or a gradient.</summary>
		private struct Paint
		{
			public bool None;
			public Color4 Color;
			public Gradient Gradient;

			public static Paint Solid(Color4 color)
			{
				return new Paint { Color = color };
			}
		}

		/// <summary>Inherited presentation state.</summary>
		private struct Style
		{
			public Paint Fill;
			public float FillOpacity;
			public Paint Stroke;
			public float StrokeWidth;
			public float StrokeOpacity;
			public int LineCap; // 0 butt, 1 round, 2 square
			public int LineJoin; // 0 miter, 1 round, 2 bevel
			public float MiterLimit;
			public bool EvenOdd;
			public float Opacity;
			public Matrix Transform;
		}

		private sealed class Edge
		{
			public float X0, Y0, X1, Y1;
			public int Winding;
		}

		private sealed class Raster
		{
			private const int SubScanlines = 4;
			private const float MinLongSide = 256f;
			private const float MaxLongSide = 1024f;
			private const float MaxUpscale = 8f;

			private readonly XElement root;
			private readonly Dictionary<string, Gradient> gradients = new Dictionary<string, Gradient>(StringComparer.Ordinal);
			private readonly int width;
			private readonly int height;
			private readonly Matrix viewTransform;
			private readonly float[] pixels; // premultiplied RGBA
			private readonly float[] coverage;
			private readonly List<Edge> edges = new List<Edge>();
			private readonly List<float> crossings = new List<float>();
			private readonly List<int> crossingWindings = new List<int>();
			private int minY, maxY, minX, maxX;

			public Raster(XElement svg)
			{
				root = svg;
				float vx = 0f, vy = 0f, vw = 0f, vh = 0f;
				string viewBox = (string)svg.Attribute("viewBox");
				if (!string.IsNullOrEmpty(viewBox))
				{
					float[] parts = Numbers(viewBox);
					if (parts.Length == 4)
					{
						vx = parts[0];
						vy = parts[1];
						vw = parts[2];
						vh = parts[3];
					}
				}

				float declaredWidth = Length((string)svg.Attribute("width"), 0f);
				float declaredHeight = Length((string)svg.Attribute("height"), 0f);
				if (vw <= 0f || vh <= 0f)
				{
					vw = declaredWidth > 0f ? declaredWidth : 64f;
					vh = declaredHeight > 0f ? declaredHeight : 64f;
				}

				float longSide = Math.Max(vw, vh);
				float scale = Math.Clamp(MinLongSide / longSide, 1f, MaxUpscale);
				if (longSide * scale > MaxLongSide)
				{
					scale = MaxLongSide / longSide;
				}

				width = Math.Max(1, (int)Math.Round(vw * scale));
				height = Math.Max(1, (int)Math.Round(vh * scale));
				viewTransform = Matrix.Scaling(width / vw, height / vh).Then(Matrix.Translation(-vx, -vy));
				pixels = new float[width * height * 4];
				coverage = new float[width * height];
			}

			public void Draw()
			{
				CollectGradients(root);
				var style = new Style
				{
					Fill = Paint.Solid(new Color4(0f, 0f, 0f, 1f)),
					FillOpacity = 1f,
					Stroke = new Paint { None = true },
					StrokeWidth = 1f,
					StrokeOpacity = 1f,
					MiterLimit = 4f,
					Opacity = 1f,
					Transform = viewTransform
				};
				DrawChildren(root, style);
			}

			public ImageData ToImage()
			{
				var bytes = new byte[width * height * 4];
				for (int i = 0; i < width * height; i++)
				{
					float a = pixels[i * 4 + 3];
					if (a <= 0f)
					{
						continue;
					}

					float inverse = 1f / a;
					bytes[i * 4] = ToByte(pixels[i * 4] * inverse);
					bytes[i * 4 + 1] = ToByte(pixels[i * 4 + 1] * inverse);
					bytes[i * 4 + 2] = ToByte(pixels[i * 4 + 2] * inverse);
					bytes[i * 4 + 3] = ToByte(a);
				}

				return new ImageData(width, height, bytes);
			}

			// ------------------------------------------------------------------ tree

			private void CollectGradients(XElement element)
			{
				foreach (XElement child in element.Elements())
				{
					string tag = child.Name.LocalName;
					if (tag == "linearGradient" || tag == "radialGradient")
					{
						string id = (string)child.Attribute("id");
						if (!string.IsNullOrEmpty(id))
						{
							gradients[id] = ParseGradient(child, tag == "radialGradient");
						}
					}

					CollectGradients(child);
				}

				// Stops inherited through href (xlink:href or href).
				foreach (Gradient gradient in gradients.Values)
				{
					Gradient source = gradient;
					int guard = 0;
					while (source.Stops.Count == 0 && !string.IsNullOrEmpty(source.Href) && guard++ < 8)
					{
						string key = source.Href.TrimStart('#');
						Gradient parent;
						if (!gradients.TryGetValue(key, out parent))
						{
							break;
						}

						gradient.Stops.AddRange(parent.Stops);
						source = parent;
					}
				}
			}

			private static Gradient ParseGradient(XElement element, bool radial)
			{
				var gradient = new Gradient { Radial = radial };
				gradient.UserSpace = string.Equals((string)element.Attribute("gradientUnits"), "userSpaceOnUse", StringComparison.Ordinal);
				gradient.X1 = Length((string)element.Attribute("x1"), 0f);
				gradient.Y1 = Length((string)element.Attribute("y1"), 0f);
				gradient.X2 = Length((string)element.Attribute("x2"), 1f);
				gradient.Y2 = Length((string)element.Attribute("y2"), 0f);
				gradient.Cx = Length((string)element.Attribute("cx"), 0.5f);
				gradient.Cy = Length((string)element.Attribute("cy"), 0.5f);
				gradient.R = Length((string)element.Attribute("r"), 0.5f);
				gradient.Transform = ParseTransform((string)element.Attribute("gradientTransform"));
				foreach (XAttribute attribute in element.Attributes())
				{
					if (attribute.Name.LocalName == "href")
					{
						gradient.Href = attribute.Value;
					}
				}

				foreach (XElement stop in element.Elements())
				{
					if (stop.Name.LocalName != "stop")
					{
						continue;
					}

					Dictionary<string, string> style = StyleOf(stop);
					string offsetText = Attribute(stop, style, "offset") ?? "0";
					float offset = offsetText.EndsWith("%", StringComparison.Ordinal) ? Number(offsetText.Substring(0, offsetText.Length - 1)) / 100f : Number(offsetText);
					Color4 color = ParseColor(Attribute(stop, style, "stop-color") ?? "#000000") ?? new Color4(0f, 0f, 0f, 1f);
					float opacity = Number(Attribute(stop, style, "stop-opacity") ?? "1");
					color.A *= opacity;
					gradient.Stops.Add(new GradientStop { Offset = Math.Clamp(offset, 0f, 1f), Color = color });
				}

				gradient.Stops.Sort((a, b) => a.Offset.CompareTo(b.Offset));
				return gradient;
			}

			private void DrawChildren(XElement parent, Style inherited)
			{
				foreach (XElement child in parent.Elements())
				{
					string tag = child.Name.LocalName;
					if (tag == "defs" || tag == "linearGradient" || tag == "radialGradient" || tag == "title" || tag == "desc" || tag == "metadata" || tag == "style")
					{
						continue;
					}

					Style style = Inherit(child, inherited);
					switch (tag)
					{
						case "g":
						case "a":
						case "switch":
							DrawChildren(child, style);
							break;
						case "rect":
							DrawShape(RectPath(child), style);
							break;
						case "circle":
							DrawShape(EllipsePath(Length((string)child.Attribute("cx"), 0f), Length((string)child.Attribute("cy"), 0f), Length((string)child.Attribute("r"), 0f), Length((string)child.Attribute("r"), 0f)), style);
							break;
						case "ellipse":
							DrawShape(EllipsePath(Length((string)child.Attribute("cx"), 0f), Length((string)child.Attribute("cy"), 0f), Length((string)child.Attribute("rx"), 0f), Length((string)child.Attribute("ry"), 0f)), style);
							break;
						case "line":
						{
							var path = new List<SubPath>();
							var line = new SubPath();
							line.Points.Add(new Vector2(Length((string)child.Attribute("x1"), 0f), Length((string)child.Attribute("y1"), 0f)));
							line.Points.Add(new Vector2(Length((string)child.Attribute("x2"), 0f), Length((string)child.Attribute("y2"), 0f)));
							path.Add(line);
							DrawShape(path, style);
							break;
						}

						case "polyline":
						case "polygon":
						{
							float[] numbers = Numbers((string)child.Attribute("points"));
							var poly = new SubPath { Closed = tag == "polygon" };
							for (int i = 0; i + 1 < numbers.Length; i += 2)
							{
								poly.Points.Add(new Vector2(numbers[i], numbers[i + 1]));
							}

							DrawShape(new List<SubPath> { poly }, style);
							break;
						}

						case "path":
							DrawShape(ParsePath((string)child.Attribute("d")), style);
							break;
					}
				}
			}

			private Style Inherit(XElement element, Style parent)
			{
				Style style = parent;
				Dictionary<string, string> css = StyleOf(element);
				string fill = Attribute(element, css, "fill");
				if (fill != null)
				{
					style.Fill = ParsePaint(fill, style.Fill);
				}

				string stroke = Attribute(element, css, "stroke");
				if (stroke != null)
				{
					style.Stroke = ParsePaint(stroke, style.Stroke);
				}

				string value;
				if ((value = Attribute(element, css, "fill-opacity")) != null) style.FillOpacity = Number(value);
				if ((value = Attribute(element, css, "stroke-opacity")) != null) style.StrokeOpacity = Number(value);
				if ((value = Attribute(element, css, "stroke-width")) != null) style.StrokeWidth = Length(value, 1f);
				if ((value = Attribute(element, css, "stroke-miterlimit")) != null) style.MiterLimit = Number(value);
				if ((value = Attribute(element, css, "fill-rule")) != null) style.EvenOdd = value == "evenodd";
				if ((value = Attribute(element, css, "stroke-linecap")) != null) style.LineCap = value == "round" ? 1 : value == "square" ? 2 : 0;
				if ((value = Attribute(element, css, "stroke-linejoin")) != null) style.LineJoin = value == "round" ? 1 : value == "bevel" ? 2 : 0;
				// Group opacity multiplies down the tree (each element composites its own, which is close enough without overlaps).
				if ((value = Attribute(element, css, "opacity")) != null) style.Opacity = parent.Opacity * Number(value);
				string transform = (string)element.Attribute("transform");
				if (!string.IsNullOrEmpty(transform))
				{
					style.Transform = parent.Transform.Then(ParseTransform(transform));
				}

				return style;
			}

			private Paint ParsePaint(string text, Paint current)
			{
				text = text.Trim();
				if (text == "none")
				{
					return new Paint { None = true };
				}

				if (text.StartsWith("url(", StringComparison.Ordinal))
				{
					int start = text.IndexOf('#');
					int end = text.IndexOf(')');
					if (start >= 0 && end > start)
					{
						string id = text.Substring(start + 1, end - start - 1).Trim('\'', '"', ' ');
						Gradient gradient;
						if (gradients.TryGetValue(id, out gradient))
						{
							return new Paint { Gradient = gradient, Color = new Color4(1f, 1f, 1f, 1f) };
						}
					}

					return new Paint { None = true };
				}

				if (text == "currentColor" || text == "inherit")
				{
					return current;
				}

				Color4? color = ParseColor(text);
				return color.HasValue ? Paint.Solid(color.Value) : current;
			}

			// ------------------------------------------------------------------ shapes

			private sealed class SubPath
			{
				public readonly List<Vector2> Points = new List<Vector2>();
				public bool Closed;
			}

			private static List<SubPath> RectPath(XElement element)
			{
				float x = Length((string)element.Attribute("x"), 0f);
				float y = Length((string)element.Attribute("y"), 0f);
				float w = Length((string)element.Attribute("width"), 0f);
				float h = Length((string)element.Attribute("height"), 0f);
				float rx = Length((string)element.Attribute("rx"), -1f);
				float ry = Length((string)element.Attribute("ry"), -1f);
				if (rx < 0f && ry < 0f)
				{
					rx = ry = 0f;
				}
				else if (rx < 0f)
				{
					rx = ry;
				}
				else if (ry < 0f)
				{
					ry = rx;
				}

				rx = Math.Min(rx, w * 0.5f);
				ry = Math.Min(ry, h * 0.5f);
				var path = new SubPath { Closed = true };
				if (rx <= 0f || ry <= 0f)
				{
					path.Points.Add(new Vector2(x, y));
					path.Points.Add(new Vector2(x + w, y));
					path.Points.Add(new Vector2(x + w, y + h));
					path.Points.Add(new Vector2(x, y + h));
				}
				else
				{
					const int steps = 8;
					Corner(path, x + w - rx, y + ry, rx, ry, -90f, 0f, steps);
					Corner(path, x + w - rx, y + h - ry, rx, ry, 0f, 90f, steps);
					Corner(path, x + rx, y + h - ry, rx, ry, 90f, 180f, steps);
					Corner(path, x + rx, y + ry, rx, ry, 180f, 270f, steps);
				}

				return new List<SubPath> { path };
			}

			private static void Corner(SubPath path, float cx, float cy, float rx, float ry, float from, float to, int steps)
			{
				for (int i = 0; i <= steps; i++)
				{
					double angle = (from + (to - from) * i / steps) * Math.PI / 180.0;
					path.Points.Add(new Vector2(cx + (float)Math.Cos(angle) * rx, cy + (float)Math.Sin(angle) * ry));
				}
			}

			private static List<SubPath> EllipsePath(float cx, float cy, float rx, float ry)
			{
				var path = new SubPath { Closed = true };
				int segments = Math.Clamp((int)(Math.Max(rx, ry) * 2f), 24, 160);
				for (int i = 0; i < segments; i++)
				{
					double angle = i * Math.PI * 2.0 / segments;
					path.Points.Add(new Vector2(cx + (float)Math.Cos(angle) * rx, cy + (float)Math.Sin(angle) * ry));
				}

				return new List<SubPath> { path };
			}

			private static List<SubPath> ParsePath(string data)
			{
				var result = new List<SubPath>();
				if (string.IsNullOrWhiteSpace(data))
				{
					return result;
				}

				var tokens = new List<string>();
				int i = 0;
				while (i < data.Length)
				{
					char c = data[i];
					if (char.IsLetter(c))
					{
						tokens.Add(c.ToString());
						i++;
					}
					else if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
					{
						int numberStart = i;
						i++;
						bool exponent = false;
						bool dot = c == '.';
						while (i < data.Length)
						{
							char d = data[i];
							if (char.IsDigit(d))
							{
								i++;
							}
							else if (d == '.' && !dot && !exponent)
							{
								dot = true;
								i++;
							}
							else if ((d == 'e' || d == 'E') && !exponent)
							{
								exponent = true;
								i++;
								if (i < data.Length && (data[i] == '-' || data[i] == '+'))
								{
									i++;
								}
							}
							else
							{
								break;
							}
						}

						tokens.Add(data.Substring(numberStart, i - numberStart));
					}
					else
					{
						i++;
					}
				}

				SubPath current = null;
				Vector2 position = Vector2.Zero;
				Vector2 start = Vector2.Zero;
				Vector2 lastControl = Vector2.Zero;
				char lastCommand = ' ';
				int t = 0;
				char command = ' ';
				while (t < tokens.Count)
				{
					string token = tokens[t];
					if (token.Length == 1 && char.IsLetter(token[0]))
					{
						command = token[0];
						t++;
						if (command == 'Z' || command == 'z')
						{
							if (current != null)
							{
								current.Closed = true;
							}

							position = start;
							lastCommand = command;
							current = null;
							continue;
						}
					}
					else if (command == 'M')
					{
						command = 'L';
					}
					else if (command == 'm')
					{
						command = 'l';
					}

					bool relative = char.IsLower(command);
					char upper = char.ToUpperInvariant(command);
					Vector2 origin = relative ? position : Vector2.Zero;
					switch (upper)
					{
						case 'M':
						{
							position = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							current = new SubPath();
							current.Points.Add(position);
							result.Add(current);
							start = position;
							break;
						}

						case 'L':
							position = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Ensure(ref current, result, position);
							break;
						case 'H':
							position = new Vector2((relative ? position.X : 0f) + Num(tokens, ref t), position.Y);
							Ensure(ref current, result, position);
							break;
						case 'V':
							position = new Vector2(position.X, (relative ? position.Y : 0f) + Num(tokens, ref t));
							Ensure(ref current, result, position);
							break;
						case 'C':
						{
							Vector2 c1 = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Vector2 c2 = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Vector2 end = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Cubic(ref current, result, position, c1, c2, end);
							lastControl = c2;
							position = end;
							break;
						}

						case 'S':
						{
							Vector2 c1 = lastCommand is 'C' or 'c' or 'S' or 's' ? position * 2f - lastControl : position;
							Vector2 c2 = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Vector2 end = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Cubic(ref current, result, position, c1, c2, end);
							lastControl = c2;
							position = end;
							break;
						}

						case 'Q':
						{
							Vector2 c = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Vector2 end = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Quadratic(ref current, result, position, c, end);
							lastControl = c;
							position = end;
							break;
						}

						case 'T':
						{
							Vector2 c = lastCommand is 'Q' or 'q' or 'T' or 't' ? position * 2f - lastControl : position;
							Vector2 end = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							Quadratic(ref current, result, position, c, end);
							lastControl = c;
							position = end;
							break;
						}

						case 'A':
						{
							float rx = Num(tokens, ref t);
							float ry = Num(tokens, ref t);
							float rotation = Num(tokens, ref t);
							bool large = Num(tokens, ref t) != 0f;
							bool sweep = Num(tokens, ref t) != 0f;
							Vector2 end = origin + new Vector2(Num(tokens, ref t), Num(tokens, ref t));
							ArcTo(ref current, result, position, rx, ry, rotation, large, sweep, end);
							position = end;
							break;
						}

						default:
							// Unknown command: skip its token.
							t++;
							break;
					}

					lastCommand = command;
				}

				return result;
			}

			private static float Num(List<string> tokens, ref int t)
			{
				if (t >= tokens.Count)
				{
					return 0f;
				}

				return Number(tokens[t++]);
			}

			private static void Ensure(ref SubPath current, List<SubPath> result, Vector2 point)
			{
				if (current == null)
				{
					current = new SubPath();
					result.Add(current);
				}

				current.Points.Add(point);
			}

			private static void Cubic(ref SubPath current, List<SubPath> result, Vector2 from, Vector2 c1, Vector2 c2, Vector2 to)
			{
				float length = Vector2.Distance(from, c1) + Vector2.Distance(c1, c2) + Vector2.Distance(c2, to);
				int segments = Math.Clamp((int)Math.Ceiling(length / 2f), 6, 96);
				for (int i = 1; i <= segments; i++)
				{
					float s = i / (float)segments;
					float u = 1f - s;
					Ensure(ref current, result, u * u * u * from + 3f * u * u * s * c1 + 3f * u * s * s * c2 + s * s * s * to);
				}
			}

			private static void Quadratic(ref SubPath current, List<SubPath> result, Vector2 from, Vector2 c, Vector2 to)
			{
				float length = Vector2.Distance(from, c) + Vector2.Distance(c, to);
				int segments = Math.Clamp((int)Math.Ceiling(length / 2f), 6, 96);
				for (int i = 1; i <= segments; i++)
				{
					float s = i / (float)segments;
					float u = 1f - s;
					Ensure(ref current, result, u * u * from + 2f * u * s * c + s * s * to);
				}
			}

			/// <summary>SVG arc (endpoint parameterization, as the specification's implementation notes), sampled.</summary>
			private static void ArcTo(ref SubPath current, List<SubPath> result, Vector2 from, float rx, float ry, float rotation, bool large, bool sweep, Vector2 to)
			{
				if (rx <= 0f || ry <= 0f || from == to)
				{
					Ensure(ref current, result, to);
					return;
				}

				double phi = rotation * Math.PI / 180.0;
				double cos = Math.Cos(phi), sin = Math.Sin(phi);
				double dx = (from.X - to.X) / 2.0, dy = (from.Y - to.Y) / 2.0;
				double x1 = cos * dx + sin * dy;
				double y1 = -sin * dx + cos * dy;
				double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
				if (lambda > 1.0)
				{
					rx *= (float)Math.Sqrt(lambda);
					ry *= (float)Math.Sqrt(lambda);
				}

				double rx2 = rx * rx, ry2 = ry * ry;
				double numerator = Math.Max(0.0, rx2 * ry2 - rx2 * y1 * y1 - ry2 * x1 * x1);
				double denominator = rx2 * y1 * y1 + ry2 * x1 * x1;
				double factor = denominator > 0.0 ? Math.Sqrt(numerator / denominator) : 0.0;
				if (large == sweep)
				{
					factor = -factor;
				}

				double cxp = factor * rx * y1 / ry;
				double cyp = factor * -ry * x1 / rx;
				double cx = cos * cxp - sin * cyp + (from.X + to.X) / 2.0;
				double cy = sin * cxp + cos * cyp + (from.Y + to.Y) / 2.0;
				double startAngle = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
				double endAngle = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
				double delta = endAngle - startAngle;
				if (sweep && delta < 0)
				{
					delta += Math.PI * 2.0;
				}
				else if (!sweep && delta > 0)
				{
					delta -= Math.PI * 2.0;
				}

				int segments = Math.Clamp((int)Math.Ceiling(Math.Abs(delta) * Math.Max(rx, ry) / 2.0), 6, 128);
				for (int i = 1; i <= segments; i++)
				{
					double angle = startAngle + delta * i / segments;
					double ex = rx * Math.Cos(angle), ey = ry * Math.Sin(angle);
					Ensure(ref current, result, new Vector2((float)(cos * ex - sin * ey + cx), (float)(sin * ex + cos * ey + cy)));
				}
			}

			// ------------------------------------------------------------------ drawing

			private void DrawShape(List<SubPath> shape, Style style)
			{
				if (shape.Count == 0)
				{
					return;
				}

				// To raster space.
				var transformed = new List<SubPath>(shape.Count);
				foreach (SubPath path in shape)
				{
					var copy = new SubPath { Closed = path.Closed };
					foreach (Vector2 p in path.Points)
					{
						copy.Points.Add(style.Transform.Apply(p));
					}

					transformed.Add(copy);
				}

				if (!style.Fill.None)
				{
					var polygons = new List<List<Vector2>>();
					foreach (SubPath path in transformed)
					{
						if (path.Points.Count >= 3)
						{
							polygons.Add(path.Points);
						}
					}

					FillPolygons(polygons, style.EvenOdd, style.Fill, style.FillOpacity * style.Opacity, transformed, style.Transform);
				}

				if (!style.Stroke.None && style.StrokeWidth > 0f)
				{
					float width = style.StrokeWidth * style.Transform.Scale;
					var polygons = new List<List<Vector2>>();
					foreach (SubPath path in transformed)
					{
						StrokeOutline(path, width, style.LineCap, style.LineJoin, style.MiterLimit, polygons);
					}

					FillPolygons(polygons, false, style.Stroke, style.StrokeOpacity * style.Opacity, transformed, style.Transform);
				}
			}

			/// <summary>The stroke of a polyline as polygons (one per segment, join and cap), all wound the same way for a nonzero union.</summary>
			private static void StrokeOutline(SubPath path, float width, int cap, int join, float miterLimit, List<List<Vector2>> output)
			{
				var points = new List<Vector2>();
				foreach (Vector2 p in path.Points)
				{
					if (points.Count == 0 || (points[points.Count - 1] - p).LengthSquared > 1e-8f)
					{
						points.Add(p);
					}
				}

				if (path.Closed && points.Count > 2 && (points[points.Count - 1] - points[0]).LengthSquared <= 1e-8f)
				{
					points.RemoveAt(points.Count - 1);
				}

				float half = width * 0.5f;
				if (points.Count == 1)
				{
					if (cap == 1)
					{
						output.Add(Circle(points[0], half));
					}

					return;
				}

				int segments = path.Closed ? points.Count : points.Count - 1;
				for (int i = 0; i < segments; i++)
				{
					Vector2 a = points[i];
					Vector2 b = points[(i + 1) % points.Count];
					Vector2 dir = (b - a).Normalized();
					if (!path.Closed && cap == 2)
					{
						if (i == 0)
						{
							a -= dir * half;
						}

						if (i == segments - 1)
						{
							b += dir * half;
						}
					}

					var normal = new Vector2(-dir.Y, dir.X) * half;
					output.Add(Oriented(new List<Vector2> { a + normal, b + normal, b - normal, a - normal }));
				}

				int firstJoin = path.Closed ? 0 : 1;
				int lastJoin = path.Closed ? points.Count - 1 : points.Count - 2;
				for (int i = firstJoin; i <= lastJoin; i++)
				{
					Vector2 previous = points[(i + points.Count - 1) % points.Count];
					Vector2 p = points[i];
					Vector2 next = points[(i + 1) % points.Count];
					Vector2 d0 = (p - previous).Normalized();
					Vector2 d1 = (next - p).Normalized();
					float turn = d0.X * d1.Y - d0.Y * d1.X;
					if (Math.Abs(turn) < 1e-5f && Vector2.Dot(d0, d1) > 0f)
					{
						continue;
					}

					if (join == 1)
					{
						output.Add(Circle(p, half));
						continue;
					}

					float side = turn > 0f ? -1f : 1f;
					var n0 = new Vector2(-d0.Y, d0.X) * side;
					var n1 = new Vector2(-d1.Y, d1.X) * side;
					Vector2 c0 = p + n0 * half;
					Vector2 c1 = p + n1 * half;
					var corner = new List<Vector2> { p, c0, c1 };
					if (join == 0)
					{
						float cos = Vector2.Dot(n0, n1);
						float miterLength = 1f / (float)Math.Sqrt(Math.Max(1e-4f, (1f + cos) * 0.5f));
						Vector2 bisector = n0 + n1;
						if (miterLength <= miterLimit && bisector.LengthSquared > 1e-8f)
						{
							bisector.Normalize();
							corner = new List<Vector2> { p, c0, p + bisector * (half * miterLength), c1 };
						}
					}

					output.Add(Oriented(corner));
				}

				if (!path.Closed && cap == 1)
				{
					output.Add(Circle(points[0], half));
					output.Add(Circle(points[points.Count - 1], half));
				}
			}

			private static List<Vector2> Circle(Vector2 center, float radius)
			{
				int segments = Math.Clamp((int)(radius * 2f), 12, 96);
				var points = new List<Vector2>(segments);
				for (int i = 0; i < segments; i++)
				{
					double angle = i * Math.PI * 2.0 / segments;
					points.Add(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius);
				}

				return points;
			}

			/// <summary>The polygon wound with a positive signed area (so every stroke piece adds +1 winding).</summary>
			private static List<Vector2> Oriented(List<Vector2> polygon)
			{
				float area = 0f;
				for (int i = 0; i < polygon.Count; i++)
				{
					Vector2 a = polygon[i];
					Vector2 b = polygon[(i + 1) % polygon.Count];
					area += a.X * b.Y - b.X * a.Y;
				}

				if (area < 0f)
				{
					polygon.Reverse();
				}

				return polygon;
			}

			/// <summary>Scan converts the polygons into the coverage buffer and composites the paint over the covered pixels.</summary>
			private void FillPolygons(List<List<Vector2>> polygons, bool evenOdd, Paint paint, float opacity, List<SubPath> shape, Matrix transform)
			{
				if (polygons.Count == 0 || opacity <= 0f)
				{
					return;
				}

				edges.Clear();
				minX = int.MaxValue;
				minY = int.MaxValue;
				maxX = int.MinValue;
				maxY = int.MinValue;
				foreach (List<Vector2> polygon in polygons)
				{
					for (int i = 0; i < polygon.Count; i++)
					{
						Vector2 a = polygon[i];
						Vector2 b = polygon[(i + 1) % polygon.Count];
						if (Math.Abs(a.Y - b.Y) < 1e-7f)
						{
							continue;
						}

						int winding = 1;
						if (a.Y > b.Y)
						{
							(a, b) = (b, a);
							winding = -1;
						}

						edges.Add(new Edge { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y, Winding = winding });
						minX = Math.Min(minX, (int)Math.Floor(Math.Min(a.X, b.X)));
						maxX = Math.Max(maxX, (int)Math.Ceiling(Math.Max(a.X, b.X)));
						minY = Math.Min(minY, (int)Math.Floor(a.Y));
						maxY = Math.Max(maxY, (int)Math.Ceiling(b.Y));
					}
				}

				if (edges.Count == 0)
				{
					return;
				}

				minX = Math.Max(0, minX);
				minY = Math.Max(0, minY);
				maxX = Math.Min(width - 1, maxX);
				maxY = Math.Min(height - 1, maxY);
				if (minX > maxX || minY > maxY)
				{
					return;
				}

				for (int y = minY; y <= maxY; y++)
				{
					Array.Clear(coverage, y * width + minX, maxX - minX + 1);
				}

				float sampleWeight = 1f / SubScanlines;
				for (int y = minY; y <= maxY; y++)
				{
					for (int s = 0; s < SubScanlines; s++)
					{
						float sy = y + (s + 0.5f) / SubScanlines;
						crossings.Clear();
						crossingWindings.Clear();
						foreach (Edge edge in edges)
						{
							if (sy < edge.Y0 || sy >= edge.Y1)
							{
								continue;
							}

							float x = edge.X0 + (sy - edge.Y0) * (edge.X1 - edge.X0) / (edge.Y1 - edge.Y0);
							int index = crossings.Count;
							crossings.Add(x);
							crossingWindings.Add(edge.Winding);
							// Insertion sort: the lists are short.
							while (index > 0 && crossings[index - 1] > crossings[index])
							{
								(crossings[index - 1], crossings[index]) = (crossings[index], crossings[index - 1]);
								(crossingWindings[index - 1], crossingWindings[index]) = (crossingWindings[index], crossingWindings[index - 1]);
								index--;
							}
						}

						int winding = 0;
						for (int i = 0; i + 1 < crossings.Count; i++)
						{
							winding += crossingWindings[i];
							bool inside = evenOdd ? (winding & 1) != 0 : winding != 0;
							if (!inside)
							{
								continue;
							}

							AddSpan(y, crossings[i], crossings[i + 1], sampleWeight);
						}
					}
				}

				// The paint's bounding box (object bounding box units): the whole shape's extent in raster space.
				var bounds = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
				foreach (SubPath path in shape)
				{
					foreach (Vector2 p in path.Points)
					{
						bounds.X = Math.Min(bounds.X, p.X);
						bounds.Y = Math.Min(bounds.Y, p.Y);
						bounds.Z = Math.Max(bounds.Z, p.X);
						bounds.W = Math.Max(bounds.W, p.Y);
					}
				}

				for (int y = minY; y <= maxY; y++)
				{
					int row = y * width;
					for (int x = minX; x <= maxX; x++)
					{
						float c = coverage[row + x];
						if (c <= 0.0005f)
						{
							continue;
						}

						Color4 color = paint.Gradient != null ? Evaluate(paint.Gradient, x + 0.5f, y + 0.5f, bounds, transform) : paint.Color;
						float alpha = Math.Min(1f, c) * color.A * opacity;
						if (alpha <= 0f)
						{
							continue;
						}

						int i = (row + x) * 4;
						float keep = 1f - alpha;
						pixels[i] = color.R * alpha + pixels[i] * keep;
						pixels[i + 1] = color.G * alpha + pixels[i + 1] * keep;
						pixels[i + 2] = color.B * alpha + pixels[i + 2] * keep;
						pixels[i + 3] = alpha + pixels[i + 3] * keep;
					}
				}
			}

			/// <summary>Adds the horizontal coverage of [x0, x1] on one sub scanline of row y.</summary>
			private void AddSpan(int y, float x0, float x1, float weight)
			{
				if (x1 <= x0)
				{
					return;
				}

				x0 = Math.Max(x0, 0f);
				x1 = Math.Min(x1, width);
				if (x1 <= x0)
				{
					return;
				}

				int row = y * width;
				int first = (int)Math.Floor(x0);
				int last = (int)Math.Ceiling(x1) - 1;
				if (first == last)
				{
					coverage[row + first] += (x1 - x0) * weight;
					return;
				}

				coverage[row + first] += (first + 1 - x0) * weight;
				for (int x = first + 1; x < last; x++)
				{
					coverage[row + x] += weight;
				}

				coverage[row + last] += (x1 - last) * weight;
			}

			private static Color4 Evaluate(Gradient gradient, float px, float py, Vector4 bounds, Matrix transform)
			{
				if (gradient.Stops.Count == 0)
				{
					return new Color4(0f, 0f, 0f, 0f);
				}

				float t;
				if (gradient.UserSpace)
				{
					// Gradient coordinates in user space: bring them to raster space with the shape's transform.
					Matrix m = transform.Then(gradient.Transform);
					if (gradient.Radial)
					{
						Vector2 center = m.Apply(new Vector2(gradient.Cx, gradient.Cy));
						float radius = gradient.R * m.Scale;
						t = radius > 0f ? Vector2.Distance(new Vector2(px, py), center) / radius : 1f;
					}
					else
					{
						Vector2 p1 = m.Apply(new Vector2(gradient.X1, gradient.Y1));
						Vector2 p2 = m.Apply(new Vector2(gradient.X2, gradient.Y2));
						t = Project(new Vector2(px, py), p1, p2);
					}
				}
				else
				{
					float w = Math.Max(1e-5f, bounds.Z - bounds.X);
					float h = Math.Max(1e-5f, bounds.W - bounds.Y);
					// Object bounding box units: the unit square maps onto the shape's bounds (a non-uniform scale, as SVG).
					float ux = (px - bounds.X) / w;
					float uy = (py - bounds.Y) / h;
					if (gradient.Radial)
					{
						float dx = (ux - gradient.Cx) / Math.Max(1e-5f, gradient.R);
						float dy = (uy - gradient.Cy) / Math.Max(1e-5f, gradient.R);
						t = (float)Math.Sqrt(dx * dx + dy * dy);
					}
					else
					{
						t = Project(new Vector2(ux, uy), new Vector2(gradient.X1, gradient.Y1), new Vector2(gradient.X2, gradient.Y2));
					}
				}

				t = Math.Clamp(t, 0f, 1f);
				List<GradientStop> stops = gradient.Stops;
				if (t <= stops[0].Offset)
				{
					return stops[0].Color;
				}

				for (int i = 1; i < stops.Count; i++)
				{
					if (t <= stops[i].Offset)
					{
						float span = stops[i].Offset - stops[i - 1].Offset;
						float f = span > 1e-6f ? (t - stops[i - 1].Offset) / span : 1f;
						return Lerp(stops[i - 1].Color, stops[i].Color, f);
					}
				}

				return stops[stops.Count - 1].Color;
			}

			private static float Project(Vector2 p, Vector2 a, Vector2 b)
			{
				Vector2 d = b - a;
				float length = d.LengthSquared;
				return length > 1e-10f ? Vector2.Dot(p - a, d) / length : 0f;
			}

			private static Color4 Lerp(Color4 a, Color4 b, float t)
			{
				return new Color4(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
			}

			// ------------------------------------------------------------------ parsing helpers

			private static Dictionary<string, string> StyleOf(XElement element)
			{
				var result = new Dictionary<string, string>(StringComparer.Ordinal);
				string style = (string)element.Attribute("style");
				if (string.IsNullOrEmpty(style))
				{
					return result;
				}

				foreach (string declaration in style.Split(';'))
				{
					int colon = declaration.IndexOf(':');
					if (colon > 0)
					{
						result[declaration.Substring(0, colon).Trim()] = declaration.Substring(colon + 1).Trim();
					}
				}

				return result;
			}

			/// <summary>A presentation value: the style attribute wins over the attribute of the same name.</summary>
			private static string Attribute(XElement element, Dictionary<string, string> style, string name)
			{
				string value;
				if (style.TryGetValue(name, out value))
				{
					return value;
				}

				return (string)element.Attribute(name);
			}

			private static Matrix ParseTransform(string text)
			{
				Matrix result = Matrix.Identity;
				if (string.IsNullOrWhiteSpace(text))
				{
					return result;
				}

				int i = 0;
				while (i < text.Length)
				{
					int open = text.IndexOf('(', i);
					if (open < 0)
					{
						break;
					}

					int close = text.IndexOf(')', open);
					if (close < 0)
					{
						break;
					}

					string function = text.Substring(i, open - i).Trim(' ', ',', '\t', '\n', '\r');
					float[] v = Numbers(text.Substring(open + 1, close - open - 1));
					Matrix m = Matrix.Identity;
					switch (function)
					{
						case "matrix":
							if (v.Length == 6) m = new Matrix { A = v[0], B = v[1], C = v[2], D = v[3], E = v[4], F = v[5] };
							break;
						case "translate":
							m = Matrix.Translation(v.Length > 0 ? v[0] : 0f, v.Length > 1 ? v[1] : 0f);
							break;
						case "scale":
							m = Matrix.Scaling(v.Length > 0 ? v[0] : 1f, v.Length > 1 ? v[1] : v.Length > 0 ? v[0] : 1f);
							break;
						case "rotate":
							m = Matrix.Rotation(v.Length > 0 ? v[0] : 0f);
							if (v.Length == 3)
							{
								m = Matrix.Translation(v[1], v[2]).Then(m).Then(Matrix.Translation(-v[1], -v[2]));
							}

							break;
						case "skewX":
							m = new Matrix { A = 1f, D = 1f, C = (float)Math.Tan((v.Length > 0 ? v[0] : 0f) * Math.PI / 180.0) };
							break;
						case "skewY":
							m = new Matrix { A = 1f, D = 1f, B = (float)Math.Tan((v.Length > 0 ? v[0] : 0f) * Math.PI / 180.0) };
							break;
					}

					result = result.Then(m);
					i = close + 1;
				}

				return result;
			}

			private static float[] Numbers(string text)
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					return Array.Empty<float>();
				}

				string[] parts = text.Replace(',', ' ').Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
				var result = new List<float>(parts.Length);
				foreach (string part in parts)
				{
					result.Add(Number(part));
				}

				return result.ToArray();
			}

			private static float Number(string text)
			{
				float value;
				return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
			}

			/// <summary>A length: a number with an optional unit (px as is, pt / mm / cm / in converted, % of 1).</summary>
			private static float Length(string text, float fallback)
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					return fallback;
				}

				text = text.Trim();
				float factor = 1f;
				if (text.EndsWith("px", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 2);
				else if (text.EndsWith("pt", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 2); factor = 96f / 72f; }
				else if (text.EndsWith("mm", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 2); factor = 96f / 25.4f; }
				else if (text.EndsWith("cm", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 2); factor = 96f / 2.54f; }
				else if (text.EndsWith("in", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 2); factor = 96f; }
				else if (text.EndsWith("%", StringComparison.Ordinal)) { text = text.Substring(0, text.Length - 1); factor = 0.01f; }

				float value;
				return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value * factor : fallback;
			}

			private static Color4? ParseColor(string text)
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					return null;
				}

				text = text.Trim();
				if (text.StartsWith("#", StringComparison.Ordinal))
				{
					string hex = text.Substring(1);
					if (hex.Length == 3 || hex.Length == 4)
					{
						var expanded = new char[hex.Length * 2];
						for (int i = 0; i < hex.Length; i++)
						{
							expanded[i * 2] = hex[i];
							expanded[i * 2 + 1] = hex[i];
						}

						hex = new string(expanded);
					}

					if (hex.Length == 6 || hex.Length == 8)
					{
						int value;
						if (int.TryParse(hex.Substring(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
						{
							float a = 1f;
							int alpha;
							if (hex.Length == 8 && int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha))
							{
								a = alpha / 255f;
							}

							return new Color4(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f, a);
						}
					}

					return null;
				}

				if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
				{
					int open = text.IndexOf('(');
					int close = text.IndexOf(')');
					if (open >= 0 && close > open)
					{
						string[] parts = text.Substring(open + 1, close - open - 1).Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
						if (parts.Length >= 3)
						{
							float r = Channel(parts[0]), g = Channel(parts[1]), b = Channel(parts[2]);
							float a = parts.Length > 3 ? (parts[3].EndsWith("%", StringComparison.Ordinal) ? Number(parts[3].TrimEnd('%')) / 100f : Number(parts[3])) : 1f;
							return new Color4(r, g, b, a);
						}
					}

					return null;
				}

				switch (text.ToLowerInvariant())
				{
					case "white": return new Color4(1f, 1f, 1f, 1f);
					case "black": return new Color4(0f, 0f, 0f, 1f);
					case "red": return new Color4(1f, 0f, 0f, 1f);
					case "green": return new Color4(0f, 0.5f, 0f, 1f);
					case "lime": return new Color4(0f, 1f, 0f, 1f);
					case "blue": return new Color4(0f, 0f, 1f, 1f);
					case "yellow": return new Color4(1f, 1f, 0f, 1f);
					case "cyan":
					case "aqua": return new Color4(0f, 1f, 1f, 1f);
					case "magenta":
					case "fuchsia": return new Color4(1f, 0f, 1f, 1f);
					case "gray":
					case "grey": return new Color4(0.5f, 0.5f, 0.5f, 1f);
					case "silver": return new Color4(0.75f, 0.75f, 0.75f, 1f);
					case "orange": return new Color4(1f, 0.647f, 0f, 1f);
					case "transparent": return new Color4(0f, 0f, 0f, 0f);
					default: return null;
				}
			}

			private static float Channel(string text)
			{
				return text.EndsWith("%", StringComparison.Ordinal) ? Number(text.TrimEnd('%')) / 100f : Number(text) / 255f;
			}

			private static byte ToByte(float value)
			{
				return (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);
			}
		}

		#endregion
	}
}
