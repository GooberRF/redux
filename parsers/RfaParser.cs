// RfaParser.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using redux.utilities;

namespace redux.parsers
{
    
    public static class RfaParser
    {
        private const string logSrc = "RfaParser";
        private const int EXPECTED_MAGIC = 0x46564D56;

        public static RfaFile ReadRfa(string path)
        {
            Logger.Debug(logSrc, $"ReadRfa: Opening \"{path}\" for reading.");
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            var result = new RfaFile();

            result.Header = ReadHeader(reader);
            result.Bones = ReadBones(reader, result.Header);
            result.MorphVertexMappings = ReadMorphVertexMappings(reader, result.Header);
            result.MorphKeyframes = ReadMorphKeyframes(reader, result.Header);

            Logger.Debug(logSrc, $"ReadRfa: Finished reading \"{path}\". Total bones: {result.Header.NumBones}, " +
                                  $"morph vertices: {result.Header.NumMorphVertices}, " +
                                  $"morph keyframes: {result.Header.NumMorphKeyframes}.");
            return result;
        }

        private static RfaHeader ReadHeader(BinaryReader r)
        {
            Logger.Debug(logSrc, "ReadHeader: Seeking to file start (0) and reading header.");
            r.BaseStream.Seek(0, SeekOrigin.Begin);

            var hdr = new RfaHeader();

            hdr.Magic = r.ReadBytes(4);
            int magicInt = BitConverter.ToInt32(hdr.Magic, 0);
            Logger.Debug(logSrc, $"Header: magic bytes = 0x{magicInt:X8} (\"{(char)hdr.Magic[0]}{(char)hdr.Magic[1]}{(char)hdr.Magic[2]}{(char)hdr.Magic[3]}\")");
            if (magicInt != EXPECTED_MAGIC)
                throw new InvalidDataException($"RFA: magic mismatch (got 0x{magicInt:X8}, expected 0x{EXPECTED_MAGIC:X8}).");

            // Version
            hdr.Version = r.ReadInt32();
            Logger.Debug(logSrc, $"Header: version = {hdr.Version}");

            // PosReduction, RotReduction
            hdr.PosReduction = r.ReadSingle();
            hdr.RotReduction = r.ReadSingle();
            Logger.Debug(logSrc, $"Header: pos_reduction = {hdr.PosReduction}, rot_reduction = {hdr.RotReduction}");

            // StartTime, EndTime
            hdr.StartTime = r.ReadInt32();
            hdr.EndTime = r.ReadInt32();
            Logger.Debug(logSrc, $"Header: start_time = {hdr.StartTime}, end_time = {hdr.EndTime}");

            // NumBones, NumMorphVertices, NumMorphKeyframes
            hdr.NumBones = r.ReadInt32();
            hdr.NumMorphVertices = r.ReadInt32();
            hdr.NumMorphKeyframes = r.ReadInt32();
            Logger.Debug(logSrc, $"Header: num_bones = {hdr.NumBones}, num_morph_vertices = {hdr.NumMorphVertices}, num_morph_keyframes = {hdr.NumMorphKeyframes}");

            // RampInTime, RampOutTime
            hdr.RampInTime = r.ReadInt32();
            hdr.RampOutTime = r.ReadInt32();
            Logger.Debug(logSrc, $"Header: ramp_in_time = {hdr.RampInTime}, ramp_out_time = {hdr.RampOutTime}");

            // TotalRotation
            float rx = r.ReadSingle();
            float ry = r.ReadSingle();
            float rz = r.ReadSingle();
            float rw = r.ReadSingle();
            hdr.TotalRotation = new Quaternion(rx, ry, rz, rw);
            Logger.Debug(logSrc, $"Header: total_rotation = ({rx}, {ry}, {rz}, {rw})");

            // TotalTranslation
            float tx = r.ReadSingle();
            float ty = r.ReadSingle();
            float tz = r.ReadSingle();
            hdr.TotalTranslation = new Vector3(tx, ty, tz);
            Logger.Debug(logSrc, $"Header: total_translation = ({tx}, {ty}, {tz})");

            // MorphVertexMappingsOffset, MorphVertexDataOffset
            hdr.MorphVertexMappingsOffset = r.ReadInt32();
            hdr.MorphVertexDataOffset = r.ReadInt32();
            Logger.Debug(logSrc, $"Header: morph_vert_mappings_offset = {hdr.MorphVertexMappingsOffset}, morph_vert_data_offset = {hdr.MorphVertexDataOffset}");

            // BoneOffsets (int32[num_bones])
            hdr.BoneOffsets = new int[hdr.NumBones];
            for (int i = 0; i < hdr.NumBones; i++)
            {
                hdr.BoneOffsets[i] = r.ReadInt32();
                Logger.Debug(logSrc, $"Header: bone_offsets[{i}] = {hdr.BoneOffsets[i]}");
            }

            Logger.Debug(logSrc, "ReadHeader: Completed.");
            return hdr;
        }

