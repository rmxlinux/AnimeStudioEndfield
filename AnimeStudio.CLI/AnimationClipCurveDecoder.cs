using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACLLibs;
using AnimeStudio;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    internal static class AnimationClipCurveDecoder
    {
        private sealed class Key
        {
            public float time;
            public float[] values;
        }

        private sealed class Track
        {
            public uint pathHash;
            public uint attribute;
            public int classID;
            public byte customType;
            public int width;
            public List<Key> keys = new();
        }

        private sealed class Document
        {
            public string name;
            public float sampleRate;
            public float stopTime;
            public List<Track> tracks = new();
            public AclCompressedDiagnostic aclCompressed;
        }

        private sealed class AclBufferDiagnostic
        {
            public int byteCount;
            public int valueCount;
            public int timeCount;
            public float[] times;
            public float[] firstValues;
            public string error;
        }

        private sealed class AclCompressedDiagnostic
        {
            public int version;
            public ushort outputTrackCount;
            public ushort rootPosIndex;
            public ushort rootRotIndex;
            public ushort rootScaleIndex;
            public ushort rootTrackCount;
            public ushort floatCurveCount;
            public int transformSubTrackMaskCount;
            public int transformSubTrackConstantMaskCount;
            public ushort[] defaultIndices;
            public ushort[] constantIndices;
            public float[] constantValues;
            public AclBufferDiagnostic transform;
            public AclBufferDiagnostic rootMotion;
            public AclBufferDiagnostic floats;
        }

        private static AclBufferDiagnostic InspectAclBuffer(byte[] data)
        {
            var result = new AclBufferDiagnostic { byteCount = data?.Length ?? 0 };
            if (data == null || data.Length == 0)
                return result;
            return result;
        }

        private static AclCompressedDiagnostic InspectAclCompressed(AnimationClip animationClip)
        {
            var acl = animationClip.m_AclCompressedBuffer;
            if (acl == null)
                return null;
            return new AclCompressedDiagnostic
            {
                version = acl.Version,
                outputTrackCount = acl.OutputTrackCount,
                rootPosIndex = acl.RootPosIndex,
                rootRotIndex = acl.RootRotIndex,
                rootScaleIndex = acl.RootScaleIndex,
                rootTrackCount = acl.RootTrackCount,
                floatCurveCount = acl.FloatCurveCount,
                transformSubTrackMaskCount = acl.TransformSubTrackMasks?.Length ?? 0,
                transformSubTrackConstantMaskCount = acl.TransformSubTrackConstantMasks?.Length ?? 0,
                defaultIndices = acl.m_DefaultIndexs,
                constantIndices = acl.m_ConstantIndexs,
                constantValues = acl.m_ConstantValues,
                transform = InspectAclBuffer(acl.TransformBufferData),
                rootMotion = InspectAclBuffer(acl.RootMotionBufferData),
                floats = InspectAclBuffer(acl.FloatBufferData),
            };
        }

        private static int Width(GenericBinding binding)
        {
            if (binding.typeID != ClassIDType.Transform)
                return 1;
            return binding.attribute switch
            {
                1 => 3,
                2 => 4,
                3 => 3,
                4 => 3,
                _ => 1,
            };
        }

        private static (GenericBinding binding, int start) FindBinding(AnimationClipBindingConstant bindings, int index)
        {
            var start = 0;
            foreach (var binding in bindings.genericBindings)
            {
                var width = Width(binding);
                if (index < start + width)
                    return (binding, start);
                start += width;
            }
            throw new IndexOutOfRangeException($"Animation curve index {index} is outside the binding table ({start} scalar curves). ");
        }

        private static string TrackKey(GenericBinding binding) =>
            $"{binding.path}:{binding.attribute}:{(int)binding.typeID}:{binding.customType}";

        private static void AddSample(
            Dictionary<string, Track> tracks,
            AnimationClipBindingConstant bindings,
            int globalIndex,
            float time,
            float[] data,
            int dataOffset,
            ref int curveIndex)
        {
            var (binding, bindingStart) = FindBinding(bindings, globalIndex);
            var width = Width(binding);
            // ModelConverter consumes a complete transform binding at once. Keep the
            // same behavior and flag malformed/non-boundary input instead of silently
            // shifting quaternion/vector components.
            if (globalIndex != bindingStart)
                throw new InvalidDataException($"Curve {globalIndex} starts inside a width-{width} binding at {bindingStart}.");
            var values = new float[width];
            Array.Copy(data, dataOffset + curveIndex, values, 0, width);
            curveIndex += width;

            var key = TrackKey(binding);
            if (!tracks.TryGetValue(key, out var track))
            {
                track = new Track
                {
                    pathHash = binding.path,
                    attribute = binding.attribute,
                    classID = (int)binding.typeID,
                    customType = binding.customType,
                    width = width,
                };
                tracks.Add(key, track);
            }
            track.keys.Add(new Key { time = time, values = values });
        }

        public static void WriteJson(AnimationClip animationClip, string path)
        {
            var compressed = animationClip.m_AclCompressedBuffer;
            if (compressed != null)
            {
                if (compressed.TransformBufferData?.Length > 0)
                    File.WriteAllBytes(path + ".transform.acl", compressed.TransformBufferData);
                if (compressed.RootMotionBufferData?.Length > 0)
                    File.WriteAllBytes(path + ".root.acl", compressed.RootMotionBufferData);
                if (compressed.FloatBufferData?.Length > 0)
                    File.WriteAllBytes(path + ".float.acl", compressed.FloatBufferData);
                if (compressed.TransformSubTrackMasks?.Length > 0)
                    File.WriteAllBytes(path + ".transform_masks.bin", compressed.TransformSubTrackMasks);
                if (compressed.TransformSubTrackConstantMasks?.Length > 0)
                    File.WriteAllBytes(path + ".transform_constant_masks.bin", compressed.TransformSubTrackConstantMasks);
            }
            var document = new Document
            {
                name = animationClip.m_Name,
                sampleRate = animationClip.m_SampleRate,
                stopTime = animationClip.m_MuscleClip?.m_StopTime ?? 0f,
                aclCompressed = InspectAclCompressed(animationClip),
            };
            var tracks = new Dictionary<string, Track>();
            if (animationClip.m_MuscleClip == null)
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(document, Formatting.Indented));
                return;
            }

            if (EndfieldClipDecoder.IsCompressed(animationClip))
            {
                foreach (var source in EndfieldClipDecoder.Decode(animationClip))
                {
                    var binding = source.Binding;
                    var track = new Track { pathHash = binding.path, attribute = binding.attribute,
                        classID = (int)binding.typeID, customType = binding.customType, width = source.Width };
                    for (int f = 0; f < source.Times.Length; f++)
                    {
                        if (source.Constant && f != 0 && f != source.Times.Length - 1) continue;
                        var value = new float[source.Width];
                        Array.Copy(source.Values, f * source.Width, value, 0, source.Width);
                        track.keys.Add(new Key { time = source.Times[f], values = value });
                    }
                    document.tracks.Add(track);
                }
                File.WriteAllText(path, JsonConvert.SerializeObject(document, Formatting.Indented));
                return;
            }

            var clip = animationClip.m_MuscleClip.m_Clip;
            var bindings = animationClip.m_ClipBindingConstant ?? clip.ConvertValueArrayToGenericBinding();
            var acl = clip.m_ACLClip;
            var aclCount = (int)acl.CurveCount;

            if (acl.IsSet && !Studio.Game.Type.IsSRGroup())
            {
                acl.Process(Studio.Game, out var values, out var times);
                for (var frameIndex = 0; frameIndex < times.Length; frameIndex++)
                {
                    var offset = frameIndex * aclCount;
                    for (var curveIndex = 0; curveIndex < aclCount;)
                    {
                        var index = curveIndex;
                        AddSample(tracks, bindings, index, times[frameIndex], values, offset, ref curveIndex);
                    }
                }
            }

            var streamedFrames = clip.m_StreamedClip.ReadData();
            for (var frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
            {
                var frame = streamedFrames[frameIndex];
                var values = frame.keyList.Select(key => key.value).ToArray();
                for (var curveIndex = 0; curveIndex < frame.keyList.Count;)
                {
                    var index = frame.keyList[curveIndex].index;
                    if (!Studio.Game.Type.IsSRGroup())
                        index += aclCount;
                    AddSample(tracks, bindings, index, frame.time, values, 0, ref curveIndex);
                }
            }

            var dense = clip.m_DenseClip;
            var streamCount = (int)clip.m_StreamedClip.curveCount;
            for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
            {
                var time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                var offset = frameIndex * (int)dense.m_CurveCount;
                for (var curveIndex = 0; curveIndex < dense.m_CurveCount;)
                {
                    var index = streamCount + curveIndex;
                    if (!Studio.Game.Type.IsSRGroup())
                        index += aclCount;
                    AddSample(tracks, bindings, index, time, dense.m_SampleArray, offset, ref curveIndex);
                }
            }

            if (acl.IsSet && Studio.Game.Type.IsSRGroup())
            {
                acl.Process(Studio.Game, out var values, out var times);
                for (var frameIndex = 0; frameIndex < times.Length; frameIndex++)
                {
                    var offset = frameIndex * aclCount;
                    for (var curveIndex = 0; curveIndex < aclCount;)
                    {
                        var index = curveIndex + (int)dense.m_CurveCount + streamCount;
                        AddSample(tracks, bindings, index, times[frameIndex], values, offset, ref curveIndex);
                    }
                }
            }

            if (clip.m_ConstantClip != null)
            {
                var constant = clip.m_ConstantClip;
                var baseIndex = aclCount + streamCount + (int)dense.m_CurveCount;
                foreach (var time in new[] { 0f, animationClip.m_MuscleClip.m_StopTime })
                {
                    for (var curveIndex = 0; curveIndex < constant.data.Length;)
                    {
                        var index = baseIndex + curveIndex;
                        AddSample(tracks, bindings, index, time, constant.data, 0, ref curveIndex);
                    }
                }
            }

            document.tracks = tracks.Values.ToList();
            File.WriteAllText(path, JsonConvert.SerializeObject(document, Formatting.Indented));
        }
    }
}
