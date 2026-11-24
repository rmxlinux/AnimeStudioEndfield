using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ACLLibs;
using AnimeStudio;

if (args.Length < 2 || args.Length > 4 || (args.Length == 4 && args[3] != "--verify-skip"))
    throw new ArgumentException("Usage: SmokeTest <animation bundle> <output FBX> [clip name] [--verify-skip]");

var assets = new AssetsManager { Game = GameManager.GetGame("ArknightsEndfield") };
assets.LoadFiles(args[0]);
var clip = assets.assetsFileList.SelectMany(file => file.Objects)
    .OfType<AnimationClip>()
    .First(animation => animation.m_AclCompressedBuffer?.TransformBufferData?.Length > 0 &&
        (args.Length == 2 || animation.m_Name == args[2]));
var compressed = clip.m_AclCompressedBuffer;
var (_, _, decodedTrackCount) = EndfieldACL.DecodeTransforms(compressed.TransformBufferData);
Console.WriteLine($"{clip.m_Name}: output tracks={compressed.OutputTrackCount}, decoded tracks={decodedTrackCount}, " +
    $"mask bytes={compressed.TransformSubTrackMasks?.Length ?? 0}, transform bindings=" +
    clip.m_ClipBindingConstant.genericBindings.Count(binding => binding.typeID == ClassIDType.Transform));
var planeBytes = (decodedTrackCount + 7) / 8;
for (var plane = 0; plane < 3; plane++)
{
    var attribute = (uint)(plane + 1);
    var maskBits = Enumerable.Range(0, decodedTrackCount).Count(trackIndex =>
        (compressed.TransformSubTrackMasks[plane * planeBytes + trackIndex / 8] & (1 << (7 - trackIndex % 8))) != 0);
    var bindingCount = clip.m_ClipBindingConstant.genericBindings.Count(binding =>
        binding.typeID == ClassIDType.Transform && binding.attribute == attribute);
    Console.WriteLine($"attribute {attribute}: mask bits={maskBits}, bindings={bindingCount}");
}

var converter = (ModelConverter)RuntimeHelpers.GetUninitializedObject(typeof(ModelConverter));
var root = new ImportedFrame { Name = "root", LocalScale = Vector3.One };
var hashes = new Dictionary<uint, string>();
foreach (var hash in clip.m_ClipBindingConstant.genericBindings
    .Where(binding => binding.typeID == ClassIDType.Transform)
    .Select(binding => binding.path).Distinct())
{
    var name = $"bone_{hash}";
    hashes.Add(hash, name);
    root.AddChild(new ImportedFrame { Name = name, LocalScale = Vector3.One });
}

Set("RootFrame", root);
Set("MeshList", new List<ImportedMesh>());
Set("MaterialList", new List<ImportedMaterial>());
Set("TextureList", new List<ImportedTexture>());
Set("MorphList", new List<ImportedMorph>());
typeof(ModelConverter).GetField("bonePathHash", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(converter, hashes);

var animation = new ImportedKeyframedAnimation
{
    Name = clip.m_Name,
    SampleRate = clip.m_SampleRate,
    TrackList = new List<ImportedAnimationKeyframedTrack>()
};
Set("AnimationList", new List<ImportedKeyframedAnimation> { animation });
typeof(ModelConverter).GetMethod("ConvertEndfieldAclAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(converter, new object[] { animation, clip, clip.m_ClipBindingConstant });

var translations = animation.TrackList.Sum(track => track.Translations.Count);
var rotations = animation.TrackList.Sum(track => track.Rotations.Count);
var scales = animation.TrackList.Sum(track => track.Scalings.Count);
Console.WriteLine($"{clip.m_Name}: {animation.TrackList.Count} tracks, {translations} translations, {rotations} rotations, {scales} scales");
if (animation.TrackList.Count < 300 || translations < 1000 || rotations < 1000)
    throw new InvalidDataException("Decoded animation has too few populated tracks.");

ModelExporter.ExportFbx(args[1], converter, new Fbx.ExportOptions
{
    exportAllNodes = true,
    exportAnimations = true,
    castToBone = true,
    boneSize = 1,
    scaleFactor = 1,
    fbxVersion = 3
});
Console.WriteLine($"FBX: {new FileInfo(args[1]).Length} bytes");

if (args.Length == 4)
{
    var invalidClip = (AnimationClip)RuntimeHelpers.GetUninitializedObject(typeof(AnimationClip));
    invalidClip.m_Name = "invalid_test_clip";
    Set("AnimationList", new List<ImportedKeyframedAnimation>());
    typeof(ModelConverter).GetField("options", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(converter, new ModelConverter.Options { game = GameManager.GetGame("ArknightsEndfield") });
    typeof(ModelConverter).GetField("animationClipHashSet", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(converter, new HashSet<AnimationClip> { invalidClip, clip });
    typeof(ModelConverter).GetMethod("ConvertAnimations", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(converter, null);
    if (converter.AnimationList.Count != 1 || converter.AnimationList[0].Name != clip.m_Name)
        throw new InvalidDataException("A failed animation clip prevented a valid clip from converting.");
    Console.WriteLine("Skip check: invalid clip removed, valid clip retained.");
}

void Set(string property, object value) => typeof(ModelConverter).GetProperty(property)!.SetValue(converter, value);
