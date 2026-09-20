using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace SurroundWeather;

/// <summary>
/// Puts a block's ambient sound (rain on windows, a beehive, a translocator) on the block that
/// actually makes it.
/// <para>
/// Vanilla plays it from the point of a bounding box nearest the listener, and that box has grown
/// to hold every such block in a 32-block section. A house with windows on more than one wall has
/// a box spanning the room, and the nearest point of a box you are standing inside is where you
/// stand: rain on the windows plays from your own head and follows you about the room, instead of
/// from the windows. Here it plays from the nearest pane, inside or out, which is also what the
/// audio engine needs to muffle it through a wall.
/// </para>
/// </summary>
[HarmonyPatch(typeof(AmbientSound), "updatePosition")]
internal static class AmbientSoundPlacementPatch
{
    /// <summary>Blocks from the listener the sound's own blocks are looked for.</summary>
    private const int SearchRadius = 12;

    /// <summary>The nearest block is found this often; between times the sound slides along it.</summary>
    private const long SearchEveryMs = 200;

    private static readonly ConditionalWeakTable<AmbientSound, Anchor> Anchors = new();
    private static ICoreClientAPI capi;

    internal static void Initialize(ICoreClientAPI api) => capi = api;

    public static bool Prefix(AmbientSound __instance, EntityPos position)
    {
        ICoreClientAPI api = capi;
        IBlockAccessor blocks = api?.World?.BlockAccessor;
        if (blocks == null || position == null || __instance?.Sound == null || __instance.AssetLoc == null
            || !SurroundWeatherConfigManager.Current.PlaceAmbientSoundsOnTheirBlocks)
        {
            return true;  // vanilla's bounding box
        }

        Anchor anchor = Anchors.GetOrCreateValue(__instance);
        long nowMs = api.ElapsedMilliseconds;
        if (!anchor.Found || nowMs - anchor.FoundMs >= SearchEveryMs)
        {
            anchor.Found = TryFindNearestBlock(blocks, __instance, position, anchor);
            anchor.FoundMs = nowMs;
        }

        if (!anchor.Found)
        {
            return true;  // none of its blocks nearby: vanilla's box is as good as anything
        }

        // On the face of that block nearest the listener, so a pane sounds like the pane it is.
        __instance.Sound.SetPosition(new Vec3f(
            (float)GameMath.Clamp(position.X, anchor.X, anchor.X + 1),
            (float)GameMath.Clamp(position.Y, anchor.Y, anchor.Y + 1),
            (float)GameMath.Clamp(position.Z, anchor.Z, anchor.Z + 1)));
        return false;
    }

    /// <summary>The nearest block within reach that makes this sound, over the sound's own boxes.</summary>
    private static bool TryFindNearestBlock(IBlockAccessor blocks, AmbientSound sound, EntityPos position, Anchor anchor)
    {
        double best = double.MaxValue;
        bool found = false;
        int px = (int)Math.Floor(position.X);
        int py = (int)Math.Floor(position.Y);
        int pz = (int)Math.Floor(position.Z);
        var pos = new BlockPos(0, 0, 0, position.Dimension);
        foreach (Cuboidi box in sound.BoundingBoxes)
        {
            // The boxes' far corners are exclusive: the scan grew them a block past the last block.
            int minX = Math.Max(box.X1, px - SearchRadius);
            int maxX = Math.Min(box.X2, px + SearchRadius + 1);
            int minY = Math.Max(box.Y1, py - SearchRadius);
            int maxY = Math.Min(box.Y2, py + SearchRadius + 1);
            int minZ = Math.Max(box.Z1, pz - SearchRadius);
            int maxZ = Math.Min(box.Z2, pz + SearchRadius + 1);
            for (int x = minX; x < maxX; x++)
            {
                double dx = x + 0.5 - position.X;
                for (int y = minY; y < maxY; y++)
                {
                    double dy = y + 0.5 - position.Y;
                    for (int z = minZ; z < maxZ; z++)
                    {
                        double dz = z + 0.5 - position.Z;
                        double distance = (dx * dx) + (dy * dy) + (dz * dz);
                        if (distance >= best)
                        {
                            continue;  // no nearer than what we have: not worth reading the block
                        }

                        pos.Set(x, y, z);
                        AssetLocation ambient = blocks.GetBlock(pos)?.Sounds?.Ambient;
                        if (ambient == null || !ambient.Equals(sound.AssetLoc))
                        {
                            continue;
                        }

                        best = distance;
                        anchor.X = x;
                        anchor.Y = y;
                        anchor.Z = z;
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    private sealed class Anchor
    {
        public int X;
        public int Y;
        public int Z;
        public bool Found;
        public long FoundMs;
    }
}
