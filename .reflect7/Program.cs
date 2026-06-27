using System;using System.IO;using System.Linq;using System.Reflection;
class P{static void Main(){
 string dir=@"C:\Program Files\Autodesk\Revit 2027";
 var asm=Directory.GetFiles(dir,"*.dll").ToList();
 asm.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"*.dll"));
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asm.Distinct()));
 var api=mlc.LoadFromAssemblyPath(Path.Combine(dir,"RevitAPI.dll"));
 var v=api.GetType("Autodesk.Revit.DB.View");
 Console.WriteLine("== View Hide/Isolate methods ==");
 foreach(var m in v.GetMethods(BindingFlags.Public|BindingFlags.Instance).OrderBy(m=>m.Name))
   if(m.Name.Contains("Isolate")||m.Name.Contains("Hide")||m.Name.Contains("Temporary")||m.Name.Contains("Unhide"))
     Console.WriteLine("  "+m.ReturnType.Name+" "+m.Name+"("+string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name))+")");
}}
