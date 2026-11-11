using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections;

var asm=Assembly.LoadFrom(@"D:\MazeLineGit\CompileValidationMonitoring\Bin\ShipCheckMonitor.dll");
var formType=asm.GetType("ShipCheckMonitor.MainForm")!;
var queryType=asm.GetType("ShipCheckMonitor.QueryResult")!;
var runType=asm.GetType("ShipCheckMonitor.RunResult")!;
const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
foreach(string key in new[]{"valid-key",""}){
 var form=RuntimeHelpers.GetUninitializedObject(formType);
 var query=JsonSerializer.Deserialize(JsonSerializer.Serialize(new{StateKey=key,UpToDate=false,Changes=new[]{new{Path=@"C:\fixture\Foo.cpp",Affects=true}}}),queryType)!;
 var run=JsonSerializer.Deserialize(JsonSerializer.Serialize(new{StateKey=key,Result="Fail",Reason="Build failed",StartTime=DateTimeOffset.Now.ToString("O")}),runType)!;
 var history=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(runType))!;history.Add(run);
 void Field(string name,object? value)=>formType.GetField(name,flags)!.SetValue(form,value);
 Field("_project",@"C:\fixture\Game.uproject");Field("_query",query);Field("_history",history);Field("_shownRun",run);
 var health=formType.GetMethod("ComputeHealth",flags)!.Invoke(form,null);
 Console.WriteLine($"StateKey={(key==""?"EMPTY":key)}, LatestResult=Fail, GUIHealth={health}");
}
