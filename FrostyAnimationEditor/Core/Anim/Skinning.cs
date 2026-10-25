using System;
using System.Collections.Generic;

namespace FrostyAnimationEditor.Core.Anim
{
    /// <summary>a skinned mesh in the skeleton's bind pose (model space, meters)</summary>
    public sealed class MeshPart
    {
        public string Name = "mesh";
        public string Source = "";
        public float[] Positions;      // x, y, z per vertex
        public float[] Normals;        // optional, x, y, z per vertex
        public float[] Uvs;            // optional, u, v per vertex
        public int[] Triangles;        // 3 vertex indices per triangle
        public int Influences;         // bone slots per vertex
        public int[] BoneIndices;      // Influences per vertex: skeleton bone index, or -1
        public float[] BoneWeights;    // Influences per vertex
        public int VertexCount { get { return Positions != null ? Positions.Length / 3 : 0; } }
        public int TriangleCount { get { return Triangles != null ? Triangles.Length / 3 : 0; } }

        /// <summary>true when most triangles' winding (a, b, c counter-clockwise) agrees with the vertex normals</summary>
        public bool WindingMatchesNormals()
        {
            if (Normals == null || Triangles == null) return true;
            int agree = 0, total = 0, nv = VertexCount;
            for (int t = 0; t + 2 < Triangles.Length; t += 3)
            {
                int a = Triangles[t], b = Triangles[t + 1], c = Triangles[t + 2];
                if (a >= nv || b >= nv || c >= nv) continue;
                double ux = Positions[b * 3] - Positions[a * 3], uy = Positions[b * 3 + 1] - Positions[a * 3 + 1], uz = Positions[b * 3 + 2] - Positions[a * 3 + 2];
                double vx = Positions[c * 3] - Positions[a * 3], vy = Positions[c * 3 + 1] - Positions[a * 3 + 1], vz = Positions[c * 3 + 2] - Positions[a * 3 + 2];
                double fx = uy * vz - uz * vy, fy = uz * vx - ux * vz, fz = ux * vy - uy * vx;
                double nx = Normals[a * 3] + Normals[b * 3] + Normals[c * 3], ny = Normals[a * 3 + 1] + Normals[b * 3 + 1] + Normals[c * 3 + 1], nz = Normals[a * 3 + 2] + Normals[b * 3 + 2] + Normals[c * 3 + 2];
                double d = fx * nx + fy * ny + fz * nz;
                if (d == 0) continue;
                total++;
                if (d > 0) agree++;
            }
            return agree * 2 >= total;
        }

        /// <summary>smooth per-vertex normals from the triangles (when the mesh has none)</summary>
        public void ComputeNormals()
        {
            int nv = VertexCount;
            float[] n = new float[nv * 3];
            for (int t = 0; t + 2 < Triangles.Length; t += 3)
            {
                int a = Triangles[t], b = Triangles[t + 1], c = Triangles[t + 2];
                if (a >= nv || b >= nv || c >= nv) continue;
                double ux = Positions[b * 3] - Positions[a * 3], uy = Positions[b * 3 + 1] - Positions[a * 3 + 1], uz = Positions[b * 3 + 2] - Positions[a * 3 + 2];
                double vx = Positions[c * 3] - Positions[a * 3], vy = Positions[c * 3 + 1] - Positions[a * 3 + 1], vz = Positions[c * 3 + 2] - Positions[a * 3 + 2];
                float nx = (float)(uy * vz - uz * vy), ny = (float)(uz * vx - ux * vz), nz = (float)(ux * vy - uy * vx);
                foreach (int k in new[] { a, b, c }) { n[k * 3] += nx; n[k * 3 + 1] += ny; n[k * 3 + 2] += nz; }
            }
            for (int v = 0; v < nv; v++)
            {
                double l = Math.Sqrt(n[v * 3] * n[v * 3] + n[v * 3 + 1] * n[v * 3 + 1] + n[v * 3 + 2] * n[v * 3 + 2]);
                if (l > 0) { n[v * 3] = (float)(n[v * 3] / l); n[v * 3 + 1] = (float)(n[v * 3 + 1] / l); n[v * 3 + 2] = (float)(n[v * 3 + 2] / l); }
            }
            // the triangle order (winding) decides which side the normals point to; body parts wrap around their
            // centre, so the normals should point away from it on average
            double cx = 0, cy = 0, cz = 0;
            for (int v = 0; v < nv; v++) { cx += Positions[v * 3]; cy += Positions[v * 3 + 1]; cz += Positions[v * 3 + 2]; }
            if (nv > 0) { cx /= nv; cy /= nv; cz /= nv; }
            double outward = 0;
            for (int v = 0; v < nv; v++) outward += n[v * 3] * (Positions[v * 3] - cx) + n[v * 3 + 1] * (Positions[v * 3 + 1] - cy) + n[v * 3 + 2] * (Positions[v * 3 + 2] - cz);
            if (outward < 0) for (int i = 0; i < n.Length; i++) n[i] = -n[i];
            Normals = n;
        }
    }

