﻿using System.Collections.Generic;
using System.IO;
using OpenTK;

namespace LELEngine
{
	public sealed class Mesh
	{
		#region PublicFields

		public List<Vector2> UVs = new List<Vector2>();
		public List<Vector3> Positions = new List<Vector3>();
		public List<Vector3> Normals = new List<Vector3>();
		public List<Vertex> Verticies = new List<Vertex>();
		public List<int> indicies = new List<int>();

		#endregion

		#region PrivateFields

		private List<VertexTemplate> Templates = new List<VertexTemplate>();
		private int index = -1;

		#endregion

		#region Constructors

		/// <summary>
		///     Obj Deserializer
		/// </summary>
		/// <param name="name">File name (with *.obj)</param>
		public Mesh(string name)
		{
			if (name.Split('.')[1] == "obj")
			{
				using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + "/Meshes/" + name))
				{
					var lines = new List<string>();
					while (!sr.EndOfStream)
					{
						string tmp = sr.ReadLine();
						lines.Add(tmp);
					}

					foreach (string ob in lines)
					{
						string[] words = ob.Split(' ');
						switch (words[0].Trim())
						{
							case "v":
								Positions.Add(new Vector3(float.Parse(words[1].Trim().Replace('.', ',')), float.Parse(words[2].Trim().Replace('.', ',')), float.Parse(words[3].Trim().Replace('.', ','))));
								break;
							case "vt":
								UVs.Add(new Vector2(float.Parse(words[1].Replace('.', ',')), float.Parse(words[2].Replace('.', ','))));
								break;
							case "vn":
								Normals.Add(new Vector3(float.Parse(words[1].Replace('.', ',')), float.Parse(words[2].Replace('.', ',')), float.Parse(words[3].Replace('.', ','))));
								break;
							case "f":
								string[] v1 = words[1].Trim().Split('/');
								string[] v2 = words[2].Trim().Split('/');
								string[] v3 = words[3].Trim().Split('/');

								int tri1 = int.Parse(v1[0]) - 1;
								int tri2 = int.Parse(v2[0]) - 1;
								int tri3 = int.Parse(v3[0]) - 1;
								int uv1 = int.Parse(v1[1]) - 1;
								int uv2 = int.Parse(v2[1]) - 1;
								int uv3 = int.Parse(v3[1]) - 1;
								int nr1 = int.Parse(v1[2]) - 1;
								int nr2 = int.Parse(v2[2]) - 1;
								int nr3 = int.Parse(v3[2]) - 1;

								VertexTemplate temp1 = new VertexTemplate();
								temp1.position = Positions[tri1];
								temp1.texcoord = UVs[uv1];
								temp1.normal = Normals[nr1];
								Templates.Add(temp1);

								VertexTemplate temp2 = new VertexTemplate();
								temp2.position = Positions[tri2];
								temp2.texcoord = UVs[uv2];
								temp2.normal = Normals[nr2];
								Templates.Add(temp2);

								VertexTemplate temp3 = new VertexTemplate();
								temp3.position = Positions[tri3];
								temp3.texcoord = UVs[uv3];
								temp3.normal = Normals[nr3];
								Templates.Add(temp3);

								Vector3 edge1 = temp2.position - temp1.position;
								Vector3 edge2 = temp3.position - temp1.position;
								Vector2 deltaUV1 = temp2.texcoord - temp1.texcoord;
								Vector2 deltaUV2 = temp3.texcoord - temp1.texcoord;

								float f = 1.0f / (deltaUV1.X * deltaUV2.Y - deltaUV2.X * deltaUV1.Y);

								Vector3 tangent1 = Vector3.Zero;
								tangent1.X = f * (deltaUV2.Y * edge1.X - deltaUV1.Y * edge2.X);
								tangent1.Y = f * (deltaUV2.Y * edge1.Y - deltaUV1.Y * edge2.Y);
								tangent1.Z = f * (deltaUV2.Y * edge1.Z - deltaUV1.Y * edge2.Z);
								tangent1 = tangent1.Normalized();

								Vector3 bitangent1 = Vector3.Zero;
								bitangent1.X = f * (-deltaUV2.X * edge1.X + deltaUV1.X * edge2.X);
								bitangent1.Y = f * (-deltaUV2.X * edge1.Y + deltaUV1.X * edge2.Y);
								bitangent1.Z = f * (-deltaUV2.X * edge1.Z + deltaUV1.X * edge2.Z);
								bitangent1 = bitangent1.Normalized();

								temp1.tangent = tangent1;
								temp2.tangent = tangent1;
								temp3.tangent = tangent1;
								temp1.bitangent = bitangent1;
								temp2.bitangent = bitangent1;
								temp3.bitangent = bitangent1;

								index++;
								indicies.Add(index);
								index++;
								indicies.Add(index);
								index++;
								indicies.Add(index);
								break;
						}
					}
				}
			}
			else if (name.Split('.')[1] == "fbx")
			{
				// To be done
			}

			foreach (VertexTemplate te in Templates)
			{
				Verticies.Add(new Vertex(te.position, te.normal, te.texcoord, te.tangent, te.bitangent));
			}
		}

		#endregion
	}
}
