using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACLLibs;

namespace AnimeStudio
{
    /// <summary>Lossless binding-to-sample expansion shared by YAML, JSON and FBX.</summary>
    public static class EndfieldClipDecoder
    {
        public sealed class Track
        {
            public GenericBinding Binding;
            public int BindingIndex;
            public int Width;
            public bool Constant;
            public float[] Times;
            public float[] Values;
        }

        public static bool IsCompressed(AnimationClip clip) =>
            clip.m_AclCompressedBuffer?.TransformBufferData?.Length > 0 ||
            clip.m_AclCompressedBuffer?.FloatBufferData?.Length > 0;

        public static List<Track> Decode(AnimationClip clip)
        {
            var result = new List<Track>();
            var compressed = clip.m_AclCompressedBuffer;
            var bindings = clip.m_ClipBindingConstant.genericBindings;
            var constants = new HashSet<int>((compressed.m_ConstantIndexs ?? Array.Empty<ushort>()).Select(x => (int)x));
            if (compressed.TransformBufferData?.Length > 0)
            {
                var (values, times, count) = EndfieldACL.DecodeTransforms(compressed.TransformBufferData);
                var masks = compressed.TransformSubTrackMasks;
                int planeBytes = (count + 7) / 8;
                // The three bit planes are packed back-to-back.  The whole
                // byte array is then aligned for the serialized object (the
                // 362-track example has 46*3=138 data bytes and 6 zero pad
                // bytes).  Padding must not be mistaken for a fourth plane.
                int planeStride = planeBytes, required = planeStride * 3;
                if (compressed.OutputTrackCount != count || masks == null || masks.Length < required ||
                    masks.Length > required + 15 ||
                    masks.Skip(required).Any(x => x != 0))
                    throw new InvalidDataException($"Invalid Endfield ACL transform mask: {clip.m_Name}.");
                Validate(values, times, count * 10, clip.m_Name);
                var paths = new uint?[count];
                for (uint attribute = 1; attribute <= 3; attribute++)
                {
                    var candidates = bindings.Select((binding, index) => (binding, index))
                        .Where(x => x.binding.typeID == ClassIDType.Transform && x.binding.attribute == attribute).ToArray();
                    int next = 0;
                    for (int t = 0; t < count; t++)
                    {
                        if ((masks[(attribute - 1) * planeStride + t / 8] & (1 << (7 - t % 8))) == 0) continue;
                        if (next >= candidates.Length) throw new InvalidDataException($"Missing ACL transform binding: {clip.m_Name}.");
                        var (binding, index) = candidates[next++];
                        if (paths[t].HasValue && paths[t].Value != binding.path)
                            throw new InvalidDataException($"ACL transform components disagree on path: {clip.m_Name}.");
                        paths[t] = binding.path;
                        int width = attribute == 2 ? 4 : 3, component = attribute == 1 ? 4 : attribute == 2 ? 0 : 7;
                        var samples = new float[times.Length * width];
                        for (int f = 0; f < times.Length; f++)
                            Array.Copy(values, (f * count + t) * 10 + component, samples, f * width, width);
                        result.Add(new Track { Binding = binding, BindingIndex = index, Width = width,
                            Constant = constants.Contains(index), Times = times, Values = samples });
                    }
                    if (next != candidates.Length) throw new InvalidDataException($"Excess ACL transform bindings: {clip.m_Name}.");
                }
            }
            if (compressed.FloatBufferData?.Length > 0)
            {
                var (values, times, count) = EndfieldACL.DecodeScalars(compressed.FloatBufferData);
                var candidates = bindings.Select((binding, index) => (binding, index))
                    .Where(x => x.binding.typeID != ClassIDType.Transform).ToArray();
                if (count != compressed.FloatCurveCount || count != candidates.Length)
                    throw new InvalidDataException($"Endfield ACL scalar binding count mismatch: {clip.m_Name}.");
                Validate(values, times, count, clip.m_Name);
                for (int t = 0; t < count; t++)
                {
                    var (binding, index) = candidates[t];
                    var samples = new float[times.Length];
                    for (int f = 0; f < times.Length; f++) samples[f] = values[f * count + t];
                    result.Add(new Track { Binding = binding, BindingIndex = index, Width = 1,
                        Constant = constants.Contains(index), Times = times, Values = samples });
                }
            }
            return result.OrderBy(x => x.BindingIndex).ToList();
        }

        private static void Validate(float[] values, float[] times, int width, string name)
        {
            if (times.Length == 0 || values.Length != checked(times.Length * width) ||
                values.Any(x => !float.IsFinite(x)) || times.Any(x => !float.IsFinite(x)))
                throw new InvalidDataException($"Invalid ACL samples: {name}.");
            for (int f = 1; f < times.Length; f++)
                if (times[f] <= times[f - 1]) throw new InvalidDataException($"Non-increasing ACL times: {name}.");
        }
    }
}
