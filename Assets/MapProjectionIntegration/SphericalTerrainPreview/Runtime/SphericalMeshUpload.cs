using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WW2.SphericalTerrainPreview
{
    public sealed partial class SphericalTerrainPreview
    {
        sealed partial class MeshBuffer
        {
            // All attributes use stream zero, in Unity's canonical attribute
            // order. Explicit packing keeps managed arrays identical to the GPU
            // declaration: terrain/lines 136 bytes, water 60 bytes per vertex.
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            struct TerrainUploadVertex
            {
                public Vector3 Position, Normal;
                public Color Color;
                public Vector4 UV0, UV1, UV2, UV3, UV4, UV5;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            struct WaterUploadVertex
            {
                public Vector3 Position, Normal;
                public Color32 Color;
                public Vector4 UV0, UV1;
            }

            sealed class UploadPacket
            {
                public TerrainUploadVertex[] Terrain;
                public WaterUploadVertex[] Water;
                public int[] Indices;
                public Bounds Bounds;
                public bool IsWater, Lines;
                public int VertexCount;
                public long Bytes => (IsWater ? 60L : 136L) * VertexCount + 4L * Indices.Length;
            }

            static readonly VertexAttributeDescriptor[] TerrainUploadLayout =
            {
                new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord4, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord5, VertexAttributeFormat.Float32, 4)
            };
            static readonly VertexAttributeDescriptor[] WaterUploadLayout =
            {
                new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
                new(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4)
            };
            const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontRecalculateBounds |
                MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            UploadPacket preparedUpload;

            public long PreparedUploadBytes => preparedUpload?.Bytes ?? 0;
            public int PreparedVertexCount => preparedUpload?.VertexCount ?? 0;
            public int PreparedTriangleCount => preparedUpload == null || preparedUpload.Lines ? 0 : preparedUpload.Indices.Length / 3;

            /// <summary>
            /// Worker-safe immutable snapshot. Call after normals, UV edits and
            /// topology are final. Do not mutate the public source lists between
            /// this method and Create; direct in-place edits require preparing
            /// again. No Mesh or other Unity object is created here.
            /// </summary>
            public void PrepareUpload(bool water = false, bool lines = false)
            {
                if (water && lines) throw new ArgumentException("Water upload is a triangle layout.");
                if (Marshal.SizeOf<TerrainUploadVertex>() != 136 || Marshal.SizeOf<WaterUploadVertex>() != 60)
                    throw new InvalidOperationException("Unexpected managed spherical vertex layout.");
                int count = Vertices.Count;
                RequireChannel(Normals, count, "normal"); RequireChannel(Colors, count, "color");
                RequireChannel(UV0, count, "UV0"); RequireChannel(UV1, count, "UV1");
                if (!water)
                {
                    RequireChannel(UV2, count, "UV2"); RequireChannel(UV3, count, "UV3");
                    RequireChannel(UV4, count, "UV4"); RequireChannel(UV5, count, "UV5");
                }
                int[] indices = Indices.ToArray();
                int primitiveSize = lines ? 2 : 3;
                if (indices.Length % primitiveSize != 0)
                    throw new InvalidOperationException("Incomplete spherical mesh primitive.");
                for (int i = 0; i < indices.Length; i++)
                    if ((uint)indices[i] >= (uint)count)
                        throw new InvalidOperationException("Spherical mesh index is outside its vertex buffer.");

                var packet = new UploadPacket { IsWater = water, Lines = lines, VertexCount = count, Indices = indices };
                if (water) packet.Water = new WaterUploadVertex[count];
                else packet.Terrain = new TerrainUploadVertex[count];
                Vector3 minimum = count == 0 ? Vector3.zero : Vertices[0], maximum = minimum;
                for (int i = 0; i < count; i++)
                {
                    Vector3 position = Vertices[i];
                    if (!Finite(position.x) || !Finite(position.y) || !Finite(position.z))
                        throw new InvalidOperationException("Non-finite spherical mesh position.");
                    minimum = Vector3.Min(minimum, position); maximum = Vector3.Max(maximum, position);
                    if (water)
                        packet.Water[i] = new WaterUploadVertex { Position = position, Normal = Normals[i],
                            Color = Colors[i], UV0 = UV0[i], UV1 = UV1[i] };
                    else
                        packet.Terrain[i] = new TerrainUploadVertex { Position = position, Normal = Normals[i],
                            Color = Colors[i], UV0 = UV0[i], UV1 = UV1[i], UV2 = UV2[i], UV3 = UV3[i], UV4 = UV4[i], UV5 = UV5[i] };
                }
                packet.Bounds = new Bounds((minimum + maximum) * .5f, maximum - minimum);
                preparedUpload = packet;
            }

            static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            static void RequireChannel<T>(List<T> channel, int count, string name)
            {
                if (channel.Count != count) throw new InvalidOperationException("Spherical " + name + " channel has the wrong vertex count.");
            }

            public Mesh CreateWater(string name) => CreatePrepared(name, true, false);

            Mesh CreatePrepared(string name, bool water, bool lines)
            {
                // Startup meshes may not have a worker preparation step. This
                // compatibility path packs once; streamed tiles arrive prepared.
                if (preparedUpload == null || preparedUpload.IsWater != water || preparedUpload.Lines != lines ||
                    preparedUpload.VertexCount != Vertices.Count || preparedUpload.Indices.Length != Indices.Count)
                    PrepareUpload(water, lines);
                var packet = preparedUpload;
                var mesh = new Mesh();
                try
                {
                    mesh.name = name;
                    mesh.SetVertexBufferParams(packet.VertexCount, water ? WaterUploadLayout : TerrainUploadLayout);
                    if (packet.VertexCount > 0)
                    {
                        if (water) mesh.SetVertexBufferData(packet.Water, 0, 0, packet.VertexCount, 0, UploadFlags);
                        else mesh.SetVertexBufferData(packet.Terrain, 0, 0, packet.VertexCount, 0, UploadFlags);
                    }
                    mesh.SetIndexBufferParams(packet.Indices.Length, IndexFormat.UInt32);
                    if (packet.Indices.Length > 0) mesh.SetIndexBufferData(packet.Indices, 0, 0, packet.Indices.Length, UploadFlags);
                    mesh.subMeshCount = 1;
                    mesh.SetSubMesh(0, new SubMeshDescriptor(0, packet.Indices.Length,
                        lines ? MeshTopology.Lines : MeshTopology.Triangles)
                        { bounds = packet.Bounds, firstVertex = 0, vertexCount = packet.VertexCount }, UploadFlags);
                    mesh.bounds = packet.Bounds;
                    return mesh;
                }
                catch
                {
                    // The caller never received this mesh, so publication cannot
                    // clean it up. Also support the isolated Edit-mode roundtrip.
                    if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                    else UnityEngine.Object.DestroyImmediate(mesh);
                    throw;
                }
            }
        }
    }
}
