using System;
using System.IO;
using UnityEngine;

namespace IcoSphere {
    // 二进制模型数据打包
    public struct Pack {
        // Resources文件夹路径
        public static readonly string RES_DEFAULT_PATH = "Bin/pack_arr_";

        public Vector3[] verts;
        public Tri[] tris;
        public HexAbuts[] abuts;
        public Vector3[] ctrs;
        public Tri[] adjTris;
        public PosVert[] posVerts;

        public static Pack Read(int recursion) {
            string resFilePath = RES_DEFAULT_PATH + recursion;
            Pack pack = new();

            try {
                // 从Resources加载TextAsset, 二进制文件需要作为TextAsset导入
                TextAsset binData = Resources.Load<TextAsset>(resFilePath);

                if (binData == null) {
                    Debug.LogError($"Resources中未找到文件: {resFilePath} (请确保文件放在Resources文件夹下, 并且后缀名不能为.bin, Unity无法解析.bin文件, 建议是.bytes)");
                    return pack;
                }

                byte[] bytes = binData.bytes;

                using MemoryStream ms = new(bytes);
                using BinaryReader reader = new(ms);

                // 读取头部信息
                Int32 vertsSize = reader.ReadInt32();
                Int32 trisSize = reader.ReadInt32();
                Int32 abutsSize = reader.ReadInt32();
                Int32 ctrsSize = reader.ReadInt32();
                Int32 adjTrisSize = reader.ReadInt32();
                Int32 posVertsSize = reader.ReadInt32();

                // 读取顶点数据
                pack.verts = new Vector3[vertsSize];
                for (Int32 i = 0; i < vertsSize; ++i) {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    pack.verts[i] = new(x, y, z);
                }

                // 读取三角形数据
                pack.tris = new Tri[trisSize];
                for (Int32 i = 0; i < trisSize; ++i) {
                    Int32 v0 = reader.ReadInt32();
                    Int32 v1 = reader.ReadInt32();
                    Int32 v2 = reader.ReadInt32();
                    pack.tris[i] = new(v0, v1, v2);
                }

                // 读取毗邻数据
                pack.abuts = new HexAbuts[abutsSize];
                for (Int32 i = 0; i < abutsSize; ++i) {
                    Int32 v0 = reader.ReadInt32();
                    Int32 a0t0 = reader.ReadInt32();
                    Int32 a0t1 = reader.ReadInt32();
                    Abut a0 = new(a0t0, a0t1);

                    Int32 v1 = reader.ReadInt32();
                    Int32 a1t0 = reader.ReadInt32();
                    Int32 a1t1 = reader.ReadInt32();
                    Abut a1 = new(a1t0, a1t1);

                    Int32 v2 = reader.ReadInt32();
                    Int32 a2t0 = reader.ReadInt32();
                    Int32 a2t1 = reader.ReadInt32();
                    Abut a2 = new(a2t0, a2t1);

                    Int32 v3 = reader.ReadInt32();
                    Int32 a3t0 = reader.ReadInt32();
                    Int32 a3t1 = reader.ReadInt32();
                    Abut a3 = new(a3t0, a3t1);

                    Int32 v4 = reader.ReadInt32();
                    Int32 a4t0 = reader.ReadInt32();
                    Int32 a4t1 = reader.ReadInt32();
                    Abut a4 = new(a4t0, a4t1);

                    Int32 v5 = reader.ReadInt32();
                    Int32 a5t0 = reader.ReadInt32();
                    Int32 a5t1 = reader.ReadInt32();
                    Abut a5 = new(a5t0, a5t1);

                    pack.abuts[i] = new(
                        v0, a0,
                        v1, a1,
                        v2, a2,
                        v3, a3,
                        v4, a4,
                        v5, a5
                    );
                }

                // 读取毗邻三角形中心坐标数据
                pack.ctrs = new Vector3[ctrsSize];
                for (Int32 i = 0; i < ctrsSize; ++i) {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    pack.ctrs[i] = new(x, y, z);
                }

                // 读取毗邻三角形序号数据
                pack.adjTris = new Tri[adjTrisSize];
                for (Int32 i = 0; i < adjTrisSize; ++i) {
                    Int32 t01 = reader.ReadInt32();
                    Int32 t12 = reader.ReadInt32();
                    Int32 t20 = reader.ReadInt32();
                    pack.adjTris[i] = new(t01, t12, t20);
                }

                // 读取坐标顶点有序列表数据
                pack.posVerts = new PosVert[posVertsSize];
                for (Int32 i = 0; i < posVertsSize; ++i) {
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float z = reader.ReadSingle();
                    Int32 v = reader.ReadInt32();
                    pack.posVerts[i] = new(new(x, y, z), v);
                }

                //Debug.Log($"Resources反序列化成功: {resFilePath}, 顶点数: {vertsSize}, 三角形数: {trisSize}, 毗邻数据数: {abutsSize}, 毗邻三角形中心坐标数: {ctrsSize}, 毗邻三角形序号数: {adjTrisSize}, 坐标顶点有序列表数: {posVertsSize}");
                return pack;
            } catch (Exception e) {
                Debug.LogError($"反序列化失败: {e.Message}\n{e.StackTrace}");
                return pack;
            }
        }
    }
}