        private static List<RfaBone> ReadBones(BinaryReader r, RfaHeader hdr)
        {
            var bones = new List<RfaBone>(hdr.NumBones);
            Logger.Debug(logSrc, $"ReadBones: About to read {hdr.NumBones} bones.");

            for (int i = 0; i < hdr.NumBones; i++)
            {
                int offset = hdr.BoneOffsets[i];
                Logger.Debug(logSrc, $"ReadBones: Seeking to bone[{i}] at absolute offset {offset}.");
                r.BaseStream.Seek(offset, SeekOrigin.Begin);

                var bone = new RfaBone();
                bone.Weight = r.ReadSingle();
                bone.NumRotationKeys = r.ReadInt16();
                bone.NumTranslationKeys = r.ReadInt16();
                Logger.Debug(logSrc, $"Bone[{i}]: weight={bone.Weight}, num_rotation_keys={bone.NumRotationKeys}, num_translation_keys={bone.NumTranslationKeys}");

                // RotationKeys
                bone.RotationKeys = new List<RfaRotationKey>(bone.NumRotationKeys);
                for (int k = 0; k < bone.NumRotationKeys; k++)
                {
                    var rotKey = new RfaRotationKey();
                    rotKey.Time = r.ReadInt32();

                    // 4×short for quaternion
                    short qx = r.ReadInt16();
                    short qy = r.ReadInt16();
                    short qz = r.ReadInt16();
                    short qw = r.ReadInt16();
                    const float SHORT_QUAT_SCALE = 16383f;
                    rotKey.Rotation = new Quaternion(
                        qx / SHORT_QUAT_SCALE,
                        qy / SHORT_QUAT_SCALE,
                        qz / SHORT_QUAT_SCALE,
                        qw / SHORT_QUAT_SCALE
                    );

                    rotKey.EaseIn = r.ReadByte();
                    rotKey.EaseOut = r.ReadByte();
                    r.ReadInt16(); // pad
                    Logger.Debug(logSrc, $"Bone[{i}] RotationKey[{k}]: time={rotKey.Time}, rawQuat=({qx},{qy},{qz},{qw}), normalizedQuat=({rotKey.Rotation.X:F4},{rotKey.Rotation.Y:F4},{rotKey.Rotation.Z:F4},{rotKey.Rotation.W:F4}), easeIn={rotKey.EaseIn}, easeOut={rotKey.EaseOut}");

                    bone.RotationKeys.Add(rotKey);
                }

                // TranslationKeys
                bone.TranslationKeys = new List<RfaTranslationKey>(bone.NumTranslationKeys);
                for (int k = 0; k < bone.NumTranslationKeys; k++)
                {
                    var transKey = new RfaTranslationKey();
                    transKey.Time = r.ReadInt32();

                    float px = r.ReadSingle();
                    float py = r.ReadSingle();
                    float pz = r.ReadSingle();
                    transKey.Translation = new Vector3(px, py, pz);

                    float i0 = r.ReadSingle();
                    float i1 = r.ReadSingle();
                    float i2 = r.ReadSingle();
                    transKey.InTangent = new Vector3(i0, i1, i2);

                    float o0 = r.ReadSingle();
                    float o1 = r.ReadSingle();
                    float o2 = r.ReadSingle();
                    transKey.OutTangent = new Vector3(o0, o1, o2);

                    Logger.Debug(logSrc, $"Bone[{i}] TranslationKey[{k}]: time={transKey.Time}, translation=({px},{py},{pz}), inTangent=({i0},{i1},{i2}), outTangent=({o0},{o1},{o2})");
                    bone.TranslationKeys.Add(transKey);
                }

                bones.Add(bone);
            }

            Logger.Debug(logSrc, "ReadBones: Completed reading all bones.");
            return bones;
        }

        private static short[] ReadMorphVertexMappings(BinaryReader r, RfaHeader hdr)
        {
            if (hdr.NumMorphVertices <= 0 || hdr.MorphVertexMappingsOffset == 0)
            {
                Logger.Debug(logSrc, "ReadMorphVertexMappings: no morph vertices or offset is zero; returning empty array.");
                return Array.Empty<short>();
            }

            Logger.Debug(logSrc, $"ReadMorphVertexMappings: Seeking to offset {hdr.MorphVertexMappingsOffset} for {hdr.NumMorphVertices} entries.");
            r.BaseStream.Seek(hdr.MorphVertexMappingsOffset, SeekOrigin.Begin);

            var mappings = new short[hdr.NumMorphVertices];
            for (int i = 0; i < hdr.NumMorphVertices; i++)
            {
                mappings[i] = r.ReadInt16();
                Logger.Debug(logSrc, $"MorphVertexMapping[{i}] = {mappings[i]}");
            }

            Logger.Debug(logSrc, "ReadMorphVertexMappings: Completed.");
            return mappings;
        }

        private static List<MorphKeyframe> ReadMorphKeyframes(BinaryReader r, RfaHeader hdr)
        {
            var result = new List<MorphKeyframe>();
            if (hdr.Version < 8 || hdr.NumMorphKeyframes <= 0 || hdr.MorphVertexDataOffset == 0)
            {
                Logger.Debug(logSrc, $"ReadMorphKeyframes: version={hdr.Version} (<8) or no keyframes/offset=0; skipping morph keyframe read.");
                return result;
            }

            Logger.Debug(logSrc, $"ReadMorphKeyframes: Seeking to offset {hdr.MorphVertexDataOffset} for {hdr.NumMorphKeyframes} keyframes of {hdr.NumMorphVertices} vertices each.");
            r.BaseStream.Seek(hdr.MorphVertexDataOffset, SeekOrigin.Begin);

            for (int k = 0; k < hdr.NumMorphKeyframes; k++)
            {
                var mk = new MorphKeyframe();
                mk.Time = r.ReadInt32();
                Logger.Debug(logSrc, $"MorphKeyframe[{k}]: time = {mk.Time}");

                mk.Positions = new Vector3[hdr.NumMorphVertices];
                for (int v = 0; v < hdr.NumMorphVertices; v++)
                {
                    sbyte sx = r.ReadSByte();
                    sbyte sy = r.ReadSByte();
                    sbyte sz = r.ReadSByte();
                    mk.Positions[v] = new Vector3(sx, sy, sz);
                    Logger.Debug(logSrc, $"   Vertex[{v}] delta = ({sx},{sy},{sz})");
                }

                result.Add(mk);
            }

            Logger.Debug(logSrc, "ReadMorphKeyframes: Completed reading all morph keyframes.");
            return result;
        }
    }
}
