﻿using System.Collections.Generic;
using System.Numerics;

namespace redux.utilities
{	public class Mesh
	{
		public List<Brush> Brushes { get; set; } = new List<Brush>();
		public List<Light> Lights { get; } = new List<Light>();
		public List<RoomEffect> RoomEffects { get; } = new();
		public List<MpRespawnPoint> MPRespawnPoints { get; } = new();
		public List<RflEvent> Events { get; } = new();
		public List<PushRegion> PushRegions { get; } = new();
		public List<Trigger> Triggers { get; } = new();
		public List<RflItem> Items { get; } = new();
		public List<ClimbingRegion> ClimbingRegions { get; } = new();
		public List<ParticleEmitter> ParticleEmitters { get; } = new();
		public List<CollisionSphere> CollisionSpheres { get; } = new();
		public List<Bone> Bones { get; } = new List<Bone>();
	}

	public class Brush
	{
		public int UID { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 RotationMatrix { get; set; } = Matrix4x4.Identity;
		public List<Vector3> Vertices { get; set; } = new List<Vector3>();
		public List<Vector2> UVs { get; set; } = new List<Vector2>();
		public List<int> Indices { get; set; } = new List<int>();
		public string TextureName { get; set; }
		public Solid Solid { get; set; }
		public List<PropPoint> PropPoints { get; set; } = new List<PropPoint>();
		public List<Vector4> JointIndices { get; set; }
		public List<Vector4> JointWeights { get; set; }

	}

	public class Solid
	{
		public List<string> Textures { get; set; } = new List<string>();
		public List<Vector3> Vertices { get; set; } = new List<Vector3>();
		public List<Face> Faces { get; set; } = new List<Face>();
		public uint Flags { get; set; }
		public int Life { get; set; }
		public int State { get; set; }
	}

	public enum SolidFlags : uint
	{
		Portal =		0x00000001,
		Air =			0x00000002,
		Detail =		0x00000004,
		unk_08 =		0x00000008,
		EmitsSteam =	0x00000010,
		Geoable =		0x00000020, // RF2 only
		unk_40 =		0x00000040, // RF2 only
		unk_200 =		0x00000200  // RF2 only
	}

	public class Face
	{
		public List<int> Vertices { get; set; } = new List<int>();
		public List<Vector2> UVs { get; set; } = new();
		public int TextureIndex { get; set; }
		public ushort FaceFlags { get; set; }
		public Vector3 Normal { get; set; } = Vector3.UnitZ; // Default fallback
		public bool HasHoles => (FaceFlags & 0x80) != 0;
		public bool HasAlpha => (FaceFlags & 0x40) != 0;
		public bool FullBright => (FaceFlags & 0x20) != 0;
		public bool ScrollTexture => (FaceFlags & 0x10) != 0;
		public bool IsDetail => (FaceFlags & 0x08) != 0;
		public bool LiquidSurface => (FaceFlags & 0x04) != 0;
		public bool Mirrored => (FaceFlags & 0x02) != 0;
		public bool ShowSky => (FaceFlags & 0x01) != 0;
		public bool IsInvisible => (FaceFlags & 0x2000) != 0;

	}
	public enum LightType : byte
	{
		Point = 0,
		Spot = 1,
	}

	public enum LightState : byte
	{
		Off = 0,
		On = 1,
	}
	public class Light
	{
		public int UID { get; set; }
		public string ClassName { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 Rotation { get; set; }
		public string ScriptName { get; set; }
		public bool HiddenInEditor { get; set; }

		// raw bitflags → nice typed props:
		public bool Dynamic { get; set; }
		public bool Fade { get; set; }
		public bool ShadowCasting { get; set; }
		public bool IsEnabled { get; set; }
		public LightType Type { get; set; }
		public LightState InitialState { get; set; }
		public bool RuntimeShadow { get; set; }
		public Vector4 Color { get; set; } // RGBA
		public float Range { get; set; }
		public float FOV { get; set; }
		public float FOVDropoff { get; set; }
		public float IntensityAtMaxRange { get; set; }
		public int DropoffType { get; set; }
		public float TubeLightWidth { get; set; }

		// on/off timing
		public float OnIntensity { get; set; }
		public float OnTime { get; set; }
		public float OnTimeVariation { get; set; }
		public float OffIntensity { get; set; }
		public float OffTime { get; set; }
		public float OffTimeVariation { get; set; }
	}

	public enum RoomEffectType : int
	{
		SkyRoom = 1,
		LiquidRoom = 2,
		AmbientLight = 3,
		None = 4
	}

	public enum LiquidWaveformType : int { None = 1, Calm = 2, Choppy = 3 }
	public enum LiquidType : int { Water = 1, Lava = 2, Acid = 3 }

	public class LiquidProperties
	{
		public LiquidWaveformType Waveform { get; set; }
		public float Depth { get; set; }
		public string SurfaceTexture { get; set; }
		public Vector4 LiquidColor { get; set; }
		public float Visibility { get; set; }
		public LiquidType LiquidType { get; set; }
		public bool ContainsPlankton { get; set; }
		public int TexturePixelsPerMeterU { get; set; }
		public int TexturePixelsPerMeterV { get; set; }
		public float TextureAngleDegrees { get; set; }
		public Vector2 TextureScrollRate { get; set; }
	}

