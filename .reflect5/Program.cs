using System;using System.IO;using System.Linq;using System.Reflection;
class P{static void Main(){
 string dir=@"C:\Program Files\Autodesk\Revit 2027";
 var asm=Directory.GetFiles(dir,"*.dll").ToList();
 asm.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"*.dll"));
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asm.Distinct()));
 var api=mlc.LoadFromAssemblyPath(Path.Combine(dir,"RevitAPI.dll"));
 foreach(var tn in new[]{"Autodesk.Revit.DB.Structure.Rebar","Autodesk.Revit.DB.Structure.RebarInSystem"}){
   var t=api.GetType(tn); Console.WriteLine("== "+tn+" (View visibility) ==");
   foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).OrderBy(m=>m.Name))
     if(m.Name.Contains("View")||m.Name.Contains("Solid")||m.Name.Contains("Unobscured")||m.Name.Contains("Hidden")||m.Name.Contains("Visib"))
       Console.WriteLine("  "+m.ReturnType.Name+" "+m.Name+"("+string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name))+")");
 }
}}
