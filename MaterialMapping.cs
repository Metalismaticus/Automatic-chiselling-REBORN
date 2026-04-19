using System.Collections.Generic;
using Vintagestory.API.Common;

namespace AutomaticChiselling
{
    /// <summary>
    /// Maps palette color indices (from .vox model) to block AssetLocations
    /// chosen by the player. Used for colored chiseling: each voxel's palette
    /// index is translated to a specific game block.
    ///
    /// If a palette index has no assignment, voxels of that color fall back
    /// to materialIdx=0 (the base block used to create the chisel entity).
    /// </summary>
    public class MaterialMapping
    {
        /// <summary>palette index → block code (AssetLocation string like "game:rock-granite")</summary>
        public Dictionary<byte, string> Assignments { get; set; } = new Dictionary<byte, string>();

        public bool IsEmpty => Assignments.Count == 0;

        public bool IsComplete(IReadOnlyList<byte> usedIndices)
        {
            if (usedIndices == null) return false;
            foreach (var idx in usedIndices)
                if (!Assignments.ContainsKey(idx)) return false;
            return true;
        }

        public int AssignedCount => Assignments.Count;

        public bool TryGet(byte paletteIndex, out AssetLocation loc)
        {
            if (Assignments.TryGetValue(paletteIndex, out string code))
            {
                loc = new AssetLocation(code);
                return true;
            }
            loc = null;
            return false;
        }

        public void Assign(byte paletteIndex, AssetLocation loc)
        {
            Assignments[paletteIndex] = loc.ToString();
        }

        public void Unassign(byte paletteIndex)
        {
            Assignments.Remove(paletteIndex);
        }

        public MaterialMapping Clone()
        {
            return new MaterialMapping
            {
                Assignments = new Dictionary<byte, string>(Assignments)
            };
        }
    }
}
