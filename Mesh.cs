using System.Collections.Generic;

namespace redux
{
	public class Mesh
	{
		public List<Brush> Brushes { get; set; } = new List<Brush>();
		public List<Light> Lights { get; } = new List<Light>();
		public List<RoomEffect> RoomEffects { get; } = new();
		public List<MpRespawnPoint> MPRespawnPoints { get; } = new();
		public List<RflEvent> Events { get; } = new();
		public List<PushRegion> PushRegions { get; } = new();
		public List<Trigger> Triggers { get; } = new();
		public List<RflItem> Items { get; } = new();
	}
}
