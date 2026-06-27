using System;using System.IO;using System.Linq;using System.Reflection;
class P{static void Main(){
 string dir=@"C:\Program Files\Autodesk\Revit 2027";
 var asm=Directory.GetFiles(dir,"*.dll").ToList();
 asm.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"*.dll"));
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asm.Distinct()));
 var api=mlc.LoadFromAssemblyPath(Path.Combine(dir,"RevitAPI.dll"));
 var vs=api.GetType("Autodesk.Revit.DB.ViewSection");
 Console.WriteLine("== ViewSection.CreateReferenceSection / CreateSection ==");
 foreach(var m in vs.GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name.Contains("Reference")||m.Name=="CreateSection"))
   Console.WriteLine("  "+m.ReturnType.Name+" "+m.Name+"("+string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name))+")");
 var bip=api.GetType("Autodesk.Revit.DB.BuiltInParameter");
 Console.WriteLine("== BIP DETAIL_NUMBER / VIEW_NAME ==");
 foreach(var n in Enum.GetNames(bip)) if(n.Contains("DETAIL_NUMBER")||n.Contains("VIEWER_DETAIL")||n.Contains("VIEW_NAME")) Console.WriteLine("  "+n);
}}
