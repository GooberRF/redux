using redux.utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace redux.exporters
{
    public static class ObjExporter
    {
        private const string logSrc = "ObjExporter";

        public static void ExportObj(Mesh mesh, string objPath)
        {
            var objDir = Path.GetDirectoryName(objPath)!;
            var baseName = Path.GetFileNameWithoutExtension(objPath);
            var mtlName = baseName + ".mtl";
            var mtlPath = Path.Combine(objDir, mtlName);

            Logger.Info(logSrc, $"Writing OBJ to {objPath}");
            Logger.Info(logSrc, $"Writing MTL to {mtlPath}");

            using var obj = new StreamWriter(objPath);
            using var mtl = new StreamWriter(mtlPath);

            obj.WriteLine($"mtllib {mtlName}");
            Logger.Dev(logSrc, $"Added index for material {mtlPath}");

            var writtenMats = new HashSet<string>();
            int vOffset = 1, vtOffset = 1, vnOffset = 1;

            foreach (var brush in mesh.Brushes)
            {
                if (brush.Vertices.Count == 0) continue;

                var name = $"Brush_{brush.UID}";

                // Add brush flags to the name
                // Used to track flags between conversions
                if (!Config.SimpleBrushNames)
                {
                    bool isAir = (brush.Solid.Flags & (uint)SolidFlags.Air) != 0;
                    bool isDetail = (brush.Solid.Flags & (uint)SolidFlags.Detail) != 0;
                    bool isPortal = (brush.Solid.Flags & (uint)SolidFlags.Portal) != 0;
                    bool isEmitsSteam = (brush.Solid.Flags & (uint)SolidFlags.EmitsSteam) != 0;
                    bool isGeoable = (brush.Solid.Flags & (uint)SolidFlags.Geoable) != 0;

                    name = $"Brush_{brush.UID}_{(isAir ? "A" : "S")}_{(isDetail ? "D" : "nD")}_{(isPortal ? "P" : "nP")}_{(isEmitsSteam ? "ES" : "nES")}_{(isGeoable ? "G" : "nG")}";
                }
                obj.WriteLine($"o {name}");

                // Merge verts by position in world space only 
                var posPool = new Dictionary<
                    (int x, int y, int z),
                    int // vi
                >();

                var V = new List<Vector3>(); // verts in world space

                // Merge verts by quantized UVs (u, v) per vertex index (vi):
                var uvPool = new Dictionary<
                    (int vi, int u, int v),
                    int // vti
                >();

                var VT = new List<Vector2>(); // vert UVs

                var VN = new List<Vector3>(); // vert normals

                // Group indices per material
                var facesByMat = new Dictionary<string, List<List<(int vi, int vti, int vni)>>>();

                foreach (var face in brush.Solid.Faces)
                {
                    // Compute a flat normal for the face in world space
                    var w0 = Vector3.Transform(
                        brush.Vertices[face.Vertices[0]],
                        brush.RotationMatrix
                    ) + brush.Position;

                    Vector3 flatNorm = Vector3.Zero;
                    for (int i = 1; i + 1 < face.Vertices.Count; i++)
                    {
                        var w1 = Vector3.Transform(
                            brush.Vertices[face.Vertices[i]],
                            brush.RotationMatrix
                        ) + brush.Position;

                        var w2 = Vector3.Transform(
                            brush.Vertices[face.Vertices[i + 1]],
                            brush.RotationMatrix
                        ) + brush.Position;

                        flatNorm += Vector3.Cross(w1 - w0, w2 - w0);
                    }
                    flatNorm = Vector3.Normalize(flatNorm);

                    // Get the texture for the face
                    string tex = (face.TextureIndex >= 0 && face.TextureIndex < brush.Solid.Textures.Count)
                                 ? brush.Solid.Textures[face.TextureIndex]
                                 : "missing_texture.tga";
                    string mat = Path.GetFileNameWithoutExtension(tex);

                    if (!facesByMat.TryGetValue(mat, out var listOfFaces))
                        facesByMat[mat] = listOfFaces = new List<List<(int, int, int)>>();

                    // Compute the vertex indices (vi, vti, vni) for the face
                    var idxTriples = new List<(int vi, int vti, int vni)>();

                    for (int i = 0; i < face.Vertices.Count; i++)
                    {
                        // World space position of the vertex
                        var p = Vector3.Transform(
                            brush.Vertices[face.Vertices[i]],
                            brush.RotationMatrix
                        ) + brush.Position;

                        // Quantize world space position to integer by 0.001
                        var pKey = (
                            x: (int)Math.Round(p.X * 1000f),
                            y: (int)Math.Round(p.Y * 1000f),
                            z: (int)Math.Round(p.Z * 1000f)
                        );

                        // If this is a new position, assign a new vi
                        if (!posPool.TryGetValue(pKey, out int vi))
                        {
                            vi = V.Count;
                            posPool[pKey] = vi;
                            V.Add(p);
                        }

                        // UV of the vertex
                        var uv = (i < face.UVs.Count)
                                 ? face.UVs[i]
                                 : Vector2.Zero;

                        // Quantize UV to integer by 0.001
                        var uvKey = (
                            u: (int)Math.Round(uv.X * 1000f),
                            v: (int)Math.Round(uv.Y * 1000f)
                        );

                        // Make key for the uvPool (vi, quantized_u, quantized_v)
                        var fullUVKey = (vi: vi, u: uvKey.u, v: uvKey.v);

                        // If this is a new UV for this vertex, assign a new vti
                        if (!uvPool.TryGetValue(fullUVKey, out int vti))
                        {
                            vti = VT.Count;
                            uvPool[fullUVKey] = vti;
                            VT.Add(uv);
                            VN.Add(flatNorm); // store this face’s normal for (vi, vti)
                        }

                        idxTriples.Add((vi, vti, vti));
                        // Use the same index for vni so that vn[vti] is the flat normal
                    }

                    // Triganulate if needed before adding the face
                    if (Config.TriangulatePolygons && idxTriples.Count > 3)
                    {
                        for (int i = 1; i + 1 < idxTriples.Count; i++)
                            listOfFaces.Add(new List<(int, int, int)> {
                                idxTriples[0],
                                idxTriples[i],
                                idxTriples[i + 1]
                            });
                    }
                    else
                    {
                        listOfFaces.Add(idxTriples);
                    }
                }

                // Write unique positions (V)
                // OBJ format uses right-handed coordinate system, so flip X to match RF (left-handed)
                foreach (var p in V)
                    obj.WriteLine(
                        $"v {(-p.X).ToString(CultureInfo.InvariantCulture)} " +
                        $"{p.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{p.Z.ToString(CultureInfo.InvariantCulture)}"
                    );

                // Write unique UVs (VT)
                foreach (var uv in VT)
                    obj.WriteLine(
                        $"vt {uv.X.ToString(CultureInfo.InvariantCulture)} " +
                        $"{(1 - uv.Y).ToString(CultureInfo.InvariantCulture)}"
                    );

                // Write unique normals (VN)
                foreach (var n in VN)
                    obj.WriteLine(
                        $"vn {n.X.ToString(CultureInfo.InvariantCulture)} " +
                        $"{n.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{n.Z.ToString(CultureInfo.InvariantCulture)}"
                    );

                // Write faces grouped by material
                // Use pooled indices and global offsets
                foreach (var kv in facesByMat)
                {
                    var mat = kv.Key;
                    if (writtenMats.Add(mat))
                    {
                        mtl.WriteLine($"newmtl {mat}");
                        mtl.WriteLine("Ka 1.000 1.000 1.000");
                        mtl.WriteLine("Kd 1.000 1.000 1.000");
                        mtl.WriteLine("Ks 0.000 0.000 0.000");
                        mtl.WriteLine("d 1.0");
                        mtl.WriteLine("illum 1");
                        mtl.WriteLine($"map_Kd {mat}.tga");
                        mtl.WriteLine();
                    }

                    obj.WriteLine($"usemtl {mat}");
                    foreach (var face in kv.Value)
                    {
                        face.Reverse();  // correct winding so face normals point outward
                        var faceTokens = face
                            .Select(t => $"{t.vi + vOffset}/{t.vti + vtOffset}/{t.vni + vnOffset}")
                            .ToArray();
                        obj.WriteLine("f " + string.Join(" ", faceTokens));
                    }
                }

                // Increment global vertex offsets before parsing next brush
                vOffset += V.Count;
                vtOffset += VT.Count;
                vnOffset += VN.Count;

                foreach (var prop in brush.PropPoints)
                {
                    // Prop object name: Prop_<brushID>_<propName>
                    string safeName = Path.GetInvalidFileNameChars()
                        .Aggregate(prop.Name, (s, c) => s.Replace(c, '='));
                    string objName = $"RFPP={brush.UID}={safeName}";
                    obj.WriteLine($"o {objName}");

                    // Compute world‐space position of the PropPoint:
                    var wp = Vector3.Transform(prop.Position, brush.RotationMatrix)
                             + brush.Position;

                    // Compute the “forward” direction from the quaternion, then flip it:
                    var forward = Vector3.Transform(new Vector3(0, 0, 1), prop.Orientation);
                    forward = Vector3.Normalize(forward);
                    forward = -forward; // flip direction

                    float arrowLength = 1.0f; // shaft length
                    float wingLength = 0.25f; // arrowhead wing length

                    // Tip of arrow = base + flipped_forward * arrowLength
                    var tip = wp + forward * arrowLength;

                    // Choose a “right” vector perpendicular to flipped forward:
                    Vector3 worldUp = Vector3.UnitY;
                    Vector3 right = Vector3.Cross(forward, worldUp);
                    if (right.LengthSquared() < 1e-6f)
                        right = Vector3.Cross(forward, Vector3.UnitX);
                    right = Vector3.Normalize(right);

                    // Arrowhead wings:
                    var wingBase = tip - forward * wingLength;
                    var wing1 = wingBase + right * wingLength;
                    var wing2 = wingBase - right * wingLength;

                    // 1) Write base vertex:
                    obj.WriteLine(
                        $"v {(-wp.X).ToString(CultureInfo.InvariantCulture)} " +
                        $"{wp.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{wp.Z.ToString(CultureInfo.InvariantCulture)}"
                    );

                    // 2) Write tip vertex:
                    obj.WriteLine(
                        $"v {(-tip.X).ToString(CultureInfo.InvariantCulture)} " +
                        $"{tip.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{tip.Z.ToString(CultureInfo.InvariantCulture)}"
                    );

                    // 3) Write wing1 vertex:
                    obj.WriteLine(
                        $"v {(-wing1.X).ToString(CultureInfo.InvariantCulture)} " +
                        $"{wing1.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{wing1.Z.ToString(CultureInfo.InvariantCulture)}"
                    );

                    // 4) Write wing2 vertex:
                    obj.WriteLine(
                        $"v {(-wing2.X).ToString(CultureInfo.InvariantCulture)} " +
                        $"{wing2.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{wing2.Z.ToString(CultureInfo.InvariantCulture)}"
                    );

                    // 5) Draw shaft: line from base (index=vOffset) to tip (index=vOffset+1)
                    obj.WriteLine($"l {vOffset} {vOffset + 1}");

                    // 6) Draw arrowhead wings:
                    obj.WriteLine($"l {vOffset + 1} {vOffset + 2}"); // tip → wing1
                    obj.WriteLine($"l {vOffset + 1} {vOffset + 3}"); // tip → wing2

                    // 7) (Optional) Write orientation quaternion as a comment
                    obj.WriteLine(
                        "# quat " +
                        $"{prop.Orientation.X.ToString(CultureInfo.InvariantCulture)} " +
                        $"{prop.Orientation.Y.ToString(CultureInfo.InvariantCulture)} " +
                        $"{prop.Orientation.Z.ToString(CultureInfo.InvariantCulture)} " +
                        $"{prop.Orientation.W.ToString(CultureInfo.InvariantCulture)}"
                    );

                    // 8) (Optional) Write parent‐index as a comment
                    obj.WriteLine($"# parent {prop.ParentIndex}");

                    Logger.Dev(logSrc, $"Wrote prop point {prop.Name} with parent {prop.ParentIndex}, " +
                                        $"vertices vIndices={vOffset},{vOffset + 1},{vOffset + 2},{vOffset + 3}");

                    // 9) We used four “v” lines → bump vOffset by 4
                    vOffset += 4;
                }

                Logger.Dev(logSrc, $"Wrote brush {brush.UID}");
            }

            int stacks = 6, slices = 6;
            for (int i = 0; i < mesh.CollisionSpheres.Count; i++)
            {
                var cs = mesh.CollisionSpheres[i];
                string safeName = Path.GetInvalidFileNameChars()
                    .Aggregate(cs.Name, (s, c) => s.Replace(c, '_'));
                string objName = $"CSphere_{i}_{safeName}";
                obj.WriteLine($"o {objName}");

                var center = cs.Position;
                float radius = cs.Radius;

                // Generate (stacks+1)×slices vertices on sphere
                // iLat = 0..stacks, iLon = 0..slices-1
                for (int iLat = 0; iLat <= stacks; iLat++)
                {
                    float theta = (float)Math.PI * iLat / stacks; // 0..π
                    float sinT = (float)Math.Sin(theta);
                    float cosT = (float)Math.Cos(theta);

                    for (int iLon = 0; iLon < slices; iLon++)
                    {
                        float phi = 2f * (float)Math.PI * iLon / slices; // 0..2π
                        float sinP = (float)Math.Sin(phi);
                        float cosP = (float)Math.Cos(phi);

                        // Base sphere point (before scaling/translating):
                        // x = sinθ·cosφ, y = cosθ, z = sinθ·sinφ
                        var p = new Vector3(
                            sinT * cosP,
                            cosT,
                            sinT * sinP
                        );

                        // Scale by radius, translate by center
                        p = center + p * radius;

                        // Flip X for OBJ right‐handed
                        obj.WriteLine(
                            $"v {(-p.X).ToString(CultureInfo.InvariantCulture)} " +
                            $"{p.Y.ToString(CultureInfo.InvariantCulture)} " +
                            $"{p.Z.ToString(CultureInfo.InvariantCulture)}"
                        );
                    }
                }

                // Build faces (triangles) between these vertices
                // Indexing: baseIndex = vOffset, then
                // index(iLat, iLon) = baseIndex + iLat*slices + iLon
                int baseIndex = vOffset;
                for (int iLat = 0; iLat < stacks; iLat++)
                {
                    for (int iLon = 0; iLon < slices; iLon++)
                    {
                        int nextLon = (iLon + 1) % slices;
                        int i0 = baseIndex + iLat * slices + iLon;
                        int i1 = baseIndex + iLat * slices + nextLon;
                        int i2 = baseIndex + (iLat + 1) * slices + iLon;
                        int i3 = baseIndex + (iLat + 1) * slices + nextLon;

                        if (iLat == 0)
                        {
                            // Top cap: one triangle (i0, i2, i3)
                            obj.WriteLine($"f {i0} {i2} {i3}");
                        }
                        else if (iLat == stacks - 1)
                        {
                            // Bottom cap: one triangle (i0, i2, i1)
                            obj.WriteLine($"f {i0} {i2} {i1}");
                        }
                        else
                        {
                            // Middle: two triangles
                            obj.WriteLine($"f {i0} {i2} {i1}");
                            obj.WriteLine($"f {i1} {i2} {i3}");
                        }
                    }
                }

                // Write radius comment for reconstruction
                obj.WriteLine($"# radius {radius.ToString(CultureInfo.InvariantCulture)}");

                Logger.Dev(logSrc, $"Wrote CSphere '{cs.Name}' as sphere with center ({center.X},{center.Y},{center.Z}), " +
                                    $"radius={radius}, vIndices={vOffset}..{vOffset + (stacks + 1) * slices - 1}");

                vOffset += (stacks + 1) * slices;
            }

            Logger.Info(logSrc, $"{objPath} and {mtlPath} written successfully.");
        }
    }
}
