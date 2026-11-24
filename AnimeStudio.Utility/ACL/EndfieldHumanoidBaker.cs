using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace AnimeStudio
{
    public sealed class EndfieldHumanoidException : Exception
    {
        public EndfieldHumanoidException(string message) : base(message) { }
        public EndfieldHumanoidException(string message, Exception inner) : base(message, inner) { }
    }

    public static class EndfieldHumanoidBaker
    {
        public sealed class BakedClip
        {
            public string name;
            public string[] bones;
            public float[] times, values;
            public float humanScale;
        }
        private sealed class Output { public BakedClip[] clips; }

        public static bool NeedsBake(AnimationClip clip) => EndfieldClipDecoder.IsCompressed(clip) &&
            clip.m_ClipBindingConstant.genericBindings.Any(b => b.typeID == ClassIDType.Animator && b.customType == 8);

        private static object V(Vector3 v) => new { x=v.X, y=v.Y, z=v.Z };
        private static object Q(Quaternion q) => new { x=q.X, y=q.Y, z=q.Z, w=q.W };

        public static Dictionary<AnimationClip, BakedClip> Bake(Avatar avatar, IEnumerable<AnimationClip> source)
        {
            var clips=source.Where(NeedsBake).ToArray();
            var result=new Dictionary<AnimationClip,BakedClip>();
            if(clips.Length==0) return result;
            try
            {
                var description=avatar?.m_HumanDescription;
                if(description == null || description.m_Human == null || description.m_Human.Count < 1 || description.m_Skeleton == null || description.m_Skeleton.Count < 1)
                    throw new EndfieldHumanoidException("Endfield Humanoid export requires the model's original Avatar. Load its model/Avatar bundle together with the animation bundle; exporting clothes alone is not a complete animation.");
                if(description.m_HasTranslationDoF)
                    throw new EndfieldHumanoidException("This Avatar uses translation DoF, which is not supported by the current Humanoid baker. The original scalar curves can still be exported as .anim/JSON.");
                var skeleton=description.m_Skeleton.ToDictionary(b=>b.m_Name);
                var humanNames=new HashSet<string>(description.m_Human.Select(h=>h.m_BoneName));
                // Include intermediate nonhuman bones, e.g. the extra pelvis between hips and spine.
                var outputNames=new HashSet<string>(humanNames);
                foreach(var name in humanNames)
                {
                    var bone=skeleton[name];
                    var visited=new HashSet<string>();
                    while(!string.IsNullOrEmpty(bone.m_ParentName))
                    {
                        if(!visited.Add(bone.m_ParentName)) throw new InvalidDataException("Cyclic Avatar skeleton.");
                        bone=skeleton[bone.m_ParentName];
                        if(humanNames.Contains(bone.m_Name)) break;
                        if(!string.IsNullOrEmpty(bone.m_ParentName)) outputNames.Add(bone.m_Name);
                    }
                }
                // Exclude ancestors above Hips: they carry model/root transforms, not body channels.
                var hips=description.m_Human.Single(h=>h.m_HumanName=="Hips").m_BoneName;
                for(var bone=skeleton[hips]; !string.IsNullOrEmpty(bone.m_ParentName);)
                { bone=skeleton[bone.m_ParentName]; outputNames.Remove(bone.m_Name); }
                var outputs=description.m_Skeleton.Where(b=>outputNames.Contains(b.m_Name)).Select(b=>b.m_Name).ToArray();
                var requests=new List<object>();
                for(int index=0; index<clips.Length; index++)
                {
                    var tracks=EndfieldClipDecoder.Decode(clips[index]).Where(t=>t.Binding.typeID==ClassIDType.Animator && t.Binding.customType==8).ToArray();
                    var times=tracks[0].Times;
                    if(tracks.Any(t=>!t.Times.SequenceEqual(times))) throw new InvalidDataException("Humanoid channels have different sample times.");
                    var attributes=new HashSet<int>();
                    foreach(var t in tracks)
                    {
                        int sourceAttribute = checked((int)t.Binding.attribute);
                        // Translation DoF is checked explicitly and then kept
                        // out of the HumanPose request; silently moving it
                        // into a finger channel would corrupt the bake.
                        if(sourceAttribute >= 143)
                        {
                            if(t.Values.Any(v=>Math.Abs(v)>0.0001f))
                                throw new EndfieldHumanoidException($"{clips[index].m_Name}: nonzero translation DoF is unsupported; export .anim/JSON to preserve these channels.");
                            continue;
                        }
                        // The six Endfield-only foot/toe axes have no stock
                        // Unity HumanPose slot.  They must be omitted rather
                        // than shifted into the next leg/arm muscle.
                        if(!EndfieldHumanoidLayout.TryMapToUnity(sourceAttribute, out var attribute))
                        {
                            var maxValue=t.Values.Max(v=>Math.Abs(v));
                            if(maxValue>0.0001f)
                                Logger.Warning($"{clips[index].m_Name}: {EndfieldHumanoidLayout.ToProperty(sourceAttribute,t.Binding.version)} (max |value|={maxValue:G6}) has no stock Unity muscle slot and is omitted from the FBX bake. Original values are preserved in .anim/JSON.");
                            continue;
                        }
                        if(!attributes.Add(attribute)) throw new InvalidDataException("Duplicate Humanoid channel after Endfield layout mapping.");
                        // Preserve source root and TDoF channels in .anim/JSON; don't silently approximate in FBX.
                        if(attribute<7 && t.Values.Any(v=>Math.Abs(v-(attribute==6?1f:0f))>0.0001f))
                            throw new EndfieldHumanoidException($"{clips[index].m_Name}: nonidentity Motion channels require root-motion baking; FBX was stopped to prevent an incomplete export.");
                    }
                    if(Enumerable.Range(7,7).Any(a=>!attributes.Contains(a)))
                        throw new InvalidDataException("Humanoid Root position/quaternion channels are missing.");
                    requests.Add(new { name=index.ToString(), times,
                        channels=tracks.Select(t =>
                        {
                            int sourceAttribute = checked((int)t.Binding.attribute);
                            if(sourceAttribute >= 143)
                                return new { include=false, attribute=-1, property=string.Empty, values=t.Values };
                            return EndfieldHumanoidLayout.TryMapToUnity(sourceAttribute, out var attribute)
                                ? new { include=true, attribute, property=EndfieldHumanoidLayout.ToUnityProperty(sourceAttribute, t.Binding.version), values=t.Values }
                                : new { include=false, attribute=-1, property=string.Empty, values=t.Values };
                        }).Where(x=>x.include).Select(x=>new { x.attribute, x.property, x.values }).ToArray() });
                }
                var input=new {
                    skeleton=description.m_Skeleton.Select(b=>new {name=b.m_Name,parent=b.m_ParentName,position=V(b.m_Position),rotation=Q(b.m_Rotation),scale=V(b.m_Scale)}).ToArray(),
                    human=description.m_Human.Select(h=>new { boneName=h.m_BoneName,humanName=h.m_HumanName,
                        limit=new {useDefaultValues=!h.m_Limit.m_Modified,min=V(h.m_Limit.m_Min),max=V(h.m_Limit.m_Max),center=V(h.m_Limit.m_Value),axisLength=h.m_Limit.m_Length} }).ToArray(),
                    outputBones=outputs, upperArmTwist=description.m_ArmTwist,lowerArmTwist=description.m_ForeArmTwist,
                    upperLegTwist=description.m_UpperLegTwist,lowerLegTwist=description.m_LegTwist,
                    armStretch=description.m_ArmStretch,legStretch=description.m_LegStretch,feetSpacing=description.m_FeetSpacing,
                    humanScale=avatar.m_Avatar.m_Human.m_Scale,clips=requests.ToArray()
                };
                var payload=JsonConvert.SerializeObject(input);
                var assembly=typeof(EndfieldHumanoidBaker).Assembly;
                using var stream=assembly.GetManifestResourceStream("AnimeStudio.UnityHumanoidBake.cs");
                using var reader=new StreamReader(stream ?? throw new FileNotFoundException("Embedded Unity baker is missing."));
                var script=reader.ReadToEnd();
                var unity=FindUnity();
                var engineVersion=FileVersionInfo.GetVersionInfo(unity).FileVersion ?? "unknown";
                var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload+script+engineVersion)));
                var project=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AnimeStudio","HumanoidCache",key);
                Directory.CreateDirectory(project);
                var outputPath=Path.Combine(project,"output.json");
                using var mutex=new Mutex(false,"Local\\AnimeStudioHumanoid_"+key);
                bool acquired=false;
                try
                {
                    try { acquired=mutex.WaitOne(TimeSpan.FromMinutes(10)); } catch(AbandonedMutexException) { acquired=true; }
                    if(!acquired) throw new TimeoutException("Timed out waiting for the Humanoid bake cache.");
                    if(!File.Exists(outputPath))
                    {
                        var editor=Path.Combine(project,"Assets","Editor");Directory.CreateDirectory(editor);
                        Directory.CreateDirectory(Path.Combine(project,"ProjectSettings"));
                        Directory.CreateDirectory(Path.Combine(project,"Packages"));
                        var editorFolder = Directory.GetParent(Path.GetDirectoryName(unity)).Name;
                        File.WriteAllText(Path.Combine(project,"ProjectSettings","ProjectVersion.txt"),"m_EditorVersion: "+editorFolder+"\n");
                        File.WriteAllText(Path.Combine(project,"Packages","manifest.json"),"{\"dependencies\":{\"com.unity.modules.animation\":\"1.0.0\",\"com.unity.modules.jsonserialize\":\"1.0.0\"}}");
                        File.WriteAllText(Path.Combine(editor,"AnimeStudioHumanoidBake.cs"),script);
                        File.WriteAllText(Path.Combine(project,"input.json"),payload);
                        var pendingPath=Path.Combine(project,"output.pending.json");
                        if(File.Exists(pendingPath)) File.Delete(pendingPath);
                        var log=Path.Combine(project,"unity.log");
                        var start=new ProcessStartInfo(unity) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                        foreach(var argument in new[]{"-batchmode","-nographics","-quit","-projectPath",project,"-executeMethod","AnimeStudioHumanoidBake.Run","-logFile",log}) start.ArgumentList.Add(argument);
                        start.Environment["ANIMESTUDIO_BAKE_INPUT"]=Path.Combine(project,"input.json");
                        start.Environment["ANIMESTUDIO_BAKE_OUTPUT"]=pendingPath;
                        Logger.Info($"Baking Endfield Humanoid clips: {string.Join(", ", clips.Select(c=>c.m_Name))}. Unity: {unity}. Log: {log}");
                        using var process=Process.Start(start) ?? throw new IOException("Unity could not start.");
                        if(!process.WaitForExit(600000)) {process.Kill(true);throw new TimeoutException($"Unity Humanoid bake timed out. Log: {log}");}
                        var logText=File.Exists(log)?File.ReadAllText(log):string.Empty;
                        if(process.ExitCode!=0 || !File.Exists(pendingPath) || !logText.Contains("ANIMESTUDIO_HUMANOID_SUCCESS"))
                            throw new EndfieldHumanoidException(UnityHumanoidBakeDiagnostics.DescribeFailure(process.ExitCode,logText,unity,log));
                        ValidateOutput(pendingPath,clips.Length,outputs);
                        File.Move(pendingPath,outputPath);
                    }
                    else Logger.Info($"Using cached Endfield Humanoid bake: {string.Join(", ", clips.Select(c=>c.m_Name))}.");
                    var baked=ValidateOutput(outputPath,clips.Length,outputs);
                    for(int i=0;i<clips.Length;i++) result.Add(clips[i],baked.clips.Single(c=>c.name==i.ToString()));
                }
                finally { if(acquired) mutex.ReleaseMutex(); }
                return result;
            }
            catch(EndfieldHumanoidException) {throw;}
            catch(Exception ex) when(ex is not OutOfMemoryException)
            {throw new EndfieldHumanoidException("Endfield Humanoid bake failed; incomplete FBX export was stopped. "+ex.Message,ex);}
        }

        private static Output ValidateOutput(string path,int count,string[] bones)
        {
            var output=JsonConvert.DeserializeObject<Output>(File.ReadAllText(path));
            if(output?.clips?.Length!=count) throw new InvalidDataException("Unity returned an incomplete clip batch.");
            foreach(var clip in output.clips)
                if(clip.bones==null || !clip.bones.SequenceEqual(bones) || clip.times==null || clip.times.Length==0 ||
                   clip.values==null || clip.values.Length!=checked(clip.times.Length*bones.Length*10) ||
                   clip.values.Any(v=>!float.IsFinite(v)) || clip.times.Any(v=>!float.IsFinite(v)))
                    throw new InvalidDataException("Invalid Humanoid bake output.");
            return output;
        }

        private static string FindUnity()
        {
            var configured=Environment.GetEnvironmentVariable("ANIMESTUDIO_UNITY_EDITOR");
            if(!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured)?Path.GetFullPath(configured):throw new FileNotFoundException("ANIMESTUDIO_UNITY_EDITOR points to a missing Unity.exe.",configured);
            var hub=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Unity","Hub","Editor");
            var candidates=Directory.Exists(hub)?Directory.GetDirectories(hub).OrderByDescending(x=>x).Select(x=>Path.Combine(x,"Editor","Unity.exe")).Where(File.Exists).ToArray():Array.Empty<string>();
            var selected=candidates.FirstOrDefault(x=>x.Contains("2021.3.")) ?? candidates.FirstOrDefault();
            return selected ?? throw new EndfieldHumanoidException("Humanoid FBX export requires a licensed Unity Editor (tested: 2021.3). Install it through Unity Hub or set ANIMESTUDIO_UNITY_EDITOR to Unity.exe. Scalar .anim/JSON export does not require Unity.");
        }
    }
}
