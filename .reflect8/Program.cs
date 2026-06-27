using System;using System.IO;using System.Linq;using System.Reflection;
class P{static void Main(){
 string dir=@"C:\Program Files\Autodesk\Revit 2027";
 var asm=Directory.GetFiles(dir,"*.dll").ToList();
 asm.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"*.dll"));
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asm.Distinct()));
 var api=mlc.LoadFromAssemblyPath(Path.Combine(dir,"RevitAPI.dll"));
 foreach(var tn in new[]{"Autodesk.Revit.DB.Structure.Rebar","Autodesk.Revit.DB.Structure.RebarInSystem","Autodesk.Revit.DB.Structure.RebarHostData"}){
   var t=api.GetType(tn); if(t==null){Console.WriteLine("NF "+tn);continue;}
   Console.WriteLine("== "+tn+" (Host) ==");
   foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).Where(m=>m.Name.Contains("Host")))
     Console.WriteLine("  "+m.ReturnType.Name+" "+m.Name+"("+string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name))+")");
   foreach(var pr in t.GetProperties(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(p=>p.Name.Contains("Host")))
     Console.WriteLine("  P "+pr.PropertyType.Name+" "+pr.Name);
 }
}}
