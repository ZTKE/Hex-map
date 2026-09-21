using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace IcoSphere {
    public class PosVert {
        public readonly Vector3 p;
        public readonly Int32 v;

        public PosVert() {
            v = -1;
        }

        public PosVert(Vector3 p, Int32 v) {
            this.p = p;
            this.v = v;
        }

        public static int BinarySearch(PosVert[] pv, Vector3 target) {
            PosVert finder = new(target, -1);
            return Array.BinarySearch(pv, finder, new PosVertComparer());
        }
    }

    public class PosVertComparer : IComparer<PosVert> {
        public int Compare(PosVert l, PosVert r) {
            if (l.p.x != r.p.x) {
                return l.p.x.CompareTo(r.p.x);
            }
            if (l.p.y != r.p.y) {
                return l.p.y.CompareTo(r.p.y);
            }
            return l.p.z.CompareTo(r.p.z);
        }
    }
}
