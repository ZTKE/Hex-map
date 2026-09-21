using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace IcoSphere {
    // 毗邻三角形序号数据
    public readonly struct Abut {
        private readonly Int32 t0;
        private readonly Int32 t1;

        public Abut(Int32 t0, Int32 t1) {
            this.t0 = t0;
            this.t1 = t1;
        }

        public readonly Int32 this[int idx] {
            get {
                return idx switch {
                    0 => t0,
                    1 => t1,
                    _ => throw new IndexOutOfRangeException()
                };
            }
        }
    }
}
