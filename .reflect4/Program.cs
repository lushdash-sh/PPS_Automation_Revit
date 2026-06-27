using System;using System.IO;using System.Linq;using System.Reflection;
class P{static void Main(){
 string dir=@"C:\Program Files\Autodesk\Revit 2027";
 var asm=Directory.GetFiles(dir,"*.dll").ToList();
 asm.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"*.dll"));
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asm.Distinct()));
 var api=mlc.LoadFromAssemblyPath(Path.Combine(dir,"RevitAPI.dll"));
 var fi=api.GetType("Autodesk.Revit.DB.FamilyInstance");
 Console.WriteLine("== FamilyInstance.GetReferences ==");
 foreach(var m in fi.GetMethods(BindingFlags.Public|BindingFlags.Instance).Where(m=>m.Name.Contains("Reference")))
   Console.WriteLine("  "+m.ReturnType.Name+" "+m.Name+"("+string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name))+")");
 var en=api.GetType("Autodesk.Revit.DB.FamilyInstanceReferenceType");
 Console.WriteLine("== FamilyInstanceReferenceType values ==");
 if(en!=null) foreach(var n in Enum.GetNames(en)) Console.WriteLine("  "+n);
}}
