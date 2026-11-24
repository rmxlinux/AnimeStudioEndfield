using AnimeStudio;

const string unity="C:/Unity/2021.3/Editor/Unity.exe";
const string logPath="C:/cache/unity.log";
void Check(string log, params string[] expected)
{
    var message=UnityHumanoidBakeDiagnostics.DescribeFailure(1,log,unity,logPath);
    foreach(var text in expected)
        if(!message.Contains(text)) throw new Exception("Missing diagnostic: "+text+" in "+message);
    if(!message.Contains(unity) || !message.Contains(logPath)) throw new Exception("Lost diagnostic paths");
}
Check("No valid Unity Editor license found. Please activate your license.","no valid license","Unity Hub","Cached clips");
Check("No valid Unity license found.","no valid license");
// These warnings also occur in healthy Unity logs; they are not a license rejection.
var warnings="Error: LicensingClient has failed validation; ignoring\nError: Access token is unavailable; failed to update\n";
Check(warnings+"Assets/Editor/Bake.cs(1,1): error CS1002: ; expected\nScripts have compiler errors.","could not compile","CS1002");
Check(warnings+"Exception: Original HumanDescription did not build a valid Humanoid Avatar.","Original HumanDescription");
Check("It looks like another Unity instance is running with this project open.","already open","Close that project");
Check("", "did not produce a confirmed Humanoid bake");
if(args.Length>0) Check(File.ReadAllText(args[0]),"no valid license","before the Humanoid bake ran");
Console.WriteLine("BAKE_DIAGNOSTICS_PASS: license rejection, unrelated licensing warnings, compiler failure, bake exception, project lock, missing log"+(args.Length>0?", actual jsspsi failure log":""));