	public class RoomEffect
	{
		public RoomEffectType EffectType { get; set; }
		public Vector4 AmbientColor { get; set; }
		public LiquidProperties LiquidProps { get; set; }
		public bool RoomIsCold { get; set; }
		public bool RoomIsOutside { get; set; }
		public bool RoomIsAirLock { get; set; }
		public int UID { get; set; }
		public string ClassName { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 Rotation { get; set; }
		public string ScriptName { get; set; }
		public bool HiddenInEditor { get; set; }
	}

	public class MpRespawnPoint
	{
		public int UID { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 Rotation { get; set; }
		public string ScriptName { get; set; }
		public bool HiddenInEditor { get; set; }
		public int TeamID { get; set; }
		public bool RedTeam { get; set; }
		public bool BlueTeam { get; set; }
		public bool IsBot { get; set; }
	}

	public class RflEvent
	{
		public int UID { get; set; }
		public string ClassName { get; set; }
		public Vector3 Position { get; set; }
		public bool HasRotation { get; set; }
		public Matrix4x4 Rotation { get; set; }
		public string ScriptName { get; set; }
		public bool HiddenInEditor { get; set; }
		public float Delay { get; set; }
		public bool Bool1 { get; set; }
		public bool Bool2 { get; set; }
		public int Int1 { get; set; }
		public int Int2 { get; set; }
		public float Float1 { get; set; }
		public float Float2 { get; set; }
		public string Str1 { get; set; }
		public string Str2 { get; set; }
		public List<int> Links { get; set; }
		public uint RawColor { get; set; }
		// RF2 exclusive below
		public Dictionary<string, object> Properties { get; set; }
		public byte[] ParameterBlob { get; set; }
		public List<object> Parameters { get; set; }

	}

	public enum PushRegionShape { Sphere = 1, AxisAlignedBox = 2, OrientedBox = 3 }

	public class PushRegion
	{
		public int UID;
		public string ClassName;
		public Vector3 Position;
		public Matrix4x4 Rotation;
		public string ScriptName;
		public bool HiddenInEditor;
		public PushRegionShape Shape;
		public Vector3 Extents;   // for boxes
		public float Radius;      // for spheres
		public float Strength;
		public bool JumpPad;
		public bool DoesntAffectPlayer;
		public bool Radial;
		public bool GrowsTowardsBoundary;
		public bool GrowsTowardsCenter;
		public bool Grounded;
		public bool MassIndependent;
		public ushort Turbulence;
	}
	public enum TriggerShape { Sphere = 0, Box = 1 }
	public enum TriggerActivatedBy
	{
		PlayersOnly = 0, AllObjects = 1, LinkedObjects = 2, AiOnly = 3,
		PlayerVehicleOnly = 4, GeoMods = 5
	}
	public enum TriggerTeam { None = -1, Team1 = 0, Team2 = 1 }

	public class Trigger
	{
		public int UID;
		public string ScriptName;
		public bool HiddenInEditor;
		public TriggerShape Shape;
		public float ResetsAfter;
		public int ResetsTimes;
		public bool UseKeyRequired;
		public string KeyName;
		public bool WeaponActivates;
		public TriggerActivatedBy ActivatedBy;
		public bool IsNpc;
		public bool IsAuto;
		public bool InVehicle;
		public Vector3 Position;
		public float SphereRadius;
		public Matrix4x4 Rotation;
		public float BoxHeight, BoxWidth, BoxDepth;
		public bool OneWay;
		public int AirlockRoomUID;
		public int AttachedToUID;
		public int UseClutterUID;
		public bool Disabled;
		public float ButtonActiveTime;
		public float InsideTime;
		public TriggerTeam Team;
		public List<int> Links;
	}

	public class RflItem
	{
		public int UID { get; set; }
		public string ClassName { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 Rotation { get; set; }
		public string ScriptName { get; set; }
		public bool HiddenInEditor { get; set; }
		public int Count { get; set; }
		public int RespawnTime { get; set; }
		public int TeamID { get; set; }
	}

	public class ClimbingRegion
	{
		public int UID;
		public string ClassName;
		public Vector3 Position;
		public Matrix4x4 Rotation;
		public string ScriptName;
		public bool HiddenInEditor;
		public int Type;
		public Vector3 Extents;
	}

	public enum ParticleEmitterShape
	{
		Sphere = 0,
		Plane = 1,
		// Add other shape enums as needed
	}

	public class ParticleEmitter
	{
		public int UID { get; set; }
		public string ClassName { get; set; }
		public Vector3 Position { get; set; }
		public Matrix4x4 Rotation { get; set; }
		public string ScriptName { get; set; }
		public bool HiddenInEditor { get; set; }

		public ParticleEmitterShape Shape { get; set; }
		public float SphereRadius { get; set; }
		public float PlaneWidth { get; set; }
		public float PlaneDepth { get; set; }