    /// <summary>linear blend skinning on the CPU: v' = sum_i w_i * (World_i * BindWorld_i^-1) * v</summary>
    public static class Skinning
    {
        /// <summary>one 3x4 matrix per bone (12 floats, row-major)</summary>
        public static float[] Matrices(PlayerSkeleton sk, Affine[] world, float[] into = null)
        {
            int n = sk.Count;
            float[] m = into != null && into.Length >= n * 12 ? into : new float[n * 12];
            for (int b = 0; b < n; b++)
            {
                Affine s = world[b] * sk.Model[b].Inverse();
                int o = b * 12;
                m[o] = (float)s.M00; m[o + 1] = (float)s.M01; m[o + 2] = (float)s.M02; m[o + 3] = (float)s.M03;
                m[o + 4] = (float)s.M10; m[o + 5] = (float)s.M11; m[o + 6] = (float)s.M12; m[o + 7] = (float)s.M13;
                m[o + 8] = (float)s.M20; m[o + 9] = (float)s.M21; m[o + 10] = (float)s.M22; m[o + 11] = (float)s.M23;
            }
            return m;
        }

        /// <summary>skinned positions (and normals when nrm is given); vertices without weights keep the bind pose</summary>
        public static void Apply(float[] mats, MeshPart mesh, float[] pos, float[] nrm)
        {
            int nv = mesh.VertexCount, inf = mesh.Influences;
            float[] p = mesh.Positions, n = mesh.Normals;
            int nb = mats.Length / 12;
            for (int v = 0; v < nv; v++)
            {
                float px = p[v * 3], py = p[v * 3 + 1], pz = p[v * 3 + 2];
                float ox = 0, oy = 0, oz = 0, nx = 0, ny = 0, nz = 0, wsum = 0;
                float qx = 0, qy = 0, qz = 0;
                if (n != null && nrm != null) { qx = n[v * 3]; qy = n[v * 3 + 1]; qz = n[v * 3 + 2]; }
                for (int k = 0; k < inf; k++)
                {
                    float w = mesh.BoneWeights[v * inf + k];
                    int b = mesh.BoneIndices[v * inf + k];
                    if (w <= 0 || b < 0 || b >= nb) continue;
                    int o = b * 12;
                    ox += w * (mats[o] * px + mats[o + 1] * py + mats[o + 2] * pz + mats[o + 3]);
                    oy += w * (mats[o + 4] * px + mats[o + 5] * py + mats[o + 6] * pz + mats[o + 7]);
                    oz += w * (mats[o + 8] * px + mats[o + 9] * py + mats[o + 10] * pz + mats[o + 11]);
                    if (nrm != null)
                    {
                        nx += w * (mats[o] * qx + mats[o + 1] * qy + mats[o + 2] * qz);
                        ny += w * (mats[o + 4] * qx + mats[o + 5] * qy + mats[o + 6] * qz);
                        nz += w * (mats[o + 8] * qx + mats[o + 9] * qy + mats[o + 10] * qz);
                    }
                    wsum += w;
                }
                if (wsum <= 0) { ox = px; oy = py; oz = pz; nx = qx; ny = qy; nz = qz; }
                else if (Math.Abs(wsum - 1) > 1e-4f)
                {
                    float iw = 1 / wsum;
                    ox *= iw; oy *= iw; oz *= iw;
                }
                pos[v * 3] = ox; pos[v * 3 + 1] = oy; pos[v * 3 + 2] = oz;
                if (nrm != null)
                {
                    float l = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (l > 0) { nx /= l; ny /= l; nz /= l; }
                    nrm[v * 3] = nx; nrm[v * 3 + 1] = ny; nrm[v * 3 + 2] = nz;
                }
            }
        }
    }
}
