using AnimeStudio;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;

if(args.Length<2 || args.Length>3) throw new ArgumentException("HumanoidTest <bundle directory> <output directory> [validated sit_start.human.json: three-bundle mode]");
Directory.CreateDirectory(args[1]);
var assets=new AssetsManager {Game=GameManager.GetGame("ArknightsEndfield"),ResolveDependencies=false};
string[] hashes={"572d0eab779b49b517f53cca","0b44582beca1783c9a8f9732","e65095157cbb12c448a558ce",
"99fedbfa0c84c4d4223e5110","836dcdc437b098e966f0d00f","6f01581d28cfaf7d3d05dc1a","b07f4bd2e1fe8f248eab2b0f",
"3e06c1723abe10728dc68a80","4ae0a159a84feac066425c51","3da5f1779ed9fcf8d33790b9","e96b1ed7d3c60286b0b624cf","9be0b16f3ce13e7ac50b0c1f"};
var single=args.Length==3;
var bundleDirectory=Directory.Exists(Path.Combine(args[0],"main"))?Path.Combine(args[0],"main"):args[0];
assets.LoadFiles((single?hashes.Take(3):hashes).Select(h=>Path.Combine(bundleDirectory,h+".ab")).ToArray());
var objects=assets.assetsFileList.SelectMany(f=>f.Objects).ToArray();
var clips=objects.OfType<AnimationClip>().Where(c=>c.m_Name.StartsWith("A_actor_wulfa_interact_")).OrderBy(c=>c.m_Name).ToArray();
var avatar=objects.OfType<Avatar>().Single();
var root=objects.OfType<GameObject>().Single(g=>g.m_Name=="chr_0028_wulfa_postmodel");
if(clips.Length!=(single?1:11)) throw new Exception($"Unexpected Wulfa clip count: {clips.Length}.");
var sit=clips.Single(c=>c.m_Name.EndsWith("sit_start"));
var decoded=EndfieldClipDecoder.Decode(sit);
if(decoded.Count(t=>t.Binding.typeID!=ClassIDType.Transform)!=151 || decoded.Count(t=>t.Binding.typeID==ClassIDType.Animator && t.Binding.customType==8)!=143)
    throw new Exception("Missing scalar/Humanoid channels.");
var yaml=AnimationClipConverter.Process(sit);
if(yaml.Floats.Count!=151 || yaml.Rotations.Count<300) throw new Exception("YAML curve expansion failed.");
Console.WriteLine($"YAML: {yaml.Floats.Count} scalar, {yaml.Rotations.Count} rotation curves");
try {EndfieldHumanoidBaker.Bake(null,new[]{sit});throw new Exception("Missing Avatar was accepted.");}
catch(EndfieldHumanoidException){Console.WriteLine("Missing Avatar rejected.");}
var savedCount=sit.m_AclCompressedBuffer.FloatCurveCount;
try {sit.m_AclCompressedBuffer.FloatCurveCount--;EndfieldClipDecoder.Decode(sit);throw new Exception("Bad scalar count accepted.");}
catch(InvalidDataException){Console.WriteLine("Malformed scalar count rejected.");}
finally {sit.m_AclCompressedBuffer.FloatCurveCount=savedCount;}

var options=new ModelConverter.Options {game=assets.Game,collectAnimations=false,exportMaterials=false,
    materials=new HashSet<Material>(),uvs=Enumerable.Range(0,8).ToDictionary(i=>"UV"+i,i=>(true,0)),texs=new Dictionary<string,int>()};
