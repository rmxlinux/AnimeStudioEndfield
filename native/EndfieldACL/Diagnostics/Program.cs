using AnimeStudio;
using ACLLibs;
using Newtonsoft.Json;

if(args.Length < 2) throw new ArgumentException("Diagnostics <output directory> <bundle>...");
Directory.CreateDirectory(args[0]);
var failures=0;
var tested=0;
var manager = new AssetsManager { Game = GameManager.GetGame("ArknightsEndfield"), ResolveDependencies = false };
manager.LoadFiles(args.Skip(1).ToArray());
foreach(var clip in manager.assetsFileList.SelectMany(f=>f.Objects).OfType<AnimationClip>())
{
    var c=clip.m_AclCompressedBuffer;
    var bs=clip.m_ClipBindingConstant.genericBindings;
    if(c==null) continue;
    tested++;
    var transforms=c.TransformBufferData.Length>0?EndfieldACL.DecodeTransforms(c.TransformBufferData):default;
    var scalars=c.FloatBufferData.Length>0?EndfieldACL.DecodeScalars(c.FloatBufferData):default;
    var masks=c.TransformSubTrackMasks;
    var report=new {
        clip.m_Name, clip.m_SampleRate, version=clip.version, c.Version,
        c.OutputTrackCount, c.FloatCurveCount, c.RootTrackCount,c.RootPosIndex,c.RootRotIndex,c.RootScaleIndex,
        transformCount=transforms.TrackCount,scalarCount=scalars.TrackCount,
        masks=masks.Select(x=>(int)x), constantMasks=c.TransformSubTrackConstantMasks.Select(x=>(int)x),
        c.m_DefaultIndexs,c.m_ConstantIndexs,c.m_ConstantValues,
        bindings=bs.Select((b,i)=>new {i,b.path,b.attribute,type=(int)b.typeID,b.customType}),
        scalarFirst=scalars.Values?.Take(scalars.TrackCount),
        rootBytes=c.RootMotionBufferData.Length
    };
    File.WriteAllText(Path.Combine(args[0],clip.m_Name+".metadata.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
    File.WriteAllBytes(Path.Combine(args[0],clip.m_Name+".mask.bin"),masks);
    Console.WriteLine($"CLIP {clip.m_Name} QVV={transforms.TrackCount} Output={c.OutputTrackCount} mask={masks.Length} Float={scalars.TrackCount}/{c.FloatCurveCount} Root={c.RootTrackCount}");
    try {
        var decoded=EndfieldClipDecoder.Decode(clip);
        var converted=AnimationClipConverter.Process(clip);
        if(converted.Floats.Count!=scalars.TrackCount) throw new Exception("Lost scalar curves during YAML conversion");
        if(converted.Floats.Select(f=>(f.path,f.attribute,f.classID)).Distinct().Count()!=converted.Floats.Count)
            throw new Exception("Duplicate YAML curve names");
        var humanoid=bs.Where(b=>b.typeID==ClassIDType.Animator && b.customType==8).Select(b=>(int)b.attribute).ToArray();
        if(humanoid.SequenceEqual(Enumerable.Range(0,143)))
        {
            var mapped=humanoid.Where(a=>EndfieldHumanoidLayout.TryMapToUnity(a,out _))
                .Select(a=>{EndfieldHumanoidLayout.TryMapToUnity(a,out var target);return target;}).OrderBy(a=>a).ToArray();
            if(!mapped.SequenceEqual(Enumerable.Range(0,137))) throw new Exception("Incomplete or duplicate stock Humanoid channel mapping");
            var names=humanoid.Select(a=>EndfieldHumanoidLayout.ToProperty(a,clip.version)).ToArray();
            if(names.Distinct().Count()!=143) throw new Exception("Humanoid property names collide");
            Console.WriteLine("LAYOUT_PASS 137 standard channels + 6 named extended channels");
        }
        var yaml=clip.Convert(); // The same entry point used by GUI preview.
        File.WriteAllText(Path.Combine(args[0],clip.m_Name+".anim"),yaml);
        if(transforms.TrackCount>0)
        {
            int required=((transforms.TrackCount+7)/8)*3;
            try
            {
                c.TransformSubTrackMasks=masks.Take(required).ToArray();
                EndfieldClipDecoder.Decode(clip);
                c.TransformSubTrackMasks=masks.Take(required).Concat(new byte[15]).ToArray();
                EndfieldClipDecoder.Decode(clip);
                foreach(var malformed in new[]{masks.Take(required-1).ToArray(), masks.Take(required).Concat(new byte[16]).ToArray(), masks.Take(required).Concat(new byte[]{1}).ToArray()})
                {
                    c.TransformSubTrackMasks=malformed;
                    bool rejected=false;
                    try{EndfieldClipDecoder.Decode(clip);}catch(InvalidDataException){rejected=true;}
                    if(!rejected) throw new Exception("Malformed transform mask accepted");
                }
            }
            finally { c.TransformSubTrackMasks=masks; }
            Console.WriteLine("MASK_PASS compact/zero-padding accepted; truncated/nonzero/excess padding rejected");
        }
        Console.WriteLine($"DECODE_PASS tracks={decoded.Count} YAML rotations={converted.Rotations.Count} floats={converted.Floats.Count}");
    } catch(Exception ex) {failures++;Console.WriteLine("DECODE_FAIL "+ex);}
}
if(tested==0 || failures>0) Environment.Exit(1);
Console.WriteLine($"DIAGNOSTICS_SUCCESS {tested} clip(s)");