		public string Texture { get; set; }
		public float SpawnDelay { get; set; }
		public float SpawnRandomize { get; set; }
		public float Velocity { get; set; }
		public float VelocityRandomize { get; set; }
		public float Acceleration { get; set; }
		public float Decay { get; set; }
		public float DecayRandomize { get; set; }
		public float ParticleRadius { get; set; }
		public float ParticleRadiusRandomize { get; set; }
		public float GrowthRate { get; set; }
		public float GravityMultiplier { get; set; }
		public float RandomDirection { get; set; }

		public Vector4 ParticleColor { get; set; }
		public Vector4 FadeToColor { get; set; }

		public uint EmitterFlags { get; set; }
		public ushort ParticleFlags { get; set; }

		public byte Stickiness { get; set; }
		public byte Bounciness { get; set; }
		public byte PushEffect { get; set; }
		public byte Swirliness { get; set; }

		public bool InitiallyOn { get; set; }
		public float TimeOn { get; set; }
		public float TimeOnRandomize { get; set; }
		public float TimeOff { get; set; }
		public float TimeOffRandomize { get; set; }
		public float ActiveDistance { get; set; }
	}

	public class LodMesh
	{
		public uint Flags;
		public int NumVertices;
		public int NumChunks;
		public byte[] DataBlock;
		public int Unknown1;      // always –1

		public ChunkInfo[] ChunkInfos;
		public int NumPropPoints;
		public int NumTextures;
		public LodTexture[] Textures;
		public PropPoint[] PropPoints;

		public int[] ChunkHeaders;
		public ChunkData[] Chunks;
	}

	public enum LodFlags : uint
	{
		OrigMap = 0x01,
		Character = 0x02,
		Reflection = 0x04,
		_10 = 0x10,
		TrianglePlanes = 0x20
	}

	public struct ChunkInfo
	{
		public ushort NumVertices;
		public ushort NumFaces;
		public ushort VecsAlloc;
		public ushort FacesAlloc;
		public ushort SamePosVertexOffsetsAlloc;
		public ushort WiAlloc;
		public ushort UvsAlloc;
		public uint RenderFlags;
	}

	public struct LodTexture
	{
		public byte Id;
		public string Filename;
	}

	public struct PropPoint
	{
		public string Name;
		public Quaternion Orientation;
		public Vector3 Position;
		public int ParentIndex;
	}

	public struct Triangle
	{
		public ushort I0, I1, I2;
		public ushort Flags;
	}

	public class ChunkData
	{
		public Vector3[] Positions;
		public Vector3[] Normals;
		public Vector2[] UVs;
		public Triangle[] Triangles;
		public RFPlane[] Planes;
		public short[] SamePosVertexOffsets;
		public VertexBoneLink[] BoneLinks;
		public short[] OrigMap;
	}

	public struct VertexBoneLink
	{
		public byte[] Weights; // 4
		public byte[] Bones;   // 4
	}

	public struct RFPlane
	{
		public Vector3 Normal;
		public float Dist;
	}

	public class CollisionSphere
	{
		public string Name { get; set; }
		public int ParentIndex { get; set; }
		public Vector3 Position { get; set; }
		public float Radius { get; set; }
	}

	public class Bone
	{
		public string Name { get; set; }
		public Quaternion BaseRotation { get; set; }
		public Vector3 BaseTranslation { get; set; }
		public int ParentIndex { get; set; }
	}
	public class RfaFile
	{
		public RfaHeader Header { get; set; }
		public List<RfaBone> Bones { get; set; }
		public short[] MorphVertexMappings { get; set; }
		public List<MorphKeyframe> MorphKeyframes { get; set; }
	}
	public class RfaHeader
	{
		public byte[] Magic { get; set; }
		public int Version { get; set; }
		public float PosReduction { get; set; }
		public float RotReduction { get; set; }
		public int StartTime { get; set; }
		public int EndTime { get; set; }
		public int NumBones { get; set; }
		public int NumMorphVertices { get; set; }
		public int NumMorphKeyframes { get; set; }
		public int RampInTime { get; set; }
		public int RampOutTime { get; set; }
		public Quaternion TotalRotation { get; set; }
		public Vector3 TotalTranslation { get; set; }
		public int MorphVertexMappingsOffset { get; set; }
		public int MorphVertexDataOffset { get; set; }
		public int[] BoneOffsets { get; set; }
	}
	public class RfaBone
	{
		public float Weight { get; set; }
		public short NumRotationKeys { get; set; }
		public short NumTranslationKeys { get; set; }
		public List<RfaRotationKey> RotationKeys { get; set; }
		public List<RfaTranslationKey> TranslationKeys { get; set; }
	}
	public class RfaRotationKey
	{
		public int Time { get; set; }
		public Quaternion Rotation { get; set; }
		public byte EaseIn { get; set; }
		public byte EaseOut { get; set; }
	}
	public class RfaTranslationKey
	{
		public int Time { get; set; }
		public Vector3 Translation { get; set; }
		public Vector3 InTangent { get; set; }
		public Vector3 OutTangent { get; set; }
	}
	public class MorphKeyframe
	{
		public int Time { get; set; }
		public Vector3[] Positions { get; set; }
	}
}
