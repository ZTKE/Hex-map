using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace WW2.SphericalTerrainPreview.Editor
{
    /// <summary>Explicit, one-shot native mesh readback. Never runs without a request.</summary>
    [InitializeOnLoad]
    public static class SphericalMeshUploadValidation
    {
        static readonly string ProjectRoot = Directory.GetParent(Application.dataPath).FullName;
        static readonly string RequestPath = Path.Combine(ProjectRoot, "Temp/SphericalMeshUploadValidation.request");
        static readonly string ReportPath = Path.Combine(ProjectRoot, "Artifacts/Game2RotationOptimization20260913/MeshUpload/UnityRoundtrip.json");
        static bool pending;
        static int checks;

        [Serializable] sealed class Report
        {
            public string status, message, startedUtc, finishedUtc, unityVersion, graphicsApi;
            public int checks;
            public List<CaseResult> cases = new();
        }
        [Serializable] sealed class CaseResult
        {
            public string name, status;
            public int vertexCount, indexCount, stride, workerThread, mainThread;
            public int actualVertexCount, actualVertexBufferCount, actualAttributeCount, actualStride = -1;
            public string actualIndexFormat;
            public long packetBytes;
            public double preparationMilliseconds, creationMilliseconds, readbackMilliseconds;
        }
        sealed class Fixture
        {
            public object Buffer;
            public List<Vector3> Positions, Normals;
            public List<Color> Colors;
            public List<Vector4>[] UV;
            public List<int> Indices;
            public bool Water, Lines;
        }

        static SphericalMeshUploadValidation()
        {
            // Check only during domain initialization. No Editor update hook,
            // file polling, or repeated automatic validation is installed.
            if (!File.Exists(RequestPath)) return;
            try { File.Delete(RequestPath); RequestValidation(); }
            catch (Exception error)
            {
                Save(new Report { status = "failed", message = "Could not consume the fixed request: " + error,
                    finishedUtc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion });
            }
        }

        [MenuItem("Window/Map Projection/Spherical Terrain Preview/Validate Mesh Upload Roundtrip")]
        public static void RequestValidation()
        {
            if (pending) return;
            pending = true;
            EditorApplication.delayCall += RunScheduled;
        }

        static void RunScheduled()
        {
            pending = false; checks = 0;
            var report = new Report { status = "running", startedUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString() };
            Save(report);
            try
            {
                RunCase(report, "terrain-six-uv", 39, false, false);
                RunCase(report, "water-color32", 39, true, false);
                RunCase(report, "lines-full-normal-color-uv", 40, false, true);
                RunCase(report, "empty-terrain", 0, false, false);
                RunCase(report, "empty-water", 0, true, false);
                RunCase(report, "empty-lines", 0, false, true);
                RunCase(report, "water-uint32-over-65535", 70002, true, false);
                report.status = "passed";
                report.message = "Seven fresh readable meshes created through the production prepared upload path; all native channel/stride/topology/index/bounds comparisons passed. No scene objects were changed.";
                Debug.Log("Spherical mesh upload roundtrip passed: " + ReportPath);
            }
            catch (Exception error)
            {
                report.status = "failed";
                report.message = (error is TargetInvocationException invocation ? invocation.InnerException ?? error : error).ToString();
                Debug.LogError("Spherical mesh upload roundtrip failed: " + report.message);
            }
            finally
            {
                report.checks = checks; report.finishedUtc = DateTime.UtcNow.ToString("O"); Save(report);
            }
        }

        static void Save(Report report)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
        }
        static void Check(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException(message); }
        static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        static bool Same(Vector3 a, Vector3 b) => Same(a.x,b.x) && Same(a.y,b.y) && Same(a.z,b.z);
        static bool Same(Vector4 a, Vector4 b) => Same(a.x,b.x) && Same(a.y,b.y) && Same(a.z,b.z) && Same(a.w,b.w);
        static bool Same(Color a, Color b) => Same(a.r,b.r) && Same(a.g,b.g) && Same(a.b,b.b) && Same(a.a,b.a);

        static Fixture MakeFixture(int count, bool water, bool lines)
        {
            Type type = typeof(SphericalTerrainPreview).GetNestedType("MeshBuffer", BindingFlags.NonPublic);
            object buffer = Activator.CreateInstance(type, true);
            T Field<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.Public).GetValue(buffer);
            var fixture = new Fixture { Buffer = buffer, Water = water, Lines = lines,
                Positions = Field<List<Vector3>>("Vertices"), Normals = Field<List<Vector3>>("Normals"),
                Colors = Field<List<Color>>("Colors"), Indices = Field<List<int>>("Indices"), UV = new List<Vector4>[6] };
            for (int channel = 0; channel < 6; channel++) fixture.UV[channel] = Field<List<Vector4>>("UV" + channel);
            for (int i = 0; i < count; i++)
            {
                fixture.Positions.Add(new Vector3((i%97)*.032f-1, 3300+(i%29)*.015f, -i*.017f));
                fixture.Normals.Add(new Vector3(-.17f, .693f, (i%13)*.02f-.5f));
                fixture.Colors.Add(new Color(i%5*.27f-.13f, .401f, 1.28f, i%3 == 0 ? -0f : .502f));
                for (int channel = 0; channel < 6; channel++)
                {
                    if (water && channel > 3) continue;
                    fixture.UV[channel].Add(water && channel > 1 ? Vector4.zero :
                        new Vector4(-i-channel*.1f, channel*.2f, i%7 == 0 ? -0f : -.03f*i, channel+1000.125f));
                }
            }
            if (count > 65535) fixture.Indices.AddRange(new[] { 70001, 65536, 0 });
            else
            {
                int primitive = lines ? 2 : 3;
                for (int i = 0; i+primitive <= count; i += primitive)
                    for (int j = primitive-1; j >= 0; j--) fixture.Indices.Add(i+j);
            }
            return fixture;
        }

        static void RunCase(Report report, string name, int count, bool water, bool lines)
        {
            var result = new CaseResult { name = name, status = "running", vertexCount = count,
                stride = water ? 60 : 136, mainThread = Thread.CurrentThread.ManagedThreadId };
            report.cases.Add(result);
            Fixture fixture = MakeFixture(count, water, lines);
            Type type = fixture.Buffer.GetType();
            var watch = Stopwatch.StartNew();
            Task.Run(() =>
            {
                result.workerThread = Thread.CurrentThread.ManagedThreadId;
                type.GetMethod("PrepareUpload").Invoke(fixture.Buffer, new object[] { water, lines });
            }).GetAwaiter().GetResult();
            result.preparationMilliseconds = watch.Elapsed.TotalMilliseconds;
            result.indexCount = fixture.Indices.Count;
            result.packetBytes = (long)type.GetProperty("PreparedUploadBytes").GetValue(fixture.Buffer);
            Check(result.workerThread != result.mainThread, "Preparation did not execute on a worker.");
            Check(result.packetBytes == (long)result.stride*count + 4L*result.indexCount, "Packet byte count changed.");
            Mesh mesh = null;
            try
            {
                watch.Restart();
                mesh = (Mesh)type.GetMethod(water ? "CreateWater" : "Create").Invoke(fixture.Buffer,
                    water ? new object[] { "Roundtrip " + name } : new object[] { "Roundtrip " + name, lines });
                mesh.hideFlags = HideFlags.HideAndDontSave;
                result.creationMilliseconds = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                VerifyNativeMesh(mesh, fixture, result);
                result.readbackMilliseconds = watch.Elapsed.TotalMilliseconds;
                result.status = "passed";
            }
            catch { result.status = "failed"; throw; }
            finally { if (mesh) Object.DestroyImmediate(mesh); }
        }

        static void VerifyNativeMesh(Mesh mesh, Fixture fixture, CaseResult result)
        {
            int count = fixture.Positions.Count;
            Check(mesh.isReadable, "Cold mesh became unreadable before validation.");
            result.actualVertexCount = mesh.vertexCount;
            result.actualVertexBufferCount = mesh.vertexBufferCount;
            result.actualIndexFormat = mesh.indexFormat.ToString();
            var attributes = mesh.GetVertexAttributes();
            result.actualAttributeCount = attributes.Length;
            if (result.actualVertexBufferCount > 0) result.actualStride = mesh.GetVertexBufferStride(0);
            Check(result.actualVertexCount == count, $"Wrong vertex count: actual={result.actualVertexCount}, expected={count}.");
            Check(mesh.subMeshCount == 1, $"Wrong submesh count: actual={mesh.subMeshCount}, expected=1.");
            // Unity need not allocate any native vertex stream/attributes for
            // zero vertices. Empty meshes still verify indices, topology and
            // explicit ranges/bounds below; their actual layout is recorded.
            if (count > 0) VerifyNativeChannels(mesh, fixture, attributes, result);
            var submesh = mesh.GetSubMesh(0);
            Check(submesh.indexStart == 0 && submesh.indexCount == fixture.Indices.Count && submesh.baseVertex == 0 && submesh.firstVertex == 0 && submesh.vertexCount == count,
                $"Submesh range changed: indexStart={submesh.indexStart}, indexCount={submesh.indexCount}/{fixture.Indices.Count}, baseVertex={submesh.baseVertex}, firstVertex={submesh.firstVertex}, vertexCount={submesh.vertexCount}/{count}.");
            MeshTopology expectedTopology = fixture.Lines ? MeshTopology.Lines : MeshTopology.Triangles;
            Check(mesh.GetTopology(0) == expectedTopology, $"Primitive topology changed: actual={mesh.GetTopology(0)}, expected={expectedTopology}.");
            int[] indices = mesh.GetIndices(0); Check(indices.Length == fixture.Indices.Count, $"Index readback length changed: actual={indices.Length}, expected={fixture.Indices.Count}.");
            for (int i = 0; i < indices.Length; i++) Check(indices[i] == fixture.Indices[i], "Index order or UInt32 value changed.");
            Bounds bounds = mesh.bounds;
            Check(Same(bounds.center,submesh.bounds.center) && Same(bounds.extents,submesh.bounds.extents),
                $"Mesh/submesh bounds differ: mesh={bounds}, submesh={submesh.bounds}.");
            if (count == 0) Check(Same(bounds.center,Vector3.zero) && Same(bounds.extents,Vector3.zero), $"Empty mesh bounds are nonzero: actual={bounds}.");
            for (int i = 0; i < count; i++)
            {
                Vector3 delta = fixture.Positions[i]-bounds.center, extents = bounds.extents;
                Check(Mathf.Abs(delta.x) <= extents.x+.001f && Mathf.Abs(delta.y) <= extents.y+.001f && Mathf.Abs(delta.z) <= extents.z+.001f, "Native bounds exclude a source vertex.");
            }
        }

        static void VerifyNativeChannels(Mesh mesh, Fixture fixture, VertexAttributeDescriptor[] attributes, CaseResult result)
        {
            int count = fixture.Positions.Count;
            Check(result.actualVertexBufferCount == 1, $"Wrong vertex stream count: actual={result.actualVertexBufferCount}, expected=1, vertices={count}.");
            Check(result.actualStride == result.stride, $"Native vertex stride differs: actual={result.actualStride}, expected={result.stride}.");
            Check(mesh.indexFormat == IndexFormat.UInt32, $"Wrong index format: actual={mesh.indexFormat}, expected=UInt32.");
            Check(attributes.Length == (fixture.Water ? 5 : 9), $"Unexpected shader-input attribute count: actual={attributes.Length}, expected={(fixture.Water ? 5 : 9)}.");
            Check(attributes[0].attribute == VertexAttribute.Position && attributes[0].dimension == 3 && attributes[0].format == VertexAttributeFormat.Float32, "Position input changed.");
            Check(attributes[1].attribute == VertexAttribute.Normal && attributes[1].dimension == 3 && attributes[1].format == VertexAttributeFormat.Float32, "Normal input changed.");
            Check(attributes[2].attribute == VertexAttribute.Color && attributes[2].dimension == 4 &&
                attributes[2].format == (fixture.Water ? VertexAttributeFormat.UNorm8 : VertexAttributeFormat.Float32), "Color shader input changed.");
            foreach (var attribute in attributes) Check(attribute.stream == 0, "Attribute uses an unexpected stream.");
            var positions = new List<Vector3>(); var normals = new List<Vector3>();
            mesh.GetVertices(positions); mesh.GetNormals(normals);
            Check(positions.Count == count && normals.Count == count, "Native position/normal readback count changed.");
            for (int i = 0; i < count; i++)
            { Check(Same(positions[i],fixture.Positions[i]), "Position bytes changed."); Check(Same(normals[i],fixture.Normals[i]), "Normal bytes changed."); }
            if (fixture.Water)
            {
                var colors = new List<Color32>(); mesh.GetColors(colors); Check(colors.Count == count, "Water color count changed.");
                for (int i = 0; i < count; i++)
                { Color32 expected = fixture.Colors[i], actual = colors[i]; Check(actual.r == expected.r && actual.g == expected.g && actual.b == expected.b && actual.a == expected.a, "Water Color32 changed."); }
            }
            else
            {
                var colors = new List<Color>(); mesh.GetColors(colors); Check(colors.Count == count, "Terrain color count changed.");
                for (int i = 0; i < count; i++) Check(Same(colors[i],fixture.Colors[i]), "Float color bytes changed.");
            }
            for (int channel = 0; channel < (fixture.Water ? 2 : 6); channel++)
            {
                var attribute = attributes[channel+3];
                Check(attribute.attribute == VertexAttribute.TexCoord0+channel && attribute.dimension == 4 && attribute.format == VertexAttributeFormat.Float32, "UV shader input changed.");
                var values = new List<Vector4>(); mesh.GetUVs(channel,values); Check(values.Count == count, "UV readback count changed.");
                for (int i = 0; i < count; i++) Check(Same(values[i],fixture.UV[channel][i]), "UV component bytes changed.");
            }
        }
    }
}
