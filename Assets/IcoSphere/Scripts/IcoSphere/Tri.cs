using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace IcoSphere {
    // 三角形顶点序号数据
    public readonly struct Tri {
        private readonly Int32 v0;
        private readonly Int32 v1;
        private readonly Int32 v2;

        public Tri(Int32 v0, Int32 v1, Int32 v2) {
            this.v0 = v0;
            this.v1 = v1;
            this.v2 = v2;
        }

        public readonly Int32 this[int idx] {
            get {
                return idx switch {
                    0 => v0,
                    1 => v1,
                    2 => v2,
                    _ => throw new IndexOutOfRangeException()
                };
            }
        }
    }
}