var converter=new ModelConverter(root,options,clips);
if(converter.AnimationList.Count!=clips.Length) throw new Exception("Lost clips during model conversion.");
if(single)
{
    var reference=JObject.Parse(File.ReadAllText(args[2]));
    if((string)reference["humanoidLayout"]!="endfield-61body-40fingers-v1") throw new Exception("Reference bake uses an obsolete muscle layout");
    var referenceBones=reference["bones"].ToObject<string[]>();
    var referenceTimes=reference["times"].ToObject<float[]>();
    var referenceValues=reference["values"].ToObject<float[]>();
    var baked=EndfieldHumanoidBaker.Bake(avatar,new[]{sit})[sit];
    if(!baked.times.SequenceEqual(referenceTimes)) throw new Exception("Reference sample times differ");
    double maxAngle=0,maxPosition=0;
    for(int f=0;f<baked.times.Length;f++)
    for(int k=0;k<referenceBones.Length;k++)
    {
        int target=Array.IndexOf(baked.bones,referenceBones[k]);
        if(target<0) throw new Exception("Missing reference bone "+referenceBones[k]);
        int a=(f*referenceBones.Length+k)*10,b=(f*baked.bones.Length+target)*10;
        double dot=0,n1=0,n2=0,distance=0;
        for(int j=0;j<3;j++) distance+=Math.Pow(referenceValues[a+j]-baked.values[b+j],2);
        for(int j=3;j<7;j++){dot+=(double)referenceValues[a+j]*baked.values[b+j];n1+=(double)referenceValues[a+j]*referenceValues[a+j];n2+=(double)baked.values[b+j]*baked.values[b+j];}
        maxAngle=Math.Max(maxAngle,Math.Acos(Math.Min(1,Math.Abs(dot)/Math.Sqrt(n1*n2)))*360/Math.PI);
        maxPosition=Math.Max(maxPosition,Math.Sqrt(distance));
    }
    if(maxAngle>0.12 || maxPosition>0.0002) throw new Exception($"Baker disagrees with validated pose: {maxAngle} degrees, {maxPosition} metres");
    File.WriteAllText(Path.Combine(args[1],"validated_pose_comparison.json"),JsonConvert.SerializeObject(new{samples=baked.times.Length,bones=referenceBones.Length,maxAngle,maxPosition},Formatting.Indented));
    Console.WriteLine($"VALIDATED_POSE_PASS {baked.times.Length} samples x {referenceBones.Length} bones: maxAngle={maxAngle:G6}, maxPosition={maxPosition:G6}");
}
var report=new List<object>();
foreach(var clip in clips)
{
    var animation=converter.AnimationList.Single(a=>a.Name==clip.m_Name);
    foreach(var h in avatar.m_HumanDescription.m_Human)
    {
        var track=animation.TrackList.SingleOrDefault(t=>t.Path?.EndsWith("/"+h.m_BoneName)==true);
        if(track==null || track.Rotations.Count<2) throw new Exception("Missing human rotation: "+h.m_BoneName);
    }
    foreach(var source in EndfieldClipDecoder.Decode(clip).Where(t=>t.Binding.typeID==ClassIDType.Transform && t.Binding.attribute==2))
    {
        var bone=avatar.m_TOS.TryGetValue(source.Binding.path,out var path)?path.Split('/').Last():null;
        if(bone==null) continue;
        if(!animation.TrackList.Any(t=>t.Path?.EndsWith("/"+bone)==true && t.Rotations.Count>0))
            throw new Exception("Original QVV rotation was lost: "+bone);
    }
    var moving=avatar.m_HumanDescription.m_Human.Count(h=>{
        var t=animation.TrackList.Single(t=>t.Path?.EndsWith("/"+h.m_BoneName)==true);
        var first=t.Rotations[0].value;
        return t.Rotations.Any(k=>Math.Abs(k.value.X-first.X)+Math.Abs(k.value.Y-first.Y)+Math.Abs(k.value.Z-first.Z)>0.00001);
    });
    var varyingMuscles=EndfieldClipDecoder.Decode(clip).Count(t=>t.Binding.typeID==ClassIDType.Animator && t.Binding.customType==8 &&
        t.Binding.attribute>=42 && t.Binding.attribute<143 && t.Values.Max()-t.Values.Min()>0.01f);
    // Pose clips can be intentionally static; require motion only when the input contains it.
    if(varyingMuscles>10 && moving==0) throw new Exception($"{clip.m_Name} lost motion from {varyingMuscles} animated muscles.");
    if(clip==sit && moving<20) throw new Exception("sit_start lost body motion.");
    report.Add(new {clip=clip.m_Name,tracks=animation.TrackList.Count,movingHumanBones=moving,varyingMuscles});
    Console.WriteLine($"PASS {clip.m_Name}: {animation.TrackList.Count} tracks, {moving} moving human bones");
}
var sitAnimation=converter.AnimationList.Single(a=>a.Name==sit.m_Name);
File.WriteAllText(Path.Combine(args[1],"sit_start_expected.json"),JsonConvert.SerializeObject(sitAnimation));
File.WriteAllText(Path.Combine(args[1],"regression.json"),JsonConvert.SerializeObject(new{clips=report,scalarTracks=151,humanoidTracks=143,modelMeshes=converter.MeshList.Count},Formatting.Indented));
ModelExporter.ExportFbx(Path.Combine(args[1],single?"wulfa_sit_start.fbx":"wulfa_all11.fbx"),converter,new Fbx.ExportOptions {
    exportAllNodes=true,exportSkins=true,exportAnimations=true,exportBlendShape=true,castToBone=false,boneSize=1,scaleFactor=1,fbxVersion=3 });
Console.WriteLine("HUMANOID_REGRESSION_SUCCESS");
