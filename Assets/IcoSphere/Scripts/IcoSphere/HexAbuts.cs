using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace IcoSphere {
    // 六边形或者五边形的毗邻数据
    // 使用时以中心顶点序号v为下标往外扩散连接周围的vn
    // 然后划分出来的三角形为tn
    //   --v4---v5--
    //    / \ t4/ \
    // \ / t3\ /t5 \ /
    //  v3----v----v0
    // / \ t2/ \t0 / \
    //    \ /t1 \ /
    //   --v2---v1--
    // 与C++版本不同, 这里为了提升性能, 直接创建一堆变量模拟数组结构, 从而避免new
    public readonly struct HexAbuts {
        private readonly Int32 v0; private readonly Abut a0;
        private readonly Int32 v1; private readonly Abut a1;
        private readonly Int32 v2; private readonly Abut a2;
        private readonly Int32 v3; private readonly Abut a3;
        private readonly Int32 v4; private readonly Abut a4;
        private readonly Int32 v5; private readonly Abut a5;

        public HexAbuts(
            Int32 v0, Abut a0,
            Int32 v1, Abut a1,
            Int32 v2, Abut a2,
            Int32 v3, Abut a3,
            Int32 v4, Abut a4,
            Int32 v5, Abut a5) {
            this.v0 = v0; this.a0 = a0;
            this.v1 = v1; this.a1 = a1;
            this.v2 = v2; this.a2 = a2;
            this.v3 = v3; this.a3 = a3;
            this.v4 = v4; this.a4 = a4;
            this.v5 = v5; this.a5 = a5;
        }

        public readonly Int32 V(int idx) {
            return idx switch {
                0 => v0,
                1 => v1,
                2 => v2,
                3 => v3,
                4 => v4,
                5 => v5,
                _ => throw new IndexOutOfRangeException()
            };
        }

        public readonly Abut A(int idx) {
            return idx switch {
                0 => a0,
                1 => a1,
                2 => a2,
                3 => a3,
                4 => a4,
                5 => a5,
                _ => throw new IndexOutOfRangeException()
            };
        }
    }
}
